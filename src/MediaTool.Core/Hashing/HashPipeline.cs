using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Hashing;

public sealed class HashProgress
{
    public string Stage = "";
    public long FilesDone;
    public long FilesTotal;
    public long BytesRead;
    public long Failures;
    public TimeSpan Elapsed;
}

public sealed class HashStats
{
    public long ProbedFiles;
    public long ProbedBytes;
    public long FullyHashedFiles;
    public long FullyHashedBytes;
    public long Failures;
    public long SkippedOfflineVolumes;
    public List<string> OfflineVolumeNames = [];
}

/// <summary>
/// Stages 2 and 3 of the exact-duplicate cascade.
///
/// Stage 1 (size collision) is free: it is a GROUP BY over the catalog and reads nothing.
/// Stage 2 probes 64KB from each end of every survivor. Stage 3 fully hashes only the files
/// whose probe still collides. Each stage is fed exclusively by the previous one, so the
/// bytes actually pulled off disk are a small fraction of the library.
/// </summary>
public sealed class HashPipeline
{
    private const int PageSize = 20_000;

    private readonly CatalogDatabase _db;
    private readonly HashStats _stats = new();

    public Action<HashProgress>? OnProgress { get; set; }

    public HashPipeline(CatalogDatabase db) => _db = db;

    public async Task<HashStats> RunAsync(CancellationToken ct)
    {
        var mountPoints = ResolveOnlineVolumes();

        await RunStageAsync(Stage.Probe, mountPoints, ct).ConfigureAwait(false);
        await RunStageAsync(Stage.Full, mountPoints, ct).ConfigureAwait(false);

        return _stats;
    }

    private enum Stage { Probe, Full }

    /// <summary>
    /// Maps each catalogued volume to where it is reachable right now.
    ///
    /// Resolved by GUID every run, never from the stored mount point: the whole reason the
    /// catalog is keyed on GUIDs is that E: today may be F: tomorrow. A volume that is not
    /// currently attached is reported and skipped, not treated as missing data.
    /// </summary>
    private Dictionary<long, string> ResolveOnlineVolumes()
    {
        var attached = VolumeScanner.EnumerateVolumes()
            .ToDictionary(v => v.VolumeGuid, StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<long, string>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT volume_id, volume_guid, label, last_mount_point FROM volumes";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string guid = reader.GetString(1);
            string name = reader.IsDBNull(2) || reader.GetString(2).Length == 0
                ? (reader.IsDBNull(3) ? guid : reader.GetString(3))
                : reader.GetString(2);

            if (attached.TryGetValue(guid, out var volume) && volume.PrimaryMountPoint is { } mount)
                result[id] = mount.EndsWith('\\') ? mount : mount + '\\';
            else
                _stats.OfflineVolumeNames.Add(name);
        }

        return result;
    }

    private async Task RunStageAsync(Stage stage, Dictionary<long, string> mountPoints, CancellationToken ct)
    {
        BuildCandidateSet(stage);

        long total = CountCandidates(stage);
        if (total == 0) return;

        var progress = new HashProgress
        {
            Stage = stage == Stage.Probe ? "probe" : "full hash",
            FilesTotal = total,
        };
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        // Resolved up front: the shared connection is not thread-safe, and reading it from
        // inside the per-volume tasks would race with the writer below.
        var degrees = mountPoints.Keys.ToDictionary(id => id, DegreeOfParallelismFor);

        // One task per volume. Separate physical disks are independent spindles, so running
        // them together is a real win; the parallelism *within* a volume is what has to be
        // matched to the hardware.
        var results = new ConcurrentQueue<HashRow>();
        var volumeTasks = mountPoints.Select(kv => Task.Run(async () =>
        {
            long volumeId = kv.Key;
            string mount = kv.Value;
            int degree = degrees[volumeId];

            using var connection = _db.OpenSecondaryConnection();
            long cursor = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var page = FetchPage(connection, stage, volumeId, cursor);
                if (page.Count == 0) break;
                cursor = page[^1].FileKey;

                await Parallel.ForEachAsync(
                    page,
                    new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                    (task, _) =>
                    {
                        var row = Execute(stage, task, mount);
                        results.Enqueue(row);

                        Interlocked.Increment(ref progress.FilesDone);
                        Interlocked.Add(ref progress.BytesRead, row.BytesRead);
                        if (row.Failed) Interlocked.Increment(ref progress.Failures);

                        long now = clock.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref lastReport) >= 200)
                        {
                            Interlocked.Exchange(ref lastReport, now);
                            progress.Elapsed = clock.Elapsed;
                            OnProgress?.Invoke(progress);
                        }
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);

                FlushResults(results, stage);
            }
        }, ct)).ToList();

        await Task.WhenAll(volumeTasks).ConfigureAwait(false);
        FlushResults(results, stage);

        progress.Elapsed = clock.Elapsed;
        OnProgress?.Invoke(progress);
    }

    /// <summary>
    /// An HDD services one read head. Queueing sixteen readers against it turns a sequential
    /// scan into a seek storm and makes the stage several times slower, so the depth is
    /// taken from what the storage stack reported at scan time.
    /// </summary>
    private int DegreeOfParallelismFor(long volumeId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT storage_kind FROM volumes WHERE volume_id=@v";
        cmd.Parameters.AddWithValue("@v", volumeId);
        var kind = (StorageKind)Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        return kind switch
        {
            StorageKind.Ssd => Math.Min(8, Environment.ProcessorCount),
            StorageKind.Hdd => 2,
            StorageKind.Remote => 4,        // latency-bound, so some overlap helps
            _ => 2,                          // unknown: assume it can seek badly
        };
    }

    /// <summary>
    /// Materialises the set of sizes (stage 2) or size+probe pairs (stage 3) that still have
    /// a collision. Done once per stage into a temp table so the paged candidate query does
    /// not recompute the grouping on every page.
    /// </summary>
    private void BuildCandidateSet(Stage stage)
    {
        using var cmd = _db.Connection.CreateCommand();
        // Real tables, not TEMP: a TEMP table is private to the connection that made it, and
        // the per-volume readers below each run on their own connection.
        cmd.CommandText = stage == Stage.Probe
            ? """
              DROP TABLE IF EXISTS dup_sizes;
              CREATE TABLE dup_sizes AS
                  SELECT size FROM files WHERE present=1 GROUP BY size HAVING COUNT(*) > 1;
              CREATE INDEX ix_dup_sizes ON dup_sizes(size);
              """
            : """
              DROP TABLE IF EXISTS dup_probes;
              CREATE TABLE dup_probes AS
                  SELECT size, partial_hash FROM files
                  WHERE present=1 AND partial_hash IS NOT NULL
                  GROUP BY size, partial_hash HAVING COUNT(*) > 1;
              CREATE INDEX ix_dup_probes ON dup_probes(size, partial_hash);
              """;
        cmd.ExecuteNonQuery();
    }

    private long CountCandidates(Stage stage)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM files f {CandidateJoin(stage)} WHERE {CandidateWhere(stage)}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string CandidateJoin(Stage stage) => stage == Stage.Probe
        ? "JOIN dup_sizes d ON d.size = f.size"
        : "JOIN dup_probes d ON d.size = f.size AND d.partial_hash = f.partial_hash";

    private static string CandidateWhere(Stage stage) => stage == Stage.Probe
        // Re-probe anything whose bytes may have changed since it was last hashed.
        ? "f.present=1 AND (f.partial_hash IS NULL OR f.hashed_size <> f.size OR f.hashed_mtime <> f.mtime)"
        : "f.present=1 AND f.content_hash IS NULL";

    private readonly record struct HashTask(long FileKey, string RelPath, long Size, long Mtime);

    private readonly record struct HashRow(
        long FileKey, ulong Partial, bool HasPartial, byte[]? Content,
        long Size, long Mtime, long BytesRead, bool Failed);

    /// <summary>
    /// Keyset pagination on file_key. Bounded memory, and file_key happens to follow the
    /// order the crawler walked the tree — so reads stay within a directory at a time, which
    /// is the cheap access pattern on a spinning disk.
    /// </summary>
    private List<HashTask> FetchPage(SqliteConnection connection, Stage stage, long volumeId, long cursor)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.file_key, f.rel_path, f.size, f.mtime
            FROM files f {CandidateJoin(stage)}
            WHERE {CandidateWhere(stage)} AND f.volume_id = @vol AND f.file_key > @cursor
            ORDER BY f.file_key
            LIMIT {PageSize}
            """;
        cmd.Parameters.AddWithValue("@vol", volumeId);
        cmd.Parameters.AddWithValue("@cursor", cursor);

        var page = new List<HashTask>(PageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            page.Add(new HashTask(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        return page;
    }

    private HashRow Execute(Stage stage, HashTask task, string mountPoint)
    {
        string fullPath = mountPoint + task.RelPath;
        try
        {
            if (stage == Stage.Probe)
            {
                var probe = ContentHasher.Probe(fullPath, task.Size);
                long read = Math.Min(task.Size, 2L * ContentHasher.ProbeBytes);
                return new HashRow(task.FileKey, probe.Partial, true, probe.Content,
                                   task.Size, task.Mtime, read, false);
            }

            byte[] content = ContentHasher.Full(fullPath);
            return new HashRow(task.FileKey, 0, false, content, task.Size, task.Mtime, task.Size, false);
        }
        catch (Exception)
        {
            // Locked, deleted since the scan, or a bad sector. One unreadable file must not
            // stop a pass over hundreds of thousands of others.
            return new HashRow(task.FileKey, 0, false, null, task.Size, task.Mtime, 0, true);
        }
    }

    private readonly Lock _writeLock = new();

    private void FlushResults(ConcurrentQueue<HashRow> queue, Stage stage)
    {
        if (queue.IsEmpty) return;

        lock (_writeLock)
        {
            using var tx = _db.Connection.BeginTransaction();
            using var cmd = _db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = stage == Stage.Probe
                ? """
                  UPDATE files SET partial_hash=@partial,
                                   content_hash=COALESCE(@content, content_hash),
                                   hashed_size=@size, hashed_mtime=@mtime
                  WHERE file_key=@key
                  """
                : "UPDATE files SET content_hash=@content, hashed_size=@size, hashed_mtime=@mtime WHERE file_key=@key";

            cmd.Parameters.Add(new SqliteParameter("@key", DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@content", DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@size", DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@mtime", DBNull.Value));
            if (stage == Stage.Probe) cmd.Parameters.Add(new SqliteParameter("@partial", DBNull.Value));

            while (queue.TryDequeue(out var row))
            {
                if (row.Failed)
                {
                    _stats.Failures++;
                    continue;
                }

                cmd.Parameters["@key"].Value = row.FileKey;
                cmd.Parameters["@content"].Value = (object?)row.Content ?? DBNull.Value;
                cmd.Parameters["@size"].Value = row.Size;
                cmd.Parameters["@mtime"].Value = row.Mtime;
                if (stage == Stage.Probe)
                    cmd.Parameters["@partial"].Value = unchecked((long)row.Partial);

                cmd.ExecuteNonQuery();

                if (stage == Stage.Probe)
                {
                    _stats.ProbedFiles++;
                    _stats.ProbedBytes += row.BytesRead;
                }
                else
                {
                    _stats.FullyHashedFiles++;
                    _stats.FullyHashedBytes += row.BytesRead;
                }
            }

            tx.Commit();
        }
    }
}
