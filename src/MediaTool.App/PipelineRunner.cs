using System.IO;
using MediaTool.Core.Crawl;
using MediaTool.Core.Imaging;
using MediaTool.Core.Hashing;
using MediaTool.Core.Metadata;
using MediaTool.Core.Scan;
using MediaTool.Core.Storage;

namespace MediaTool.App;

public sealed record PipelineProgress(string Stage, string Detail, double Fraction, int StageIndex, int StageCount);

/// <summary>
/// Runs the four passes a library needs before it can be reviewed, in the order they depend
/// on each other, reporting progress as one continuous job.
///
/// From the command line these are four separate commands, which is right there — each is
/// long enough to want to run overnight, and each resumes on its own. From the app they have
/// to look like one action, because "scan this folder" is one intention. The passes still
/// resume individually underneath, so closing the window mid-way loses nothing.
/// </summary>
public sealed class PipelineRunner
{
    private static readonly string[] Stages =
    [
        "Finding images",     // walk the folders and catalogue what is there
        "Comparing bytes",    // the exact-duplicate cascade
        "Reading pictures",   // decode once, for the pixel and perceptual hashes
        "Reading metadata",   // EXIF, so bursts can be told from duplicates
    ];

    private readonly string _catalogPath;

    public PipelineRunner(string catalogPath) => _catalogPath = catalogPath;

    public async Task RunAsync(
        IReadOnlyList<string> roots,
        IReadOnlyList<string> exclusions,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        using var db = CatalogDatabase.Open(_catalogPath);

        var options = new CrawlOptions();
        foreach (string fragment in exclusions)
            if (!string.IsNullOrWhiteSpace(fragment)) options.ExcludedPathFragments.Add(fragment.Trim());

        // --- 1. crawl -------------------------------------------------------
        for (int i = 0; i < roots.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string root = roots[i];
            var session = new ScanSession(db, options)
            {
                OnProgress = p => Report(progress, 0,
                    $"{p.FilesAccepted:N0} images in {root}",
                    // A crawl has no total to divide by, so the bar shows the roots done so
                    // far and lets the caption carry the detail.
                    (i + 0.5) / roots.Count),
            };

            try
            {
                await session.RunAsync(root, resume: true, ct).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                // A drive unplugged between choosing it and starting. The others still run.
            }
        }

        // --- 2. byte cascade -------------------------------------------------
        ct.ThrowIfCancellationRequested();
        var hashing = new HashPipeline(db)
        {
            OnProgress = p => Report(progress, 1,
                $"{p.FilesDone:N0} of {p.FilesTotal:N0} candidates",
                p.FilesTotal == 0 ? 1 : (double)p.FilesDone / p.FilesTotal),
        };
        await hashing.RunAsync(ct).ConfigureAwait(false);

        // --- 3. decode -------------------------------------------------------
        ct.ThrowIfCancellationRequested();
        var fingerprints = new FingerprintPipeline(db)
        {
            OnProgress = p => Report(progress, 2,
                $"{p.Done:N0} of {p.Total:N0} images",
                p.Total == 0 ? 1 : (double)p.Done / p.Total),
        };
        await fingerprints.RunAsync(ct).ConfigureAwait(false);

        // --- 4. metadata -----------------------------------------------------
        ct.ThrowIfCancellationRequested();
        var metadata = new MetadataPipeline(db)
        {
            OnProgress = p => Report(progress, 3,
                $"{p.Done:N0} of {p.Total:N0} files",
                p.Total == 0 ? 1 : (double)p.Done / p.Total),
        };
        await metadata.RunAsync(new CatalogScope(), ct).ConfigureAwait(false);

        progress.Report(new PipelineProgress("Done", "", 1, Stages.Length, Stages.Length));
    }

    private static void Report(IProgress<PipelineProgress> progress, int stage, string detail, double within) =>
        progress.Report(new PipelineProgress(
            Stages[stage], detail, Math.Clamp(within, 0, 1), stage + 1, Stages.Length));

    /// <summary>How much of the catalog each attached volume already accounts for.</summary>
    public IReadOnlyDictionary<string, (long Files, long Bytes)> CataloguedByVolume()
    {
        var result = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_catalogPath)) return result;

        try
        {
            using var db = CatalogDatabase.Open(_catalogPath);
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT v.volume_guid, COUNT(f.file_key), COALESCE(SUM(f.size), 0)
                FROM volumes v LEFT JOIN files f ON f.volume_id = v.volume_id AND f.present = 1
                GROUP BY v.volume_id
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }
        catch (Exception)
        {
            // A catalog that cannot be opened is simply one with nothing to report yet.
        }

        return result;
    }
}
