using System.IO;

namespace MediaTool.Core.Storage;

public sealed record ResetBlocker(string BatchId, int Files, long Bytes);

public sealed record ResetResult(bool Done, string? ArchivedTo, IReadOnlyList<ResetBlocker> Blockers);

/// <summary>
/// Starting a catalog over.
///
/// A catalog is a record of work, not of decisions: hours of walking, hashing and decoding.
/// Throwing it away is reasonable — the library was reorganised, or the plan is to work on
/// one folder rather than everything — but it should be a deliberate act with a way back,
/// not a file quietly deleted.
///
/// So the old catalog is renamed rather than removed. It costs a few tens of megabytes and
/// buys back every hour that went into it if the reset turns out to be a mistake.
/// </summary>
public static class CatalogReset
{
    /// <summary>
    /// Batches whose files are sitting in quarantine, waiting to be put back or purged.
    ///
    /// These are the reason a reset cannot simply proceed. The catalog is what knows where
    /// each moved file came from; discarding it while files are still moved would leave them
    /// stranded, recoverable only by hand from the manifest in the quarantine folder.
    /// </summary>
    public static IReadOnlyList<ResetBlocker> PendingBatches(string catalogPath)
    {
        var blockers = new List<ResetBlocker>();
        if (!File.Exists(catalogPath)) return blockers;

        try
        {
            using var db = CatalogDatabase.Open(catalogPath);
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = """
                SELECT batch_id, COUNT(*), COALESCE(SUM(size), 0)
                FROM actions WHERE state = 'done'
                GROUP BY batch_id ORDER BY batch_id
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                blockers.Add(new ResetBlocker(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        }
        catch (Exception)
        {
            // A catalog that will not open has nothing to strand; the reset can go ahead.
        }

        return blockers;
    }

    /// <summary>
    /// Moves the current catalog aside so the next open creates an empty one.
    ///
    /// Refuses while any batch is still applied, and says which. The caller is expected to
    /// have shown the same thing to the person first — this check is here because being
    /// asked twice is cheaper than stranding a file.
    /// </summary>
    public static ResetResult Reset(string catalogPath, Func<string, bool>? exists = null, Action<string, string>? move = null)
    {
        exists ??= File.Exists;
        move ??= (from, to) => File.Move(from, to);

        var blockers = PendingBatches(catalogPath);
        if (blockers.Count > 0) return new ResetResult(false, null, blockers);

        if (!exists(catalogPath)) return new ResetResult(true, null, []);

        string archived = ArchiveNameFor(catalogPath, exists);
        move(catalogPath, archived);

        // The write-ahead log and shared-memory files belong to the catalog that just moved.
        // Leaving them behind would have SQLite try to replay them into the new one.
        foreach (string suffix in new[] { "-wal", "-shm" })
            if (exists(catalogPath + suffix)) move(catalogPath + suffix, archived + suffix);

        return new ResetResult(true, archived, []);
    }

    /// <summary>A name beside the catalog that is not already taken.</summary>
    public static string ArchiveNameFor(string catalogPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;

        string directory = Path.GetDirectoryName(catalogPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(catalogPath);
        string extension = Path.GetExtension(catalogPath);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        string candidate = Path.Combine(directory, $"{stem}-before-{stamp}{extension}");
        for (int n = 2; exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem}-before-{stamp}-{n}{extension}");

        return candidate;
    }
}
