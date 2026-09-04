using System.Globalization;
using MediaTool.Core.Storage;
using MediaTool.Core.Util;

namespace MediaTool.Core.Actions;

public sealed class QuarantineBatch
{
    public required string BatchId { get; init; }
    public required DateTime ActedUtc { get; init; }
    public required int Files { get; init; }
    public required long Bytes { get; init; }

    /// <summary>Quarantined files that are no longer where the manifest says they are.</summary>
    public required int Missing { get; init; }

    public TimeSpan Age => DateTime.UtcNow - ActedUtc;
    public bool IsRipe(TimeSpan retention) => Age >= retention;
}

public sealed class PurgeResult
{
    public string BatchId = "";
    public int Deleted;
    public int Skipped;
    public int Errors;
    public long BytesFreed;
    public List<string> Problems = [];
}

/// <summary>
/// The one place in this program that can permanently remove a file, and the only reason it
/// is allowed to exist: quarantine has to end somewhere, or the disk never actually frees.
///
/// Everything about it is built to make an accident impossible rather than unlikely:
///
///   - it deletes only files the catalog itself recorded moving, in a completed batch;
///   - it re-checks that each path really is inside the quarantine folder before touching it,
///     so a corrupted or hand-edited record cannot redirect it at the original library;
///   - it refuses a batch younger than the retention period, so a mistake has days to be
///     noticed rather than seconds;
///   - it confirms the file's size still matches what was recorded, so it never removes
///     something that has been replaced since;
///   - and it defaults to a dry run, like every other destructive step here.
///
/// Once a batch is purged, undo can no longer restore it. That is the whole point of the
/// waiting period before it.
/// </summary>
public sealed class QuarantinePurger
{
    /// <summary>How long a batch must sit before it may be purged, unless overridden.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(14);

    private readonly CatalogDatabase _db;

    public QuarantinePurger(CatalogDatabase db) => _db = db;

    public List<QuarantineBatch> ListBatches()
    {
        var batches = new List<QuarantineBatch>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT batch_id, COUNT(*), COALESCE(SUM(size),0), MIN(acted_utc)
            FROM actions WHERE state='done'
            GROUP BY batch_id ORDER BY batch_id DESC
            """;

        var rows = new List<(string Id, int Files, long Bytes, string Acted)>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetString(3)));

        foreach (var row in rows)
        {
            int missing = 0;
            foreach (var action in LoadActions(row.Id))
                if (!File.Exists(action.Destination)) missing++;

            batches.Add(new QuarantineBatch
            {
                BatchId = row.Id,
                ActedUtc = DateTime.TryParse(row.Acted, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var acted)
                    ? acted : DateTime.UtcNow,
                Files = row.Files,
                Bytes = row.Bytes,
                Missing = missing,
            });
        }

        return batches;
    }

    /// <summary>
    /// Permanently deletes one quarantined batch.
    /// </summary>
    /// <param name="quarantineRoot">
    /// The folder the batch was moved into. Every file is checked to be inside it before it
    /// is touched; this is the guard that keeps a bad record from reaching the library.
    /// </param>
    /// <param name="retention">A batch younger than this is refused outright.</param>
    public PurgeResult Purge(
        string batchId,
        string quarantineRoot,
        TimeSpan retention,
        bool dryRun,
        Action<string>? log,
        CancellationToken ct)
    {
        var result = new PurgeResult { BatchId = batchId };

        var batch = ListBatches().FirstOrDefault(b => b.BatchId == batchId);
        if (batch is null)
        {
            result.Problems.Add($"No completed batch named {batchId}.");
            return result;
        }

        if (!batch.IsRipe(retention))
        {
            result.Problems.Add(
                $"Batch {batchId} is {FormatAge(batch.Age)} old; the retention period is " +
                $"{FormatAge(retention)}. Refusing — it can still be undone until then.");
            return result;
        }

        string root = Path.GetFullPath(quarantineRoot);
        if (!Directory.Exists(root))
        {
            result.Problems.Add($"Quarantine folder not found: {root}");
            return result;
        }

        foreach (var action in LoadActions(batchId))
        {
            ct.ThrowIfCancellationRequested();

            string destination;
            try
            {
                destination = Path.GetFullPath(action.Destination);
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"unreadable path in record {action.Id}: {ex.Message}");
                continue;
            }

            // The containment check. Nothing outside the quarantine folder is ever a
            // candidate, whatever the record claims.
            if (!IsInside(root, destination))
            {
                result.Errors++;
                result.Problems.Add($"REFUSED — not inside the quarantine folder: {destination}");
                continue;
            }

            if (!File.Exists(destination))
            {
                // Already gone, or restored by an undo. Nothing to do, and not an error.
                result.Skipped++;
                continue;
            }

            long actualSize;
            try
            {
                actualSize = new FileInfo(LongPath.Prefix(destination)).Length;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"could not stat {destination}: {ex.Message}");
                continue;
            }

            if (actualSize != action.Size)
            {
                // Something replaced this file since it was quarantined. Deleting it would
                // be destroying something the tool never examined.
                result.Errors++;
                result.Problems.Add(
                    $"REFUSED — size changed since quarantine ({action.Size:N0} -> {actualSize:N0}): {destination}");
                continue;
            }

            if (dryRun)
            {
                result.Deleted++;
                result.BytesFreed += actualSize;
                continue;
            }

            try
            {
                File.Delete(LongPath.Prefix(destination));
                MarkPurged(action.Id);
                result.Deleted++;
                result.BytesFreed += actualSize;

                if (result.Deleted % 500 == 0) log?.Invoke($"  purged {result.Deleted:N0}");
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} deleting {destination}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> really sits under <paramref name="root"/>.
    /// Compared segment-wise rather than by string prefix, so "C:\Quarantine-old" is not
    /// mistaken for a child of "C:\Quarantine".
    /// </summary>
    internal static bool IsInside(string root, string candidate)
    {
        string normalisedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalisedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        if (normalisedCandidate.Length <= normalisedRoot.Length) return false;
        if (!normalisedCandidate.StartsWith(normalisedRoot, StringComparison.OrdinalIgnoreCase)) return false;

        char boundary = normalisedCandidate[normalisedRoot.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }

    private readonly record struct ActionRecord(long Id, string Destination, long Size);

    private List<ActionRecord> LoadActions(string batchId)
    {
        var actions = new List<ActionRecord>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT action_id, destination, size FROM actions WHERE batch_id=@b AND state='done'";
        cmd.Parameters.AddWithValue("@b", batchId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            actions.Add(new ActionRecord(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));

        return actions;
    }

    private void MarkPurged(long actionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE actions SET state='purged', acted_utc=@utc WHERE action_id=@id";
        cmd.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", actionId);
        cmd.ExecuteNonQuery();
    }

    public static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{age.TotalDays:F0} day(s)"
        : age.TotalHours >= 1 ? $"{age.TotalHours:F0} hour(s)" : $"{age.TotalMinutes:F0} minute(s)";
}
