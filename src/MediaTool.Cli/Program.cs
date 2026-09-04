using System.Globalization;
using MediaTool.Core.Actions;
using MediaTool.Core.Crawl;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Hashing;
using MediaTool.Core.Imaging;
using MediaTool.Core.Metadata;
using MediaTool.Core.Scan;
using MediaTool.Core.Shell;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Cli;

internal static class Program
{
    private static readonly string DefaultDbPath = MediaTool.Core.Storage.CatalogLocation.Resolve();

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // let the writer commit its open transaction and exit cleanly
            Console.Error.WriteLine("\nStopping — the scan can be continued with the same command.");
            cts.Cancel();
        };

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "volumes" => CommandVolumes(),
                "scan" => await CommandScan(args[1..], cts.Token),
                "hash" => await CommandHash(args[1..], cts.Token),
                "duplicates" or "dupes" => CommandDuplicates(args[1..]),
                "images" => await CommandImages(args[1..], cts.Token),
                "probe" => CommandProbe(args[1..]),
                "metadata" => await CommandMetadata(args[1..], cts.Token),
                "plan" => CommandPlan(args[1..]),
                "apply" => CommandApply(args[1..], cts.Token),
                "undo" => CommandUndo(args[1..], cts.Token),
                "history" => CommandHistory(args[1..]),
                "purge" => CommandPurge(args[1..], cts.Token),
                "merge-exif" => CommandMergeExif(args[1..], cts.Token),
                "hardlink" => CommandHardlink(args[1..], cts.Token),
                "similar" => CommandSimilar(args[1..]),
                "stats" => CommandStats(args[1..]),
                "shell" => CommandShell(args[1..]),
                "folders" => CommandFolders(args[1..]),
                "review" => CommandReview(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'. Run 'mediatool --help'."),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Interrupted. Progress up to the last committed directory is saved.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    // ---- volumes ---------------------------------------------------------

    private static int CommandVolumes()
    {
        var volumes = VolumeScanner.EnumerateVolumes();

        Console.WriteLine($"{volumes.Count} volume(s) attached\n");
        Console.WriteLine($"{"MOUNT",-12} {"LABEL",-20} {"FS",-8} {"STORAGE",-8} {"FILE ID",-8} {"SIZE",10}");
        Console.WriteLine(new string('-', 76));

        foreach (var v in volumes.OrderBy(v => v.PrimaryMountPoint ?? "\uffff"))
        {
            Console.WriteLine(
                $"{v.PrimaryMountPoint ?? "(none)",-12} " +
                $"{Truncate(v.Label, 20),-20} " +
                $"{v.FileSystem,-8} " +
                $"{v.StorageKind,-8} " +
                $"{(v.SupportsFileId ? "yes" : "no"),-8} " +
                $"{FormatBytes((long)v.TotalBytes),10}");
            Console.WriteLine($"  {v.VolumeGuid}");
        }

        Console.WriteLine("""

            The GUID is the identity the catalog stores. Drive letters are reassigned when
            disks are replugged; the GUID is not, so a catalogued disk stays recognisable.
            """);
        return 0;
    }

    // ---- scan ------------------------------------------------------------

    private static async Task<int> CommandScan(string[] args, CancellationToken ct)
    {
        var roots = new List<string>();
        string dbPath = DefaultDbPath;
        var options = new CrawlOptions();
        bool resume = true;
        bool allDrives = false;
        bool includeCloudDrives = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--db": dbPath = args[++i]; break;
                case "--all": options.Extensions.Clear(); break;
                case "--ext":
                    options.Extensions.Clear();
                    foreach (string e in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Extensions.Add(e.StartsWith('.') ? e : "." + e);
                    break;
                case "--min-size": options.MinSizeBytes = ParseSize(args[++i]); break;
                case "--include-cloud": options.SkipCloudPlaceholders = false; break;
                case "--include-hidden": options.SkipHidden = false; break;
                case "--no-resume": resume = false; break;
                case "--all-drives": allDrives = true; break;
                case "--include-cloud-drives": includeCloudDrives = true; break;
                case "--exclude": options.ExcludedPathFragments.Add(args[++i]); break;
                default:
                    if (a.StartsWith('-')) return Fail($"Unknown option '{a}'.");
                    roots.Add(a);
                    break;
            }
        }

        if (allDrives)
        {
            // Which drives get scanned is printed, including the ones held back and why. A
            // cloud provider's virtual drive is indistinguishable from a local disk to
            // Windows, and walking it would stream the whole account over the network, so
            // that decision is never made silently.
            var targets = ScanTargetSelector.Choose(VolumeScanner.EnumerateVolumes());

            Console.WriteLine("Drives found:");
            foreach (var t in targets.OrderBy(t => t.Path))
            {
                bool take = t.Path.Length > 0 && (t.Recommended || includeCloudDrives);
                string where = t.Path.Length > 0 ? t.Path : "(no mount point)";
                Console.WriteLine($"  {(take ? "scan" : "skip")}  {where,-8} {Truncate(t.Volume.Label, 20),-20}" +
                                  (t.Note.Length > 0 ? $"  — {t.Note}" : ""));
                if (take) roots.Add(t.Path);
            }
            Console.WriteLine();

            if (!includeCloudDrives && targets.Any(t => !t.Recommended && t.Note.Contains("cloud")))
                Console.WriteLine("A cloud drive was held back. Pass --include-cloud-drives only if you\n" +
                                  "really want its contents pulled down over the network.\n");
        }

        if (roots.Count == 0)
            return Fail("scan needs at least one path, or --all-drives. Example: mediatool scan E:\\Photos F:\\");

        if (!options.SkipCloudPlaceholders)
        {
            Console.Error.WriteLine(
                "WARNING: --include-cloud will read OneDrive/Dropbox placeholder files, which makes\n" +
                "         the sync provider download them. On a large library that can pull down\n" +
                "         terabytes. Continue only if the library really is fully local.\n");
        }

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        var totals = new CrawlStats();
        foreach (string root in roots)
        {
            var session = new ScanSession(db, options) { OnProgress = RenderProgress };

            Console.WriteLine($"Scanning {root}");
            var result = await session.RunAsync(root, resume, ct);
            ClearProgressLine();

            Console.WriteLine($"  volume        {result.Volume}");
            if (result.Resumed) Console.WriteLine($"  resumed       scan #{result.ScanId}");
            if (!result.Volume.SupportsFileId)
                Console.WriteLine("  note          filesystem has no file ids; rescans fall back to path matching");

            Console.WriteLine($"  directories   {result.Stats.DirectoriesVisited:N0}");
            Console.WriteLine($"  files seen    {result.Stats.FilesSeen:N0}");
            Console.WriteLine($"  catalogued    {result.Stats.FilesAccepted:N0}  ({FormatBytes(result.Stats.BytesAccepted)})");
            if (result.FilesMarkedMissing > 0)
                Console.WriteLine($"  now missing   {result.FilesMarkedMissing:N0}");
            if (result.Stats.CloudPlaceholdersSkipped > 0)
                Console.WriteLine($"  cloud-only    {result.Stats.CloudPlaceholdersSkipped:N0} skipped (not downloaded)");
            if (result.Stats.ReparsePointsSkipped > 0)
                Console.WriteLine($"  links skipped {result.Stats.ReparsePointsSkipped:N0}");
            if (result.Stats.AccessDenied > 0)
                Console.WriteLine($"  access denied {result.Stats.AccessDenied:N0} directories");
            if (result.Stats.Errors > 0)
                Console.WriteLine($"  errors        {result.Stats.Errors:N0}");

            double seconds = Math.Max(result.Duration.TotalSeconds, 0.001);
            Console.WriteLine($"  duration      {result.Duration:hh\\:mm\\:ss}  " +
                              $"({result.Stats.FilesSeen / seconds:N0} files/s)\n");

            totals.DirectoriesVisited += result.Stats.DirectoriesVisited;
            totals.FilesSeen += result.Stats.FilesSeen;
            totals.FilesAccepted += result.Stats.FilesAccepted;
            totals.BytesAccepted += result.Stats.BytesAccepted;
        }

        if (roots.Count > 1)
            Console.WriteLine($"Total: {totals.FilesAccepted:N0} files catalogued " +
                              $"({FormatBytes(totals.BytesAccepted)}) across {roots.Count} roots.");

        return 0;
    }

    private static int _progressWidth;

    private static void RenderProgress(ScanProgress p)
    {
        double seconds = Math.Max(p.Elapsed.TotalSeconds, 0.001);
        string dir = Truncate(p.CurrentDirectory, 48);
        string line = $"  {p.FilesAccepted:N0} files  {FormatBytes(p.BytesAccepted)}  " +
                      $"{p.DirectoriesVisited:N0} dirs  {p.FilesSeen / seconds:N0}/s  {dir}";

        if (line.Length < _progressWidth) line = line.PadRight(_progressWidth);
        _progressWidth = line.Length;
        Console.Write("\r" + line);
    }

    private static void ClearProgressLine()
    {
        if (_progressWidth == 0) return;
        Console.Write('\r' + new string(' ', _progressWidth) + '\r');
        _progressWidth = 0;
    }

    // ---- stats -----------------------------------------------------------

    private static int CommandStats(string[] args)
    {
        string dbPath = DefaultDbPath;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--db") dbPath = args[++i];

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT v.last_mount_point, v.label, v.file_system, v.storage_kind,
                       COUNT(f.file_key), COALESCE(SUM(f.size),0),
                       COALESCE(SUM(CASE WHEN f.file_id_low IS NOT NULL THEN 1 ELSE 0 END),0)
                FROM volumes v LEFT JOIN files f ON f.volume_id=v.volume_id AND f.present=1
                GROUP BY v.volume_id ORDER BY 5 DESC
                """;
            using var r = cmd.ExecuteReader();
            Console.WriteLine($"{"VOLUME",-14} {"LABEL",-18} {"FS",-7} {"DISK",-8} {"FILES",12} {"SIZE",12} {"FILE IDS",9}");
            Console.WriteLine(new string('-', 86));
            while (r.Read())
            {
                long count = r.GetInt64(4), withIds = r.GetInt64(6);
                string idCoverage = count == 0 ? "-" : $"{100.0 * withIds / count:F0}%";
                Console.WriteLine(
                    $"{(r.IsDBNull(0) ? "(offline)" : r.GetString(0)),-14} " +
                    $"{Truncate(r.IsDBNull(1) ? "" : r.GetString(1), 18),-18} " +
                    $"{(r.IsDBNull(2) ? "" : r.GetString(2)),-7} " +
                    $"{(StorageKind)r.GetInt32(3),-8} " +
                    $"{count,12:N0} {FormatBytes(r.GetInt64(5)),12} {idCoverage,9}");
            }
            Console.WriteLine(
                "\n  FILE IDS is the share of files with a filesystem identity. Where it is 100%,\n" +
                "  a rescan recognises renamed and moved files instead of re-hashing them.");
        }

        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ext, COUNT(*), SUM(size) FROM files WHERE present=1
                GROUP BY ext ORDER BY 2 DESC LIMIT 12
                """;
            using var r = cmd.ExecuteReader();
            Console.WriteLine($"\n{"EXT",-10} {"FILES",12} {"SIZE",12}");
            Console.WriteLine(new string('-', 36));
            while (r.Read())
                Console.WriteLine($"{(r.IsDBNull(0) ? "(none)" : r.GetString(0)),-10} " +
                                  $"{r.GetInt64(1),12:N0} {FormatBytes(r.GetInt64(2)),12}");
        }

        // The first cut of the duplicate cascade, answered straight from the catalog: files
        // whose size is unique cannot be byte-identical to anything, so they never get read.
        using (var cmd = db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*), COALESCE(SUM(n),0), COALESCE(SUM(bytes),0), COALESCE(SUM(probe),0) FROM (
                    SELECT COUNT(*) AS n, SUM(size) AS bytes,
                           -- Stage 2 reads a 64KB head and tail, but never more than the file
                           -- itself: below 128KB the two probes overlap, so it just reads it all.
                           SUM(MIN(size, 131072)) AS probe
                    FROM files WHERE present=1 GROUP BY size HAVING n > 1
                )
                """;
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                long groups = r.GetInt64(0), files = r.GetInt64(1), bytes = r.GetInt64(2);
                long probeBytes = r.GetInt64(3);

                Console.WriteLine($"""

                    Duplicate cascade, stage 1 (size collision)
                      size groups        {groups:N0}
                      candidate files    {files:N0}  of {TotalPresentFiles(db):N0}
                      candidate bytes    {FormatBytes(bytes)}
                      stage 2 will read  {FormatBytes(probeBytes)}  (64KB head + tail per candidate)

                    Files whose size is unique are provably not byte-duplicates and are never
                    read at all. The candidate bytes above are an upper bound, not the eventual
                    cost: stage 2's head/tail probe eliminates most groups before any full read.

                    Note this cut is weak on build artifacts and generated assets, where many
                    small files share a size by coincidence. On a photo library it is sharp.

                    Visual duplicates - resized, recompressed, EXIF stripped - do not collide on
                    size and are found by a separate decode pass, not counted here.
                    """);
            }
        }

        return 0;
    }

    // ---- hash ------------------------------------------------------------

    private static async Task<int> CommandHash(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--db") dbPath = args[++i];

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        var pipeline = new HashPipeline(db) { OnProgress = RenderHashProgress };
        var stats = await pipeline.RunAsync(ct);
        ClearProgressLine();

        if (stats.OfflineVolumeNames.Count > 0)
        {
            Console.WriteLine("Volumes in the catalog that are not attached right now:");
            foreach (string name in stats.OfflineVolumeNames) Console.WriteLine($"  {name}");
            Console.WriteLine("Their files keep the hashes they already have and were not re-read.\n");
        }

        Console.WriteLine($"  stage 2 probe   {stats.ProbedFiles,10:N0} files  {FormatBytes(stats.ProbedBytes),10} read");
        Console.WriteLine($"  stage 3 full    {stats.FullyHashedFiles,10:N0} files  {FormatBytes(stats.FullyHashedBytes),10} read");
        Console.WriteLine($"  total read      {FormatBytes(stats.ProbedBytes + stats.FullyHashedBytes),10}");
        if (stats.Failures > 0)
            Console.WriteLine($"  unreadable      {stats.Failures,10:N0} files (locked, deleted, or bad sectors)");

        Console.WriteLine("\nRun 'mediatool duplicates' to see the groups.");
        return 0;
    }

    private static void RenderHashProgress(HashProgress p)
    {
        double seconds = Math.Max(p.Elapsed.TotalSeconds, 0.001);
        double percent = p.FilesTotal == 0 ? 0 : 100.0 * p.FilesDone / p.FilesTotal;
        string line = $"  {p.Stage,-9} {percent,5:F1}%  {p.FilesDone:N0}/{p.FilesTotal:N0}  " +
                      $"{FormatBytes(p.BytesRead)}  {FormatBytes((long)(p.BytesRead / seconds))}/s";
        if (p.Failures > 0) line += $"  {p.Failures:N0} failed";

        if (line.Length < _progressWidth) line = line.PadRight(_progressWidth);
        _progressWidth = line.Length;
        Console.Write("\r" + line);
    }

    // ---- duplicates ------------------------------------------------------

    private static int CommandDuplicates(string[] args)
    {
        string dbPath = DefaultDbPath;
        string? csvPath = null;
        int top = 20;
        long minSize = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--csv": csvPath = args[++i]; break;
                case "--top": top = int.Parse(args[++i]); break;
                case "--min-size": minSize = ParseSize(args[++i]); break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        var onlineGuids = VolumeScanner.EnumerateVolumes()
            .Select(v => v.VolumeGuid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var finder = new DuplicateFinder(db);
        var summary = new DuplicateSummary { UnhashedCandidates = finder.CountUnhashedCandidates() };

        // Only the biggest wins are held in memory; the totals are accumulated on the fly so
        // a catalog with a million duplicate groups still reports without loading them all.
        var biggest = new List<DuplicateGroup>();
        StreamWriter? csv = null;
        if (csvPath is not null)
        {
            csv = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            csv.WriteLine("group,content_hash,size_bytes,copies,hardlinked,reclaimable_bytes,volume,path,online");
        }

        int groupIndex = 0;
        foreach (var group in finder.FindExactDuplicates(onlineGuids))
        {
            if (group.Size < minSize) continue;

            groupIndex++;
            summary.Groups++;
            summary.RedundantFiles += group.PhysicalCopies - 1;
            summary.ReclaimableBytes += group.ReclaimableBytes;
            summary.HardlinkedPaths += group.HardlinkedPaths;
            if (group.TouchesOfflineVolume) summary.GroupsTouchingOfflineVolumes++;

            if (csv is not null)
            {
                string hex = Convert.ToHexString(group.ContentHash);
                foreach (var e in group.Entries)
                    csv.WriteLine($"{groupIndex},{hex},{group.Size},{group.PhysicalCopies}," +
                                  $"{group.HardlinkedPaths},{group.ReclaimableBytes}," +
                                  $"{Csv(e.VolumeName)},{Csv(e.RelativePath)},{(e.VolumeOnline ? 1 : 0)}");
            }

            biggest.Add(group);
            if (biggest.Count > top * 4)
            {
                biggest.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));
                biggest.RemoveRange(top, biggest.Count - top);
            }
        }

        csv?.Dispose();

        biggest.Sort((a, b) => b.ReclaimableBytes.CompareTo(a.ReclaimableBytes));

        if (summary.Groups == 0)
        {
            Console.WriteLine("No byte-identical duplicates found.");
        }
        else
        {
            Console.WriteLine($"Top {Math.Min(top, biggest.Count)} groups by reclaimable space\n");
            foreach (var g in biggest.Take(top))
            {
                Console.WriteLine($"  {FormatBytes(g.Size)} x {g.PhysicalCopies} copies " +
                                  $"-> {FormatBytes(g.ReclaimableBytes)} reclaimable" +
                                  (g.HardlinkedPaths > 0 ? $"  ({g.HardlinkedPaths} hardlinked path(s), free nothing)" : "") +
                                  (g.TouchesOfflineVolume ? "  [has copies on an offline disk]" : ""));
                foreach (var e in g.Entries)
                    Console.WriteLine($"      {(e.VolumeOnline ? " " : "!")} {e.FullPath}");
                Console.WriteLine();
            }
        }

        Console.WriteLine($"""
            Summary
              duplicate groups  {summary.Groups:N0}
              redundant copies  {summary.RedundantFiles:N0}
              reclaimable       {FormatBytes(summary.ReclaimableBytes)}
            """);

        if (summary.HardlinkedPaths > 0)
            Console.WriteLine($"  hardlinked paths  {summary.HardlinkedPaths:N0} (same bytes on disk; deleting one frees nothing)");
        if (summary.GroupsTouchingOfflineVolumes > 0)
            Console.WriteLine($"  offline-disk      {summary.GroupsTouchingOfflineVolumes:N0} groups have a copy on a disk that is not attached");
        if (summary.UnhashedCandidates > 0)
            Console.WriteLine($"  NOT YET HASHED    {summary.UnhashedCandidates:N0} size-collision candidates - run 'mediatool hash'; results above are incomplete");

        if (csvPath is not null) Console.WriteLine($"\nFull listing written to {csvPath}");

        Console.WriteLine("""

            These groups are byte-identical. Files that are the same photo but were resized,
            recompressed, or had their EXIF stripped do NOT appear here - they are found by
            the perceptual pass, which is not built yet.

            Nothing has been changed on disk. This command only reports.
            """);
        return 0;
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    // ---- images (decode pass) --------------------------------------------

    private static async Task<int> CommandImages(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        bool retry = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--retry-failed": retry = true; break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        var pipeline = new FingerprintPipeline(db)
        {
            RetryFailures = retry,
            OnProgress = RenderFingerprintProgress,
        };

        var stats = await pipeline.RunAsync(ct);
        ClearProgressLine();

        if (stats.OfflineVolumeNames.Count > 0)
        {
            Console.WriteLine("Not attached right now, so not decoded:");
            foreach (string name in stats.OfflineVolumeNames) Console.WriteLine($"  {name}");
            Console.WriteLine();
        }

        Console.WriteLine($"  decoded    {stats.Decoded,10:N0}");
        if (stats.Failed > 0)
            Console.WriteLine($"  failed     {stats.Failed,10:N0}  (corrupt, or a format with no codec installed - " +
                              "HEIC needs the HEIF extension, RAW needs the Raw Image Extension)");

        Console.WriteLine("\nRun 'mediatool similar' to cluster them.");
        return 0;
    }

    private static void RenderFingerprintProgress(FingerprintProgress p)
    {
        double seconds = Math.Max(p.Elapsed.TotalSeconds, 0.001);
        double percent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        string line = $"  decoding {percent,5:F1}%  {p.Done:N0}/{p.Total:N0}  {p.Done / seconds:N0} img/s";
        if (p.Failed > 0) line += $"  {p.Failed:N0} failed";

        if (line.Length < _progressWidth) line = line.PadRight(_progressWidth);
        _progressWidth = line.Length;
        Console.Write("\r" + line);
    }

    /// <summary>
    /// Decodes one file and reports exactly what happened. When a whole decode pass fails,
    /// the difference between "no codec for this format" and "the interop is wrong" is the
    /// only thing worth knowing, and a batch run hides it behind a failure count.
    /// </summary>
    private static int CommandProbe(string[] args)
    {
        if (args.Length == 0) return Fail("probe needs a file path.");

        foreach (string path in args)
        {
            Console.WriteLine(path);
            try
            {
                var decoded = ImageDecoder.Decode(path);
                var print = PerceptualHash.Compute(decoded);
                Console.WriteLine($"  ok        {decoded.Width}x{decoded.Height}");
                Console.WriteLine($"  pixel     {Convert.ToHexString(print.PixelHash)}");
                Console.WriteLine($"  dhash     {print.DHash:x16}");
                Console.WriteLine($"  phash     {print.PHash:x16}");
                Console.WriteLine($"  contrast  {print.Contrast:F2}");

                var meta = MediaTool.Core.Metadata.JpegMetadata.Read(path);
                Console.WriteLine($"  exif      {(meta.HasExif ? $"{meta.TagCount} tags" : "none")}" +
                                  $"  date={meta.DateTaken?.ToString("yyyy-MM-dd HH:mm") ?? "-"}" +
                                  $"  camera={meta.Camera ?? "-"}" +
                                  $"  gps={meta.HasGps}");
                Console.WriteLine($"  jpeg      quality~{meta.JpegQuality?.ToString() ?? "?"}" +
                                  $"  metadata={meta.MetadataBytes:N0} bytes");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED    {ex.GetType().Name}: {ex.Message}");
                if (ex is System.Runtime.InteropServices.COMException com)
                    Console.WriteLine($"  HRESULT   0x{com.HResult:X8}");
            }
            Console.WriteLine();
        }
        return 0;
    }

    // ---- similar ---------------------------------------------------------

    private static int CommandSimilar(string[] args)
    {
        string dbPath = DefaultDbPath;
        string? csvPath = null;
        int top = 15;
        var options = new SimilarityOptions();
        bool pixelOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--csv": csvPath = args[++i]; break;
                case "--top": top = int.Parse(args[++i]); break;
                case "--hamming": options.MaxHamming = int.Parse(args[++i]); break;
                case "--mae": options.MaxThumbnailDistance = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--min-contrast": options.MinContrast = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                // Tier 2 only: exact same picture, no threshold involved anywhere.
                case "--pixel-only": pixelOnly = true; options.MaxHamming = 0; options.MaxThumbnailDistance = 0; break;
                case "--under": options.Scope.Under.Add(args[++i]); break;
                case "--exclude": options.Scope.Exclude.Add(args[++i]); break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        if (!options.Scope.IsEmpty) Console.WriteLine($"  scope: {options.Scope}");
        var index = new SimilarityIndex(db, options);
        var result = index.Build(step => Console.WriteLine($"  {step}"));
        Console.WriteLine();

        if (result.DecodedImages == 0)
            return Fail("No decoded images in the catalog. Run 'mediatool images' first.");

        var shown = pixelOnly
            ? result.Clusters.Where(c => c.PixelIdentical).ToList()
            : result.Clusters;

        Console.WriteLine($"Top {Math.Min(top, shown.Count)} clusters by reclaimable space\n");
        foreach (var cluster in shown.Take(top))
        {
            string kind = cluster.PixelIdentical ? "IDENTICAL PICTURE" : "same photo, mixed versions";
            Console.WriteLine($"  [{kind}] {cluster.Entries.Count} files -> {FormatBytes(cluster.ReclaimableBytes)} reclaimable");

            // Grouped by exact picture, because that is where the decision changes: inside a
            // group the files differ only in metadata, so any one of them will do; across
            // groups they differ in resolution or quality, so the choice is a real one.
            var groups = cluster.PixelGroups;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                string label = groups.Count == 1 ? "   " : $" {(char)('A' + Math.Min(g, 25))} ";

                if (group.Count > 1)
                    Console.WriteLine($"     {label} {group.Count} files, identical picture - differ in metadata only:");

                foreach (var e in group.OrderByDescending(e => e.Size))
                    Console.WriteLine($"     {(group.Count > 1 ? "   " : label)} {e.Width}x{e.Height,-6} {FormatBytes(e.Size),9}  {e.FullPath}");
            }
            Console.WriteLine();
        }

        if (csvPath is not null)
        {
            using var csv = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            csv.WriteLine("cluster,kind,files,reclaimable_bytes,width,height,size_bytes,volume,path");
            int n = 0;
            foreach (var cluster in shown)
            {
                n++;
                string kind = cluster.PixelIdentical ? "identical" : "near";
                foreach (var e in cluster.Entries)
                    csv.WriteLine($"{n},{kind},{cluster.Entries.Count},{cluster.ReclaimableBytes}," +
                                  $"{e.Width},{e.Height},{e.Size},{Csv(e.VolumeName)},{Csv(e.RelativePath)}");
            }
        }

        long identical = shown.Count(c => c.PixelIdentical);
        long reclaimable = shown.Sum(c => c.ReclaimableBytes);
        long redundant = shown.Sum(c => c.Entries.Count - 1);
        long metadataOnly = shown.Sum(c => c.MetadataOnlyDuplicates);

        Console.WriteLine($"""
            Summary
              images decoded       {result.DecodedImages:N0}
              distinct prints      {result.DistinctFingerprints:N0}
              clusters             {shown.Count:N0}   ({identical:N0} wholly identical, {shown.Count - identical:N0} mixed versions)
              redundant files      {redundant:N0}
              of which metadata    {metadataOnly:N0}   (identical picture, different bytes - the stripped-EXIF case)
              reclaimable          {FormatBytes(reclaimable)}
            """);

        if (result.LowContrastHeldBack > 0)
            Console.WriteLine($"  held back            {result.LowContrastHeldBack:N0} near-blank images " +
                              $"(contrast < {options.MinContrast:F1}; they all hash alike and would form one false cluster)");
        if (result.ClustersSplitByExposure > 0)
            Console.WriteLine($"  split again          {result.ClustersSplitByExposure:N0} clusters had merged two exposures " +
                              "through a copy with no capture time, and were broken apart");
        if (result.SeparateMomentsRejected > 0)
            Console.WriteLine($"  different moments    {result.SeparateMomentsRejected:N0} look-alike pairs refused - " +
                              "the camera recorded two different capture times, so they are two photos, not two copies");
        if (result.WithCaptureTime < result.DecodedImages)
            Console.WriteLine($"  NO capture time      {result.DecodedImages - result.WithCaptureTime:N0} images - " +
                              "run 'mediatool metadata' so burst frames can be told apart");
        if (result.OversizedBucketsSkipped > 0)
            Console.WriteLine($"  skipped buckets      {result.OversizedBucketsSkipped:N0} exceeded {options.MaxBucketSize:N0} entries");

        if (!pixelOnly) PrintCalibration(result, options);

        if (csvPath is not null) Console.WriteLine($"\nFull listing written to {csvPath}");

        Console.WriteLine("\nNothing has been changed on disk. This command only reports.");
        return 0;
    }

    /// <summary>
    /// Shows where the candidate pairs actually fall, so the thresholds can be set from this
    /// library's own distribution instead of from a guess. A clean separation looks like a
    /// cluster of pairs at low distance, a gap, then unrelated pairs further out.
    /// </summary>
    private static void PrintCalibration(SimilarityResult result, SimilarityOptions options)
    {
        if (result.CandidatePairs == 0) return;

        Console.WriteLine($"\nThreshold calibration  ({result.CandidatePairs:N0} candidate pairs, " +
                          $"{result.ConfirmedPairs:N0} confirmed)\n");

        Console.WriteLine($"  hamming distance (current cut: <= {options.MaxHamming})");
        long peak = Math.Max(result.HammingHistogram.Take(options.MaxHamming + 1).DefaultIfEmpty(0).Max(), 1);
        for (int d = 0; d <= options.MaxHamming; d++)
        {
            long count = result.HammingHistogram[d];
            Console.WriteLine($"    {d,2} bits  {Bar(count, peak)} {count:N0}");
        }

        Console.WriteLine($"\n  thumbnail distance (current cut: <= {options.MaxThumbnailDistance:F1})");
        long thumbPeak = Math.Max(result.ThumbnailHistogram.DefaultIfEmpty(0).Max(), 1);
        for (int d = 0; d < 24; d++)
        {
            long count = result.ThumbnailHistogram[d];
            if (count == 0 && d > options.MaxThumbnailDistance + 6) continue;
            string marker = d <= options.MaxThumbnailDistance ? "keep" : "drop";
            Console.WriteLine($"    {d,2}      {Bar(count, thumbPeak)} {count,8:N0}  {marker}");
        }

        Console.WriteLine("""

              Raise --mae to accept looser matches, lower it to be stricter. A gap in the
              histogram is where the real cut belongs; if there is no gap, this library has a
              continuum of similar-but-different photos and the number is a judgement call.
            """);
    }

    private static string Bar(long value, long peak)
    {
        int width = peak == 0 ? 0 : (int)(30.0 * value / peak);
        return new string('#', Math.Min(width, 30)).PadRight(30);
    }

    // ---- metadata --------------------------------------------------------

    private static async Task<int> CommandMetadata(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        var scope = new CatalogScope();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--under": scope.Under.Add(args[++i]); break;
                case "--exclude": scope.Exclude.Add(args[++i]); break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}" + Environment.NewLine);

        var pipeline = new MetadataPipeline(db) { OnProgress = RenderMetadataProgress };
        var stats = await pipeline.RunAsync(scope, ct);
        ClearProgressLine();

        Console.WriteLine($"  read              {stats.Read,10:N0}");
        Console.WriteLine($"  with EXIF         {stats.WithExif,10:N0}");
        Console.WriteLine($"  with capture time {stats.WithCaptureTime,10:N0}");
        if (stats.FromSidecar > 0)
            Console.WriteLine($"  from a sidecar    {stats.FromSidecar,10:N0}  (Google Takeout / XMP - the date is " +
                              "in a separate file, which any copy or move can leave behind)");

        Console.WriteLine("""

            The capture time is what separates two frames of a burst from two copies of one
            photo. Without it, 'similar' has to judge on appearance alone, and consecutive
            shots of the same scene look identical to any perceptual hash.
            """);
        return 0;
    }

    private static void RenderMetadataProgress(MetadataProgress p)
    {
        double seconds = Math.Max(p.Elapsed.TotalSeconds, 0.001);
        double percent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        string line = $"  metadata {percent,5:F1}%  {p.Done:N0}/{p.Total:N0}  {p.Done / seconds:N0}/s";
        if (line.Length < _progressWidth) line = line.PadRight(_progressWidth);
        _progressWidth = line.Length;
        Console.Write("\r" + line);
    }

    // ---- plan / apply / undo ---------------------------------------------

    private static readonly string DefaultPlanPath = Path.Combine(
        Path.GetDirectoryName(DefaultDbPath)!, "plan.csv");

    private static int CommandPlan(string[] args)
    {
        string dbPath = DefaultDbPath;
        string outPath = DefaultPlanPath;
        var options = new PlanOptions();
        int preview = 10;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--out": outPath = args[++i]; break;
                case "--exact": options.Kind = GroupKind.ExactBytes; break;
                case "--pixel": options.Kind = GroupKind.IdenticalPicture; break;
                case "--similar": options.Kind = GroupKind.NearDuplicate; break;
                case "--min-save": options.MinReclaimableBytes = ParseSize(args[++i]); break;
                case "--include-offline": options.SkipOfflineGroups = false; break;
                case "--prefer": options.Keeper.PreferredPathFragments.Add(args[++i]); break;
                case "--under": options.Scope.Under.Add(args[++i]); break;
                case "--exclude": options.Scope.Exclude.Add(args[++i]); break;
                case "--preview": preview = int.Parse(args[++i]); break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}\n");

        if (!options.Scope.IsEmpty) Console.WriteLine($"  scope: {options.Scope}");
        var builder = new PlanBuilder(db, options);
        var (rows, summary) = builder.Build(step => Console.WriteLine($"  {step}"));
        Console.WriteLine();

        if (summary.Groups == 0)
        {
            Console.WriteLine("Nothing to plan for this mode.");
            return 0;
        }

        foreach (var group in rows.GroupBy(r => r.Group).Take(preview))
        {
            Console.WriteLine($"  group {group.Key}");
            foreach (var row in group.OrderBy(r => r.Action))
            {
                string mark = row.Action == PlannedAction.Keep ? "KEEP " : "  -> ";
                Console.WriteLine($"    {mark} {row.File.Width}x{row.File.Height,-6} " +
                                  $"{FormatBytes(row.File.Size),9}  {row.File.FullPath}");
                Console.WriteLine($"           {row.Reason}");
            }
            Console.WriteLine();
        }

        PlanCsv.Write(outPath, rows);

        Console.WriteLine($"""
            Plan
              mode              {options.Kind}
              groups            {summary.Groups:N0}
              keep              {summary.Keep:N0}
              quarantine        {summary.Quarantine:N0}
              would free        {FormatBytes(summary.ReclaimableBytes)}
            """);

        if (summary.GroupsMissingMetadataOnKeeper > 0)
            Console.WriteLine($"  ATTENTION        {summary.GroupsMissingMetadataOnKeeper:N0} groups where a copy being " +
                              "removed has more metadata than the one kept - review these first");
        if (summary.GroupsWithOfflineCopies > 0)
            Console.WriteLine($"  skipped          {summary.GroupsWithOfflineCopies:N0} groups have a copy on a disk that " +
                              "is not attached (use --include-offline to plan them anyway)");

        Console.WriteLine($"""

            Written to {outPath}

            Read it, edit it if you disagree - the 'action' column accepts keep, quarantine
            or skip - then:
              mediatool apply --plan "{outPath}" --quarantine <folder>            (dry run)
              mediatool apply --plan "{outPath}" --quarantine <folder> --execute  (moves files)

            Nothing has been changed on disk yet.
            """);
        return 0;
    }

    private static int CommandApply(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        string planPath = DefaultPlanPath;
        string? quarantine = null;
        bool execute = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--plan": planPath = args[++i]; break;
                case "--quarantine": quarantine = args[++i]; break;
                case "--execute": execute = true; break;
            }
        }

        if (!File.Exists(planPath)) return Fail($"No plan at {planPath}. Run 'mediatool plan' first.");
        if (quarantine is null) return Fail("apply needs --quarantine <folder> to move files into.");

        using var db = CatalogDatabase.Open(dbPath);
        var rows = PlanCsv.Read(planPath);
        if (rows.Count == 0) return Fail("The plan has no rows.");

        int toMove = rows.Count(r => r.Action == PlannedAction.Quarantine);
        Console.WriteLine($"Plan: {planPath}");
        Console.WriteLine($"  {toMove:N0} files to quarantine into {Path.GetFullPath(quarantine)}");
        Console.WriteLine(execute
            ? "  MODE: executing - files will be moved (not deleted)\n"
            : "  MODE: dry run - verifying only, nothing will move. Add --execute to act.\n");

        var executor = new PlanExecutor(db);
        var result = executor.Execute(rows, quarantine, dryRun: !execute, log: Console.WriteLine, ct);

        Console.WriteLine($"""

            Result
              batch             {result.BatchId}
              {(execute ? "moved" : "would move")}             {result.Moved:N0}
              {(execute ? "freed" : "would free")}             {FormatBytes(result.BytesFreed)}
            """);

        if (result.VerificationFailed > 0)
            Console.WriteLine($"  VERIFICATION FAIL {result.VerificationFailed:N0} - these were NOT touched");
        if (result.Errors > 0)
            Console.WriteLine($"  errors            {result.Errors:N0}");

        foreach (string problem in result.Problems.Take(20)) Console.WriteLine($"    {problem}");
        if (result.Problems.Count > 20)
            Console.WriteLine($"    ... and {result.Problems.Count - 20:N0} more");

        if (execute && result.Moved > 0)
            Console.WriteLine($"""

                Files were MOVED, not deleted. To reverse this batch:
                  mediatool undo --batch {result.BatchId}

                Once you are satisfied, delete the quarantine folder yourself.
                """);

        return 0;
    }

    private static int CommandUndo(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        string? batch = null;
        string? manifest = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--batch": batch = args[++i]; break;
                case "--manifest": manifest = args[++i]; break;
            }
        }

        using var db = CatalogDatabase.Open(dbPath);

        // Restoring from the manifest needs nothing but the quarantine folder itself, so a
        // lost or moved catalog never means a lost undo.
        if (manifest is not null)
        {
            if (!File.Exists(manifest)) return Fail($"No manifest at {manifest}.");

            var restored = new PlanExecutor(db).UndoFromManifest(manifest, Console.WriteLine, ct);
            Console.WriteLine($"  restored          {restored.Moved:N0}  ({FormatBytes(restored.BytesFreed)})");
            if (restored.Errors > 0) Console.WriteLine($"  errors            {restored.Errors:N0}");
            foreach (string problem in restored.Problems.Take(20)) Console.WriteLine($"    {problem}");
            return 0;
        }

        if (batch is null)
        {
            using var list = db.Connection.CreateCommand();
            list.CommandText = """
                SELECT batch_id, COUNT(*), SUM(size), MIN(acted_utc)
                FROM actions WHERE state='done' GROUP BY batch_id ORDER BY batch_id DESC LIMIT 20
                """;
            using var reader = list.ExecuteReader();
            Console.WriteLine($"{"BATCH",-18} {"FILES",8} {"SIZE",10}  WHEN");
            bool any = false;
            while (reader.Read())
            {
                any = true;
                Console.WriteLine($"{reader.GetString(0),-18} {reader.GetInt64(1),8:N0} " +
                                  $"{FormatBytes(reader.GetInt64(2)),10}  {reader.GetString(3)}");
            }
            if (!any) Console.WriteLine("(no batches to undo)");
            Console.WriteLine("\nRun: mediatool undo --batch <BATCH>");
            return 0;
        }

        var result = new PlanExecutor(db).Undo(batch, Console.WriteLine, ct);
        Console.WriteLine($"  restored          {result.Moved:N0}  ({FormatBytes(result.BytesFreed)})");
        if (result.Errors > 0) Console.WriteLine($"  errors            {result.Errors:N0}");
        foreach (string problem in result.Problems.Take(20)) Console.WriteLine($"    {problem}");
        return 0;
    }

    private static long TotalPresentFiles(CatalogDatabase db)
    {
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files WHERE present=1";
        return (long)cmd.ExecuteScalar()!;
    }


    // ---- history / purge -------------------------------------------------

    private static int CommandHistory(string[] args)
    {
        string dbPath = DefaultDbPath;
        var retention = QuarantinePurger.DefaultRetention;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--db") dbPath = args[++i];
            else if (args[i] == "--retention") retention = ParseDuration(args[++i]);
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}.");

        using var db = CatalogDatabase.Open(dbPath);
        var batches = new QuarantinePurger(db).ListBatches();

        if (batches.Count == 0)
        {
            Console.WriteLine("Nothing has been quarantined yet.");
            return 0;
        }

        Console.WriteLine($"{"BATCH",-18} {"FILES",8} {"SIZE",10} {"AGE",12}  STATUS");
        Console.WriteLine(new string('-', 78));

        foreach (var b in batches)
        {
            string status = b.IsRipe(retention)
                ? "can be purged  ·  UNDO STILL WORKS until you purge"
                : $"undoable  ·  purge allowed in {QuarantinePurger.FormatAge(retention - b.Age)}";
            if (b.Missing > 0) status += $"  ({b.Missing:N0} already gone)";

            Console.WriteLine($"{b.BatchId,-18} {b.Files,8:N0} {FormatBytes(b.Bytes),10} " +
                              $"{QuarantinePurger.FormatAge(b.Age),12}  {status}");
        }

        Console.WriteLine($"""

            Every batch above can still be put back:
              mediatool undo --batch <BATCH>

            If this catalog is ever lost, the same restore works from the manifest.csv
            written inside each quarantine folder:
              mediatool undo --manifest <folder>\<BATCH>\manifest.csv

            Retention is {QuarantinePurger.FormatAge(retention)}. Files are only removed for good by
            'mediatool purge', never automatically.
            """);
        return 0;
    }

    private static int CommandPurge(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        string? batch = null, quarantine = null;
        var retention = QuarantinePurger.DefaultRetention;
        bool execute = false, yes = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--batch": batch = args[++i]; break;
                case "--quarantine": quarantine = args[++i]; break;
                case "--retention": retention = ParseDuration(args[++i]); break;
                case "--execute": execute = true; break;
                case "--yes": yes = true; break;
            }
        }

        if (batch is null) return Fail("purge needs --batch <id>. Run 'mediatool history' to see them.");
        if (quarantine is null) return Fail("purge needs --quarantine <folder>, the folder the batch was moved into.");
        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}.");

        using var db = CatalogDatabase.Open(dbPath);
        var purger = new QuarantinePurger(db);

        var dryRun = purger.Purge(batch, quarantine, retention, dryRun: true, null, ct);
        foreach (string problem in dryRun.Problems.Take(20)) Console.WriteLine($"  {problem}");

        if (dryRun.Deleted == 0)
        {
            Console.WriteLine("Nothing would be purged.");
            return dryRun.Errors > 0 ? 1 : 0;
        }

        Console.WriteLine($"""

            Batch {batch}
              would delete   {dryRun.Deleted:N0} files
              would free     {FormatBytes(dryRun.BytesFreed)}

            THIS IS PERMANENT. After purging, 'mediatool undo --batch {batch}' can no
            longer bring these files back.
            """);

        if (!execute)
        {
            Console.WriteLine(Environment.NewLine + "Dry run. Add --execute to actually delete.");
            return 0;
        }

        // Typing the batch id is deliberate friction: this is the only irreversible action
        // in the program, and a mistyped flag should not be enough to trigger it.
        if (!yes)
        {
            Console.Write(Environment.NewLine + $"Type the batch id to confirm ({batch}): ");
            string? typed = Console.ReadLine();
            if (typed?.Trim() != batch)
            {
                Console.WriteLine("Did not match. Nothing was deleted.");
                return 1;
            }
        }

        var result = purger.Purge(batch, quarantine, retention, dryRun: false, Console.WriteLine, ct);

        Console.WriteLine(Environment.NewLine + $"  deleted        {result.Deleted:N0}");
        Console.WriteLine($"  freed          {FormatBytes(result.BytesFreed)}");
        if (result.Skipped > 0) Console.WriteLine($"  already gone   {result.Skipped:N0}");
        if (result.Errors > 0)
        {
            Console.WriteLine($"  REFUSED        {result.Errors:N0}");
            foreach (string problem in result.Problems.Take(20)) Console.WriteLine($"    {problem}");
        }
        return 0;
    }

    /// <summary>Parses "14d", "36h", "30m". Bare numbers are days.</summary>
    private static TimeSpan ParseDuration(string text)
    {
        text = text.Trim().ToLowerInvariant();
        char unit = text.Length > 0 ? text[^1] : 'd';
        string number = char.IsDigit(unit) ? text : text[..^1];
        if (!double.TryParse(number, CultureInfo.InvariantCulture, out double value)) return QuarantinePurger.DefaultRetention;

        return unit switch
        {
            'h' => TimeSpan.FromHours(value),
            'm' => TimeSpan.FromMinutes(value),
            _ => TimeSpan.FromDays(value),
        };
    }


    // ---- merge-exif ------------------------------------------------------

    private static int CommandMergeExif(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        string planPath = DefaultPlanPath;
        string? quarantine = null;
        bool execute = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--plan": planPath = args[++i]; break;
                case "--quarantine": quarantine = args[++i]; break;
                case "--execute": execute = true; break;
            }
        }

        if (!File.Exists(planPath)) return Fail($"No plan at {planPath}. Run 'mediatool plan' first.");
        if (quarantine is null) return Fail("merge-exif needs --quarantine <folder> to back originals up into.");

        using var db = CatalogDatabase.Open(dbPath);
        var rows = PlanCsv.Read(planPath);

        var merger = new MetadataMerger(db);
        var candidates = merger.FindCandidates(rows);

        if (candidates.Count == 0)
        {
            Console.WriteLine("No group has a keeper missing a capture date that a discarded copy still holds.");
            return 0;
        }

        Console.WriteLine($"{candidates.Count:N0} keeper(s) can recover a capture date:" + Environment.NewLine);
        foreach (var c in candidates.Take(10))
        {
            Console.WriteLine($"  {c.DonorDate:yyyy-MM-dd HH:mm}  {c.DonorCamera}");
            Console.WriteLine($"    into  {c.Keeper.FullPath}");
            Console.WriteLine($"    from  {c.Donor.Name}");
        }
        if (candidates.Count > 10) Console.WriteLine($"  ... and {candidates.Count - 10:N0} more");

        Console.WriteLine(Environment.NewLine + (execute
            ? "  MODE: executing — the original of each keeper is quarantined first, so this is undoable"
            : "  MODE: dry run — nothing will be written. Add --execute to act."));

        var result = merger.Merge(candidates, quarantine, dryRun: !execute, Console.WriteLine, ct);

        Console.WriteLine(Environment.NewLine + $"  {(execute ? "merged" : "would merge")}   {result.Merged:N0}");
        if (result.Skipped > 0) Console.WriteLine($"  skipped        {result.Skipped:N0}");
        if (result.Errors > 0)
        {
            Console.WriteLine($"  REFUSED        {result.Errors:N0}");
            foreach (string problem in result.Problems.Take(20)) Console.WriteLine($"    {problem}");
        }

        if (execute && result.Merged > 0)
            Console.WriteLine($"""

                Every original was moved into the quarantine folder before being replaced,
                so this is reversible in full:
                  mediatool undo --batch {result.BatchId}

                Run 'mediatool apply' next to quarantine the duplicates themselves.
                """);

        return 0;
    }


    // ---- folders ---------------------------------------------------------

    private static int CommandFolders(string[] args)
    {
        string dbPath = DefaultDbPath;
        int depth = 3;
        int top = 25;
        var scope = new CatalogScope();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--depth": depth = int.Parse(args[++i]); break;
                case "--top": top = int.Parse(args[++i]); break;
                case "--under": scope.Under.Add(args[++i]); break;
                case "--exclude": scope.Exclude.Add(args[++i]); break;
            }
        }

        if (!File.Exists(dbPath)) return Fail($"No catalog at {dbPath}. Run 'mediatool scan' first.");

        using var db = CatalogDatabase.Open(dbPath);
        Console.WriteLine($"Catalog: {db.Path}");
        if (!scope.IsEmpty) Console.WriteLine($"  scope: {scope}");
        Console.WriteLine();

        var folders = new FolderReport(db).Build(depth, scope);
        if (folders.Count == 0)
        {
            Console.WriteLine("No duplicates found in any folder. Run 'mediatool hash' and " +
                              "'mediatool images' first if you have not.");
            return 0;
        }

        Console.WriteLine($"{"RECLAIMABLE",12} {"PROVABLE",9} {"FILES",8} {"DUP",8}  FOLDER");
        Console.WriteLine(new string('-', 96));

        foreach (var f in folders.Take(top))
            Console.WriteLine($"{FormatBytes(f.ReclaimableBytes),12} {f.ProvableShare,8:P0} " +
                              $"{f.Files,8:N0} {f.RedundantShare,8:P0}  {f.Volume}{f.Folder}");

        var safest = folders
            .Where(f => f.ProvableShare >= 0.9 && f.ExactBytes > 100L * 1024 * 1024)
            .OrderByDescending(f => f.ExactBytes)
            .FirstOrDefault();

        Console.WriteLine("""

            PROVABLE is the share of the reclaimable space that comes from byte-identical
            copies. At 100% every decision in that folder can be checked by comparing bytes,
            so nothing rests on a judgement about whether two photos are "the same".
            """);

        if (safest is not null)
            Console.WriteLine($"""
                A good folder to try first — mostly provable, and worth doing:

                  {safest.Volume}{safest.Folder}
                  {FormatBytes(safest.ExactBytes)} in byte-identical copies, {safest.ProvableShare:P0} provable

                  mediatool plan --exact --under "{safest.Folder}" --out first-run.csv
                  mediatool apply --plan first-run.csv --quarantine <folder>
                """);

        return 0;
    }


    // ---- hardlink --------------------------------------------------------

    private static int CommandHardlink(string[] args, CancellationToken ct)
    {
        string dbPath = DefaultDbPath;
        string planPath = DefaultPlanPath;
        string? quarantine = null;
        string? undoBatch = null;
        bool execute = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--plan": planPath = args[++i]; break;
                case "--quarantine": quarantine = args[++i]; break;
                case "--undo": undoBatch = args[++i]; break;
                case "--execute": execute = true; break;
            }
        }

        using var db = CatalogDatabase.Open(dbPath);
        var executor = new HardlinkExecutor(db);

        if (undoBatch is not null)
        {
            var undone = executor.Undo(undoBatch, Console.WriteLine, ct);
            Console.WriteLine($"  restored          {undone.Linked:N0}  ({FormatBytes(undone.BytesFreed)})");
            if (undone.Skipped > 0) Console.WriteLine($"  already restored  {undone.Skipped:N0}");
            if (undone.Errors > 0) Console.WriteLine($"  errors            {undone.Errors:N0}");
            foreach (string problem in undone.Problems.Take(20)) Console.WriteLine($"    {problem}");
            return 0;
        }

        if (!File.Exists(planPath)) return Fail($"No plan at {planPath}. Run 'mediatool plan --exact' first.");
        if (quarantine is null) return Fail("hardlink needs --quarantine <folder> to keep the originals in.");

        var rows = PlanCsv.Read(planPath);
        int candidates = rows.Count(r => r.Action == PlannedAction.Quarantine && r.Kind == GroupKind.ExactBytes);
        if (candidates == 0)
            return Fail("This plan has no byte-identical duplicates. Hardlinking is only safe for those — " +
                        "run 'mediatool plan --exact'.");

        Console.WriteLine($"Plan: {planPath}");
        Console.WriteLine($"  {candidates:N0} byte-identical duplicates could become hardlinks");
        Console.WriteLine(execute
            ? "  MODE: executing"
            : "  MODE: dry run - checking only. Add --execute to act.");

        Console.WriteLine("""

            A hardlink makes two paths one file. Both keep working and both keep appearing
            in their folders, but the disk stores the content once. Nothing is removed.

            Two things to know before choosing this over quarantining:
              - editing either path IN PLACE changes both. Most photo software writes a new
                file instead, but not all of it does.
              - the duplicate's own timestamps are replaced by the kept file's.
            """);

        var result = executor.Execute(rows, quarantine, dryRun: !execute, Console.WriteLine, ct);

        Console.WriteLine(Environment.NewLine + $"  {(execute ? "linked" : "would link")}         {result.Linked:N0}");
        Console.WriteLine($"  {(execute ? "freed" : "would free")}          {FormatBytes(result.BytesFreed)}");
        if (result.Skipped > 0) Console.WriteLine($"  skipped         {result.Skipped:N0}  (different volume, wrong filesystem, or already linked)");
        if (result.VerificationFailed > 0) Console.WriteLine($"  VERIFICATION    {result.VerificationFailed:N0} - not touched");
        if (result.Errors > 0) Console.WriteLine($"  errors          {result.Errors:N0}");
        foreach (string problem in result.Problems.Take(15)) Console.WriteLine($"    {problem}");

        if (execute && result.Linked > 0)
            Console.WriteLine($"""

                Each original was moved into the quarantine folder before its path became a
                link, so this is fully reversible:
                  mediatool hardlink --undo {result.BatchId} --db "{dbPath}"
                """);

        return 0;
    }


    // ---- review decisions -------------------------------------------------

    /// <summary>
    /// What the review app has been told, and a way to take it back.
    ///
    /// Decisions are the one thing in the catalog a machine did not produce, so there has to
    /// be a way to look at them from outside the app that made them - and a way to start
    /// over without deleting a catalog that also holds hours of scanning.
    /// </summary>
    private static int CommandReview(string[] args)
    {
        string dbPath = DefaultDbPath;
        bool clear = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--clear": clear = true; break;
                default: return Fail($"Unknown option '{args[i]}' for review.");
            }
        }

        using var db = CatalogDatabase.Open(dbPath);
        var decisions = new ReviewDecisions(db);

        int count = decisions.Count();
        if (count == 0)
        {
            Console.WriteLine("No review decisions are stored.");
            return 0;
        }

        if (!clear)
        {
            var all = decisions.LoadAll();
            int confirmed = all.Values.Count(d => d.State == ReviewDecisionState.Confirmed);

            Console.WriteLine($"""
                {count:N0} review decision(s) stored
                  confirmed  {confirmed:N0}   (Apply will act on these)
                  skipped    {count - confirmed:N0}

                A decision only applies to a cluster holding exactly the same files, so one
                whose cluster no longer exists is simply ignored.

                Start the review over with: winnow-cli review --clear
                """);
            return 0;
        }

        foreach (string key in decisions.LoadAll().Keys) decisions.Forget(key);
        Console.WriteLine($"Cleared {count:N0} review decision(s). Nothing on disk was touched.");
        return 0;
    }

    // ---- shell integration -----------------------------------------------

    private static int CommandShell(string[] args)
    {
        string action = args.Length > 0 ? args[0].ToLowerInvariant() : "status";

        // The GUI is what a right-click should open, not the console app.
        // The console tool is winnow-cli.exe and the app is Winnow.exe. They must not share
        // a name: Windows paths are case-insensitive, so a right-click would otherwise
        // launch the console app and show nothing.
        string appExe = ShellIntegration.FindAppExecutable(AppContext.BaseDirectory)
                        ?? Path.Combine(AppContext.BaseDirectory, ShellIntegration.AppExeName);

        string icon = Path.Combine(AppContext.BaseDirectory, "winnow.ico");
        if (!File.Exists(icon))
        {
            string repoIcon = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "assets", "winnow.ico"));
            if (File.Exists(repoIcon)) icon = repoIcon;
        }

        switch (action)
        {
            case "install":
                if (!File.Exists(appExe))
                    return Fail($"Could not find Winnow.exe next to this tool ({appExe}). Build the app first.");

                ShellIntegration.InstallContextMenu(appExe, icon);
                string desktop = ShellIntegration.CreateShortcut(appExe, icon, Environment.SpecialFolder.DesktopDirectory);
                string start = ShellIntegration.CreateShortcut(appExe, icon, Environment.SpecialFolder.Programs);

                Console.WriteLine($"""
                    Installed for the current user only - no administrator rights were used,
                    and nothing was changed for other accounts on this machine.

                      right-click menu   folders, drives, and the empty space inside a folder
                      shortcut           {desktop}
                      shortcut           {start}
                      opens              {appExe}

                    On Windows 11 the entry appears under "Show more options" (Shift+F10 goes
                    straight there). Getting into the short menu requires a packaged app,
                    which is a different kind of build.

                    Remove it all again with: winnow-cli shell uninstall
                    """);
                return 0;

            case "uninstall":
                ShellIntegration.RemoveContextMenu();
                ShellIntegration.RemoveShortcuts();
                Console.WriteLine("Removed the right-click entries and both shortcuts. Nothing else was touched.");
                return 0;

            case "status":
                var status = ShellIntegration.Status(appExe);
                Console.WriteLine($"  right-click menu   {(status.ContextMenu ? "installed" : "not installed")}");
                Console.WriteLine($"  desktop shortcut   {(status.DesktopShortcut ? "installed" : "not installed")}");
                Console.WriteLine($"  start menu         {(status.StartMenuShortcut ? "installed" : "not installed")}");
                if (status.RegisteredExe is not null)
                    Console.WriteLine($"  opens              {status.RegisteredExe}");
                return 0;

            default:
                return Fail("shell takes install, uninstall or status.");
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static long ParseSize(string text)
    {
        text = text.Trim().ToUpperInvariant();
        long multiplier = 1;
        if (text.EndsWith("KB")) { multiplier = 1024; text = text[..^2]; }
        else if (text.EndsWith("MB")) { multiplier = 1024 * 1024; text = text[..^2]; }
        else if (text.EndsWith("GB")) { multiplier = 1024L * 1024 * 1024; text = text[..^2]; }
        else if (text.EndsWith("K")) { multiplier = 1024; text = text[..^1]; }
        else if (text.EndsWith("M")) { multiplier = 1024 * 1024; text = text[..^1]; }
        return long.Parse(text, CultureInfo.InvariantCulture) * multiplier;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : "..." + text[^(max - 3)..];
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static void PrintUsage() => Console.WriteLine("""
        mediatool - large-scale image duplicate finder (v0.1: catalog stage)

        USAGE
          mediatool volumes
              List attached volumes with their GUID identity and HDD/SSD classification.

          mediatool scan <path> [<path>...] [options]
              Walk the given roots and catalog every image file.
              Interrupt with Ctrl+C and re-run the same command to continue.

              --db <file>        catalog location (default: %LOCALAPPDATA%\Winnow\catalog.db,
                                 falling back to an existing media-tool\catalog.db)
              --ext .jpg,.png    only these extensions
              --all              every file, not just images
              --min-size <n>     skip files below this (default 16KB; accepts 500KB, 2MB)
              --include-cloud    also read cloud placeholders - see the warning it prints
              --include-hidden   do not skip hidden files and folders
              --no-resume        start a fresh scan instead of continuing an interrupted one
              --all-drives       scan every attached local drive; prints what it picked and
                                 what it held back, and never touches a cloud sync drive
                                 unless --include-cloud-drives is also given
              --exclude <text>   skip any folder whose path contains this, repeatable
                                 (e.g. --exclude _mvc --exclude wp-content)

          mediatool hash [--db <file>]
              Run the exact-duplicate cascade over the catalog: probe 64KB from each end
              of every size-collision candidate, then fully hash only what still collides.
              Resumable - already-hashed files are skipped, changed files are re-hashed.

          mediatool duplicates [--db <file>] [--top 20] [--csv <file>] [--min-size <n>]
              Report byte-identical groups, largest reclaimable saving first.
              Reports only; nothing is moved or deleted.

          mediatool images [--db <file>] [--retry-failed]
              Decode every catalogued image once and record: the exact-picture hash,
              two perceptual hashes, and a 16x16 thumbnail used to confirm matches.
              Resumable - already-decoded, unchanged files are skipped.

          mediatool metadata [--db <file>] [--under <path>] [--exclude <text>]
              Read EXIF for catalogued files and cache it. Cheap - a few hundred KB from
              the front of each file, no decoding. Run this BEFORE 'similar': the capture
              timestamp is what stops burst frames being clustered as duplicates.

          mediatool similar [--db <file>] [--top 15] [--csv <file>]
                            [--hamming 4] [--mae 8] [--min-contrast 6] [--pixel-only]
              Cluster images that are the same picture.
                --pixel-only  only exact same picture (catches stripped metadata,
                              no threshold, no false positives)
                --hamming     bits that may differ out of 64 (default 4)
                --mae         thumbnail difference allowed, 0-255 (default 8)
                --under       only files under this path, repeatable
                --exclude     drop files whose path contains this, repeatable
              Prints a distance histogram so the thresholds can be tuned to your data.
              Reports only; nothing is moved or deleted.

          mediatool plan [--db <file>] [--out plan.csv] [--exact|--pixel|--similar]
                         [--min-save <n>] [--prefer <path fragment>] [--include-offline]
              Decide which copy of each duplicate to keep and write a reviewable plan.
                --exact    byte-identical groups (default, safest)
                --pixel    identical picture, different bytes - the stripped-metadata case
                --similar  near duplicates; apply refuses these, review by hand
                --prefer   a path fragment marking your curated library, repeatable
                --under    only plan for files under this path, repeatable
                --exclude  drop files whose path contains this, repeatable
              Keeps the copy with the most metadata, then the highest resolution.
              Nothing is moved.

          mediatool apply --plan <file> --quarantine <folder> [--execute]
              Carry out a plan. Dry run unless --execute is given. Every file is
              re-verified against its keeper on disk before it is touched, and files
              are MOVED into the quarantine folder, never deleted.

          mediatool undo [--batch <id>] [--manifest <file>]
              List applied batches, or put one back. --manifest restores from the CSV
              inside the quarantine folder, so a lost catalog is not a lost undo.

          mediatool merge-exif --plan <file> --quarantine <folder> [--execute]
              Where the copy being kept has lost its capture date and a copy being
              discarded still has it, move the EXIF across before anything is removed.
              A byte-level splice: no decoding, no re-encoding, pixels untouched. The
              original is quarantined first, so it is undoable like everything else.
              Run this BEFORE 'apply'.

          mediatool hardlink --plan <file> --quarantine <folder> [--execute]
          mediatool hardlink --undo <batch-id>
              Free the space a byte-identical duplicate takes without removing it: both
              paths stay, pointing at one file. Only for --exact plans and only within a
              single NTFS volume. The original is kept in the quarantine folder, so it is
              reversible. Editing either path in place will change both.

          mediatool history [--retention 14d]
              Every quarantine batch: how old it is, how long until it may be purged,
              and the exact command that would put it back.

          mediatool purge --batch <id> --quarantine <folder> [--retention 14d] [--execute]
              PERMANENTLY delete one quarantined batch. The only irreversible command
              here. Refuses a batch younger than the retention period, refuses any path
              outside the quarantine folder, refuses a file whose size has changed, is a
              dry run unless --execute, and asks you to type the batch id.

          mediatool review [--db <file>] [--clear]
              Show the decisions made in the review app, or clear them to start over.
              Reports only; --clear removes decisions, never files.

          mediatool folders [--depth 3] [--top 25] [--under <path>] [--exclude <text>]
              Where the duplicates actually are, folder by folder, and how much of each
              folder's saving is provable rather than a judgement call. Answers the
              question that comes first: which folder is safe to try this on.

          winnow-cli shell install | uninstall | status
              Add Winnow to Explorer's right-click menu for folders and drives, and put
              a shortcut on the desktop and in the Start menu. Current user only, so no
              administrator prompt and nothing changes for other accounts.

          mediatool stats [--db <file>]
              Summarise the catalog and show how much data the duplicate cascade
              would actually need to read.

        NOTES
          Scans are catalogued per volume GUID, so a disk keeps its identity across
          replugs and drive-letter changes, and stays in the catalog while unplugged.
        """);
}
