using MediaTool.Core.Storage;

namespace MediaTool.Core.Dedupe;

public sealed class FolderSummary
{
    public required string Volume { get; init; }
    public required string Folder { get; init; }

    public long Files { get; set; }
    public long Bytes { get; set; }

    /// <summary>Redundant copies that are byte-identical to another file. Provably safe to act on.</summary>
    public long ExactRedundant { get; set; }
    public long ExactBytes { get; set; }

    /// <summary>Redundant copies that are the same picture in a different container — the stripped-metadata case.</summary>
    public long PictureRedundant { get; set; }
    public long PictureBytes { get; set; }

    public long Redundant => ExactRedundant + PictureRedundant;
    public long ReclaimableBytes => ExactBytes + PictureBytes;

    /// <summary>
    /// Share of the reclaimable space that comes from provable duplicates rather than from
    /// judgement. A folder at 100% can be cleaned without looking at a single photo.
    /// </summary>
    public double ProvableShare => ReclaimableBytes == 0
        ? 1.0
        : (double)ExactBytes / ReclaimableBytes;

    public double RedundantShare => Files == 0 ? 0 : (double)Redundant / Files;
}

/// <summary>
/// Where the duplicates actually are, folder by folder.
///
/// Written for the question that comes before any of the others: which folder is safe to try
/// this on first. A library that has never been deduplicated is not uniformly risky — some
/// folders are nested backups full of byte-identical copies, where the tool can prove every
/// decision, and others are working folders where telling a duplicate from a similar photo
/// takes a person. Knowing which is which is what turns "point it at everything and hope"
/// into a decision.
///
/// Reads only. Nothing here touches a file.
/// </summary>
public sealed class FolderReport
{
    private readonly CatalogDatabase _db;

    public FolderReport(CatalogDatabase db) => _db = db;

    public List<FolderSummary> Build(int depth, CatalogScope scope)
    {
        var summaries = new Dictionary<string, FolderSummary>(StringComparer.OrdinalIgnoreCase);

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            WITH present AS (
                SELECT f.file_key, f.rel_path, f.size, f.content_hash, f.pixel_hash,
                       COALESCE(v.last_mount_point, v.volume_guid) AS volume
                FROM files f JOIN volumes v ON v.volume_id = f.volume_id
                WHERE f.present = 1{scope.ToSqlPredicate("f")}
            ),
            -- Within each group of identical files one copy is kept; the rest are the
            -- redundancy, and each is counted against the folder it actually sits in.
            exact_rank AS (
                SELECT file_key,
                       ROW_NUMBER() OVER (PARTITION BY content_hash ORDER BY file_key) AS rn
                FROM present WHERE content_hash IS NOT NULL
            ),
            picture_rank AS (
                SELECT file_key,
                       ROW_NUMBER() OVER (PARTITION BY pixel_hash ORDER BY file_key) AS rn
                FROM present WHERE pixel_hash IS NOT NULL
            )
            SELECT p.volume, p.rel_path, p.size,
                   COALESCE(e.rn, 1) AS exact_rn,
                   COALESCE(pi.rn, 1) AS picture_rn,
                   p.content_hash IS NOT NULL AS hashed
            FROM present p
            LEFT JOIN exact_rank e ON e.file_key = p.file_key
            LEFT JOIN picture_rank pi ON pi.file_key = p.file_key
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string volume = reader.GetString(0);
            string folder = FolderAt(reader.GetString(1), depth);
            long size = reader.GetInt64(2);
            long exactRank = reader.GetInt64(3);
            long pictureRank = reader.GetInt64(4);

            string key = volume + "|" + folder;
            if (!summaries.TryGetValue(key, out var summary))
                summaries[key] = summary = new FolderSummary { Volume = volume, Folder = folder };

            summary.Files++;
            summary.Bytes += size;

            if (exactRank > 1)
            {
                summary.ExactRedundant++;
                summary.ExactBytes += size;
            }
            else if (pictureRank > 1)
            {
                // Same picture, different bytes — real redundancy, but only visible after a
                // decode, which is a weaker kind of evidence than a byte comparison.
                summary.PictureRedundant++;
                summary.PictureBytes += size;
            }
        }

        return summaries.Values
            .Where(s => s.ReclaimableBytes > 0)
            .OrderByDescending(s => s.ReclaimableBytes)
            .ToList();
    }

    /// <summary>The first <paramref name="depth"/> path segments, which is the folder being reported on.</summary>
    private static string FolderAt(string relativePath, int depth)
    {
        var parts = relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return "(volume root)";

        int take = Math.Min(depth, parts.Length - 1);
        return string.Join('\\', parts.Take(take));
    }
}
