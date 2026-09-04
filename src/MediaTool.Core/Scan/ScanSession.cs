using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using MediaTool.Core.Crawl;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Scan;

public sealed class ScanProgress
{
    public long DirectoriesVisited;
    public long FilesSeen;
    public long FilesAccepted;
    public long BytesAccepted;
    public long FilesWritten;
    public string CurrentDirectory = "";
    public TimeSpan Elapsed;
}

public sealed class ScanResult
{
    public required VolumeInfo Volume { get; init; }
    public required string Root { get; init; }
    public required long ScanId { get; init; }
    public required bool Resumed { get; init; }
    public required CrawlStats Stats { get; init; }
    public required long FilesWritten { get; init; }
    public required long FilesMarkedMissing { get; init; }
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Runs one scan root end to end: resolve the volume, open or resume a scan, crawl, write,
/// and reconcile what disappeared since last time.
/// </summary>
public sealed class ScanSession
{
    private readonly CatalogDatabase _db;
    private readonly CrawlOptions _options;

    public Action<ScanProgress>? OnProgress { get; set; }

    public ScanSession(CatalogDatabase db, CrawlOptions options)
    {
        _db = db;
        _options = options;
    }

    public async Task<ScanResult> RunAsync(string rootPath, bool resume, CancellationToken ct)
    {
        string fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Scan root not found: {fullRoot}");

        var volume = VolumeScanner.ForPath(fullRoot)
            ?? throw new InvalidOperationException($"Could not resolve the volume for {fullRoot}.");

        string mountPoint = VolumeScanner.GetMountPointForPath(fullRoot)!;
        string rootRel = fullRoot.Length <= mountPoint.Length ? "" : fullRoot[mountPoint.Length..].TrimEnd('\\');

        long volumeId = _db.UpsertVolume(volume);
        var (scanId, frontier, resumed) = OpenOrResumeScan(volumeId, rootRel, resume);

        var stopwatch = Stopwatch.StartNew();

        // Bounded so a slow disk cannot let the crawler balloon memory ahead of the writer.
        var channel = Channel.CreateBounded<CrawlEvent>(new BoundedChannelOptions(16_384)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var crawler = new DirectoryCrawler(_options);
        var progress = new ScanProgress();
        var lastReport = 0L;

        crawler.OnDirectory = dir =>
        {
            progress.CurrentDirectory = dir;
            // Throttled to ~8 Hz: formatting a console line per directory costs more than
            // the directory read on a fast volume.
            long now = stopwatch.ElapsedMilliseconds;
            if (now - lastReport < 125) return;
            lastReport = now;

            progress.DirectoriesVisited = crawler.Stats.DirectoriesVisited;
            progress.FilesSeen = crawler.Stats.FilesSeen;
            progress.FilesAccepted = crawler.Stats.FilesAccepted;
            progress.BytesAccepted = crawler.Stats.BytesAccepted;
            progress.Elapsed = stopwatch.Elapsed;
            OnProgress?.Invoke(progress);
        };

        using var writer = new CatalogWriter(_db, volumeId, scanId);

        var writerTask = Task.Run(async () =>
        {
            try
            {
                await writer.RunAsync(channel.Reader, ct).ConfigureAwait(false);
            }
            catch
            {
                // Stop the crawler from blocking forever on a full channel it can no longer drain.
                channel.Writer.TryComplete();
                throw;
            }
        }, ct);

        try
        {
            await crawler.CrawlAsync(mountPoint, frontier, channel.Writer, ct).ConfigureAwait(false);
            channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            try { await writerTask.ConfigureAwait(false); } catch { /* surface the crawl failure */ }
            MarkScanStatus(scanId, "aborted", crawler.Stats, stopwatch.Elapsed);
            throw;
        }

        await writerTask.ConfigureAwait(false);
        stopwatch.Stop();

        long missing = MarkMissingFiles(volumeId, rootRel, scanId);
        MarkScanStatus(scanId, "completed", crawler.Stats, stopwatch.Elapsed);

        return new ScanResult
        {
            Volume = volume,
            Root = fullRoot,
            ScanId = scanId,
            Resumed = resumed,
            Stats = crawler.Stats,
            FilesWritten = writer.FilesWritten,
            FilesMarkedMissing = missing,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Finds an interrupted scan of the same root to continue, or starts a fresh one.
    /// A resumed scan picks up the persisted frontier; a fresh one is seeded with the root.
    /// </summary>
    private (long ScanId, List<string> Frontier, bool Resumed) OpenOrResumeScan(
        long volumeId, string rootRel, bool resume)
    {
        if (resume)
        {
            using var find = _db.Connection.CreateCommand();
            find.CommandText = """
                SELECT scan_id FROM scans
                WHERE volume_id=@vol AND root_rel=@root AND status='running'
                ORDER BY scan_id DESC LIMIT 1
                """;
            find.Parameters.AddWithValue("@vol", volumeId);
            find.Parameters.AddWithValue("@root", rootRel);

            if (find.ExecuteScalar() is long existing)
            {
                var pending = LoadFrontier(existing);
                // An empty frontier means the previous run actually finished the walk and
                // only failed to record completion; nothing left to do but close it out.
                if (pending.Count > 0) return (existing, pending, true);
            }
        }

        // Any older running scan of this root is superseded.
        using (var stale = _db.Connection.CreateCommand())
        {
            stale.CommandText =
                "UPDATE scans SET status='superseded' WHERE volume_id=@vol AND root_rel=@root AND status='running'";
            stale.Parameters.AddWithValue("@vol", volumeId);
            stale.Parameters.AddWithValue("@root", rootRel);
            stale.ExecuteNonQuery();
        }

        using var insert = _db.Connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO scans (volume_id, root_rel, status, started_utc)
            VALUES (@vol, @root, 'running', @started)
            RETURNING scan_id
            """;
        insert.Parameters.AddWithValue("@vol", volumeId);
        insert.Parameters.AddWithValue("@root", rootRel);
        insert.Parameters.AddWithValue("@started", DateTime.UtcNow.ToString("O"));
        long scanId = (long)insert.ExecuteScalar()!;

        using var seed = _db.Connection.CreateCommand();
        seed.CommandText = "INSERT INTO crawl_frontier (scan_id, rel_dir) VALUES (@scan, @dir)";
        seed.Parameters.AddWithValue("@scan", scanId);
        seed.Parameters.AddWithValue("@dir", rootRel);
        seed.ExecuteNonQuery();

        return (scanId, [rootRel], false);
    }

    private List<string> LoadFrontier(long scanId)
    {
        var result = new List<string>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT rel_dir FROM crawl_frontier WHERE scan_id=@scan";
        cmd.Parameters.AddWithValue("@scan", scanId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Flags files under this root that the completed scan did not see. They are marked, never
    /// deleted: a file can vanish because it was moved, and its recorded hashes stay useful
    /// for deciding whether a copy found elsewhere is safe to drop.
    /// </summary>
    private long MarkMissingFiles(long volumeId, string rootRel, long scanId)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (rootRel.Length == 0)
        {
            cmd.CommandText = """
                UPDATE files SET present=0
                WHERE volume_id=@vol AND present=1 AND last_scan_id<>@scan
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE files SET present=0
                WHERE volume_id=@vol AND present=1 AND last_scan_id<>@scan
                  AND (rel_path=@root OR rel_path LIKE @prefix ESCAPE '\')
                """;
            cmd.Parameters.AddWithValue("@root", rootRel);
            cmd.Parameters.AddWithValue("@prefix", EscapeLike(rootRel) + @"\%");
        }
        cmd.Parameters.AddWithValue("@vol", volumeId);
        cmd.Parameters.AddWithValue("@scan", scanId);
        return cmd.ExecuteNonQuery();
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private void MarkScanStatus(long scanId, string status, CrawlStats stats, TimeSpan elapsed)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE scans SET status=@status, finished_utc=@finished,
                             dirs_visited=@dirs, files_seen=@seen,
                             files_accepted=@accepted, bytes_accepted=@bytes
            WHERE scan_id=@scan
            """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@finished", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@dirs", stats.DirectoriesVisited);
        cmd.Parameters.AddWithValue("@seen", stats.FilesSeen);
        cmd.Parameters.AddWithValue("@accepted", stats.FilesAccepted);
        cmd.Parameters.AddWithValue("@bytes", stats.BytesAccepted);
        cmd.Parameters.AddWithValue("@scan", scanId);
        cmd.ExecuteNonQuery();
    }
}
