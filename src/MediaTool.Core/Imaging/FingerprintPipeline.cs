using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Imaging;

public sealed class FingerprintProgress
{
    public long Done;
    public long Total;
    public long Failed;
    public TimeSpan Elapsed;
    public string Current = "";
}

public sealed class FingerprintStats
{
    public long Decoded;
    public long Failed;
    public List<string> OfflineVolumeNames = [];
}

/// <summary>
/// The decode pass: tiers 2 and 3 of the cascade.
///
/// This is the expensive stage — every image has to be decoded, and no cheap filter can
/// exclude anything beforehand, because the whole point is to find files whose bytes differ.
/// It is therefore also the stage where each decode has to earn its cost: one pass yields
/// the exact-picture hash, both perceptual hashes and the verification thumbnail.
///
/// Unlike the byte cascade this is CPU-bound once the bytes are in memory, so the worker
/// count follows the core count rather than the disk — but still never exceeds what the
/// volume's storage can feed.
/// </summary>
public sealed class FingerprintPipeline
{
    private const int PageSize = 5_000;

    /// <summary>
    /// Results are committed on this interval rather than only at page boundaries.
    /// Decoding is slow enough that a 5000-file page is ten minutes of work, and losing ten
    /// minutes to an interruption — or showing a progress counter that only moves in jumps
    /// of 5000 — are both avoidable.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly CatalogDatabase _db;
    private readonly FingerprintStats _stats = new();

    public Action<FingerprintProgress>? OnProgress { get; set; }

    /// <summary>Re-decode files that previously failed. Off by default so retries are opt-in.</summary>
    public bool RetryFailures { get; set; }

    public FingerprintPipeline(CatalogDatabase db) => _db = db;

    public async Task<FingerprintStats> RunAsync(CancellationToken ct)
    {
        var mountPoints = ResolveOnlineVolumes();
        if (mountPoints.Count == 0) return _stats;

        long total = CountPending();
        if (total == 0) return _stats;

        var progress = new FingerprintProgress { Total = total };
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        var results = new ConcurrentQueue<FingerprintRow>();

        var degrees = mountPoints.Keys.ToDictionary(id => id, DegreeOfParallelismFor);

        var tasks = mountPoints.Select(kv => Task.Run(async () =>
        {
            long volumeId = kv.Key;
            string mount = kv.Value;

            using var connection = _db.OpenSecondaryConnection();
            long cursor = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var page = FetchPage(connection, volumeId, cursor);
                if (page.Count == 0) break;
                cursor = page[^1].FileKey;

                await Parallel.ForEachAsync(
                    page,
                    new ParallelOptions { MaxDegreeOfParallelism = degrees[volumeId], CancellationToken = ct },
                    (item, _) =>
                    {
                        results.Enqueue(Fingerprint(item, mount));

                        Interlocked.Increment(ref progress.Done);

                        if (_sinceFlush.Elapsed >= FlushInterval) Flush(results);

                        long now = clock.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref lastReport) >= 200)
                        {
                            Interlocked.Exchange(ref lastReport, now);
                            progress.Elapsed = clock.Elapsed;
                            progress.Failed = _stats.Failed;
                            OnProgress?.Invoke(progress);
                        }
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);

                Flush(results);
            }
        }, ct)).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Flush(results);

        progress.Elapsed = clock.Elapsed;
        progress.Failed = _stats.Failed;
        OnProgress?.Invoke(progress);

        return _stats;
    }

    /// <summary>
    /// Decoding is CPU work, but the bytes still have to come off the disk first. On an HDD
    /// the read is the limit and extra threads only add seeks; on an SSD the cores are.
    /// </summary>
    private int DegreeOfParallelismFor(long volumeId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT storage_kind FROM volumes WHERE volume_id=@v";
        cmd.Parameters.AddWithValue("@v", volumeId);
        var kind = (StorageKind)Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        return kind switch
        {
            StorageKind.Ssd => Environment.ProcessorCount,
            StorageKind.Hdd => Math.Min(4, Environment.ProcessorCount),
            _ => Math.Min(4, Environment.ProcessorCount),
        };
    }

    private Dictionary<long, string> ResolveOnlineVolumes()
    {
        var attached = VolumeScanner.EnumerateVolumes()
            .ToDictionary(v => v.VolumeGuid, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<long, string>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT volume_id, volume_guid, COALESCE(label, volume_guid) FROM volumes";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            if (attached.TryGetValue(reader.GetString(1), out var volume) &&
                volume.PrimaryMountPoint is { } mount)
                result[reader.GetInt64(0)] = mount.EndsWith('\\') ? mount : mount + '\\';
            else
                _stats.OfflineVolumeNames.Add(reader.GetString(2));
        }

        return result;
    }

    private string PendingPredicate =>
        RetryFailures
            ? "f.present=1 AND (f.decode_state=0 OR f.decode_state=2 OR f.decoded_mtime <> f.mtime)"
            : "f.present=1 AND (f.decode_state=0 OR (f.decode_state=1 AND f.decoded_mtime <> f.mtime))";

    private long CountPending()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM files f WHERE {PendingPredicate}";
        return (long)cmd.ExecuteScalar()!;
    }

    private readonly record struct PendingFile(long FileKey, string RelPath, long Mtime);

    private readonly record struct FingerprintRow(
        long FileKey, long Mtime, ImageFingerprint Fingerprint, bool Failed);

    private List<PendingFile> FetchPage(SqliteConnection connection, long volumeId, long cursor)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.file_key, f.rel_path, f.mtime FROM files f
            WHERE {PendingPredicate} AND f.volume_id=@vol AND f.file_key > @cursor
            ORDER BY f.file_key LIMIT {PageSize}
            """;
        cmd.Parameters.AddWithValue("@vol", volumeId);
        cmd.Parameters.AddWithValue("@cursor", cursor);

        var page = new List<PendingFile>(PageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            page.Add(new PendingFile(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        return page;
    }

    private static FingerprintRow Fingerprint(PendingFile file, string mountPoint)
    {
        try
        {
            var decoded = ImageDecoder.Decode(mountPoint + file.RelPath);
            return new FingerprintRow(file.FileKey, file.Mtime, PerceptualHash.Compute(decoded), false);
        }
        catch (Exception)
        {
            // A corrupt JPEG, a HEIC with no codec installed, a RAW format WIC does not know.
            // Recorded as a failure so the run reports it rather than silently under-covering.
            return new FingerprintRow(file.FileKey, file.Mtime, default, true);
        }
    }

    private readonly Lock _writeLock = new();
    private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();

    private void Flush(ConcurrentQueue<FingerprintRow> queue)
    {
        if (queue.IsEmpty) return;

        lock (_writeLock)
        {
            // Another worker may have drained the queue while this one waited for the lock.
            if (queue.IsEmpty) return;
            _sinceFlush.Restart();

            using var tx = _db.Connection.BeginTransaction();

            using var ok = _db.Connection.CreateCommand();
            ok.Transaction = tx;
            ok.CommandText = """
                UPDATE files SET pixel_hash=@pixel, dhash=@dhash, phash=@phash, thumb16=@thumb,
                                 img_width=@w, img_height=@h, contrast=@contrast,
                                 decode_state=1, decoded_mtime=@mtime
                WHERE file_key=@key
                """;
            foreach (string p in new[] { "@pixel", "@dhash", "@phash", "@thumb", "@w", "@h", "@contrast", "@mtime", "@key" })
                ok.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            using var failed = _db.Connection.CreateCommand();
            failed.Transaction = tx;
            failed.CommandText = "UPDATE files SET decode_state=2, decoded_mtime=@mtime WHERE file_key=@key";
            failed.Parameters.Add(new SqliteParameter("@mtime", DBNull.Value));
            failed.Parameters.Add(new SqliteParameter("@key", DBNull.Value));

            while (queue.TryDequeue(out var row))
            {
                if (row.Failed)
                {
                    failed.Parameters["@mtime"].Value = row.Mtime;
                    failed.Parameters["@key"].Value = row.FileKey;
                    failed.ExecuteNonQuery();
                    _stats.Failed++;
                    continue;
                }

                var f = row.Fingerprint;
                ok.Parameters["@pixel"].Value = f.PixelHash;
                ok.Parameters["@dhash"].Value = unchecked((long)f.DHash);
                ok.Parameters["@phash"].Value = unchecked((long)f.PHash);
                ok.Parameters["@thumb"].Value = f.Thumb16;
                ok.Parameters["@w"].Value = f.Width;
                ok.Parameters["@h"].Value = f.Height;
                ok.Parameters["@contrast"].Value = f.Contrast;
                ok.Parameters["@mtime"].Value = row.Mtime;
                ok.Parameters["@key"].Value = row.FileKey;
                ok.ExecuteNonQuery();
                _stats.Decoded++;
            }

            tx.Commit();
        }
    }
}
