using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Metadata;

public sealed class MetadataProgress
{
    public long Done;
    public long Total;
    public long WithExif;
    public TimeSpan Elapsed;
}

public sealed class MetadataStats
{
    public long Read;
    public long WithExif;
    public long WithCaptureTime;
    public long FromSidecar;
    public List<string> OfflineVolumeNames = [];
}

/// <summary>
/// Reads EXIF for catalogued files and caches it.
///
/// Separate from the decode pass because it is far cheaper — a few hundred KB from the front
/// of each file, no decoding — and because its results are needed *before* clustering, not
/// after. The capture timestamp is what tells two frames of a burst apart from two copies of
/// one photo, and that distinction has to be made while the clusters are being formed.
/// </summary>
public sealed class MetadataPipeline
{
    /// <summary>Matches PlanBuilder: rows stamped older than this are re-read.</summary>
    public const int MetadataVersion = 4;

    private const int PageSize = 20_000;

    private readonly CatalogDatabase _db;
    private readonly MetadataStats _stats = new();

    public Action<MetadataProgress>? OnProgress { get; set; }

    public MetadataPipeline(CatalogDatabase db) => _db = db;

    public async Task<MetadataStats> RunAsync(CatalogScope scope, CancellationToken ct)
    {
        var mounts = ResolveOnlineVolumes();
        if (mounts.Count == 0) return _stats;

        string predicate = $"f.present=1 AND f.meta_state < {MetadataVersion}{scope.ToSqlPredicate("f")}";

        long total;
        using (var count = _db.Connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM files f WHERE {predicate}";
            total = (long)count.ExecuteScalar()!;
        }
        if (total == 0) return _stats;

        var progress = new MetadataProgress { Total = total };
        var clock = Stopwatch.StartNew();
        long lastReport = 0;
        var results = new ConcurrentQueue<(long Key, ImageMetadata Meta)>();

        var tasks = mounts.Select(kv => Task.Run(async () =>
        {
            using var connection = _db.OpenSecondaryConnection();
            long cursor = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var page = FetchPage(connection, predicate, kv.Key, cursor);
                if (page.Count == 0) break;
                cursor = page[^1].Key;

                await Parallel.ForEachAsync(page,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    (item, _) =>
                    {
                        results.Enqueue((item.Key, ImageMetadataReader.Read(kv.Value + item.RelPath)));

                        Interlocked.Increment(ref progress.Done);
                        long now = clock.ElapsedMilliseconds;
                        if (now - Interlocked.Read(ref lastReport) >= 200)
                        {
                            Interlocked.Exchange(ref lastReport, now);
                            progress.Elapsed = clock.Elapsed;
                            progress.WithExif = _stats.WithExif;
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
        OnProgress?.Invoke(progress);
        return _stats;
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

    private static List<(long Key, string RelPath)> FetchPage(
        SqliteConnection connection, string predicate, long volumeId, long cursor)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.file_key, f.rel_path FROM files f
            WHERE {predicate} AND f.volume_id=@vol AND f.file_key > @cursor
            ORDER BY f.file_key LIMIT {PageSize}
            """;
        cmd.Parameters.AddWithValue("@vol", volumeId);
        cmd.Parameters.AddWithValue("@cursor", cursor);

        var page = new List<(long, string)>(PageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) page.Add((reader.GetInt64(0), reader.GetString(1)));
        return page;
    }

    private readonly Lock _writeLock = new();

    private void Flush(ConcurrentQueue<(long Key, ImageMetadata Meta)> queue)
    {
        if (queue.IsEmpty) return;

        lock (_writeLock)
        {
            if (queue.IsEmpty) return;

            using var tx = _db.Connection.BeginTransaction();
            using var cmd = _db.Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE files SET has_exif=@e, exif_tags=@t, date_taken=@d, camera=@c,
                                 has_gps=@g, jpeg_quality=@q, meta_bytes=@b, sub_sec=@ss,
                                 sidecar_source=@src, sidecar_path=@sp,
                                 meta_state={MetadataVersion}
                WHERE file_key=@k
                """;
            foreach (string p in new[] { "@e", "@t", "@d", "@c", "@g", "@q", "@b", "@ss", "@src", "@sp", "@k" })
                cmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

            while (queue.TryDequeue(out var item))
            {
                var meta = item.Meta;
                cmd.Parameters["@e"].Value = meta.HasExif ? 1 : 0;
                cmd.Parameters["@t"].Value = meta.TagCount;
                cmd.Parameters["@d"].Value = meta.DateTaken is { } d
                    ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds()
                    : DBNull.Value;
                cmd.Parameters["@c"].Value = (object?)meta.Camera ?? DBNull.Value;
                cmd.Parameters["@g"].Value = meta.HasGps ? 1 : 0;
                cmd.Parameters["@q"].Value = (object?)meta.JpegQuality ?? DBNull.Value;
                cmd.Parameters["@b"].Value = meta.MetadataBytes;
                cmd.Parameters["@ss"].Value = (object?)meta.SubSecond ?? DBNull.Value;
                cmd.Parameters["@src"].Value = meta.SidecarSource == SidecarMetadata.Source.None
                    ? DBNull.Value : meta.SidecarSource.ToString();
                cmd.Parameters["@sp"].Value = (object?)meta.SidecarPath ?? DBNull.Value;
                cmd.Parameters["@k"].Value = item.Key;
                cmd.ExecuteNonQuery();

                _stats.Read++;
                if (meta.HasExif) _stats.WithExif++;
                if (meta.DateTaken is not null) _stats.WithCaptureTime++;
                if (meta.DependsOnSidecar) _stats.FromSidecar++;
            }

            tx.Commit();
        }
    }
}
