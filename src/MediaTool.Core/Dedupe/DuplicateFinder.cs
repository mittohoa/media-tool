using MediaTool.Core.Storage;

namespace MediaTool.Core.Dedupe;

/// <summary>One catalogued path inside a duplicate group.</summary>
public sealed class DuplicateEntry
{
    public required long FileKey { get; init; }
    public required string VolumeName { get; init; }
    public required string VolumeGuid { get; init; }
    public required bool VolumeOnline { get; init; }
    public required string RelativePath { get; init; }
    public required long MTime { get; init; }

    /// <summary>Null when the filesystem supplies no id. Two entries sharing it are one physical file.</summary>
    public (long Volume, long Low, long High)? PhysicalId { get; init; }

    public string FullPath => VolumeName.EndsWith('\\') ? VolumeName + RelativePath : VolumeName + '\\' + RelativePath;
}

/// <summary>A set of byte-identical files, as established by size plus a full content hash.</summary>
public sealed class DuplicateGroup
{
    public required byte[] ContentHash { get; init; }
    public required long Size { get; init; }
    public required List<DuplicateEntry> Entries { get; init; }

    /// <summary>
    /// Distinct physical files in the group. Hardlinks pointing at the same bytes collapse
    /// to one — deleting a hardlink frees nothing, so counting them as copies would inflate
    /// every reclaimable-space figure in the report.
    /// </summary>
    public int PhysicalCopies => Entries
        .Select(e => e.PhysicalId is { } id ? $"{id.Volume}:{id.Low}:{id.High}" : $"path:{e.VolumeGuid}:{e.RelativePath}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public int HardlinkedPaths => Entries.Count - PhysicalCopies;

    /// <summary>Bytes freed by keeping exactly one physical copy.</summary>
    public long ReclaimableBytes => Size * (PhysicalCopies - 1);

    /// <summary>True when at least one copy sits on a disk that is not currently attached.</summary>
    public bool TouchesOfflineVolume => Entries.Any(e => !e.VolumeOnline);
}

public sealed class DuplicateSummary
{
    public long Groups;
    public long RedundantFiles;
    public long ReclaimableBytes;
    public long GroupsTouchingOfflineVolumes;
    public long HardlinkedPaths;
    public long UnhashedCandidates;
}

public sealed class DuplicateFinder
{
    private readonly CatalogDatabase _db;

    public DuplicateFinder(CatalogDatabase db) => _db = db;

    /// <summary>
    /// Streams every group of byte-identical files, largest reclaimable saving first.
    /// </summary>
    public IEnumerable<DuplicateGroup> FindExactDuplicates(ISet<string> onlineVolumeGuids, int minCopies = 2)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.content_hash, f.size, f.file_key, f.rel_path, f.mtime,
                   f.volume_id, f.file_id_low, f.file_id_high,
                   v.volume_guid, COALESCE(v.last_mount_point, v.volume_guid), v.label
            FROM files f
            JOIN volumes v ON v.volume_id = f.volume_id
            WHERE f.present = 1 AND f.content_hash IS NOT NULL
              AND f.content_hash IN (
                  SELECT content_hash FROM files
                  WHERE present = 1 AND content_hash IS NOT NULL
                  GROUP BY content_hash HAVING COUNT(*) > 1
              )
            ORDER BY f.content_hash, f.volume_id, f.rel_path
            """;

        using var reader = cmd.ExecuteReader();

        byte[]? currentHash = null;
        long currentSize = 0;
        var entries = new List<DuplicateEntry>();

        while (reader.Read())
        {
            byte[] hash = (byte[])reader["content_hash"];

            if (currentHash is not null && !hash.AsSpan().SequenceEqual(currentHash))
            {
                var group = Build(currentHash, currentSize, entries);
                if (group.PhysicalCopies >= minCopies) yield return group;
                entries = [];
            }

            currentHash = hash;
            currentSize = reader.GetInt64(1);

            string guid = reader.GetString(8);
            entries.Add(new DuplicateEntry
            {
                FileKey = reader.GetInt64(2),
                RelativePath = reader.GetString(3),
                MTime = reader.GetInt64(4),
                VolumeGuid = guid,
                VolumeName = reader.GetString(9),
                VolumeOnline = onlineVolumeGuids.Contains(guid),
                PhysicalId = reader.IsDBNull(6)
                    ? null
                    : (reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)),
            });
        }

        if (currentHash is not null && entries.Count > 0)
        {
            var group = Build(currentHash, currentSize, entries);
            if (group.PhysicalCopies >= minCopies) yield return group;
        }
    }

    private static DuplicateGroup Build(byte[] hash, long size, List<DuplicateEntry> entries) =>
        new() { ContentHash = hash, Size = size, Entries = entries };

    /// <summary>
    /// Counts candidates that made it through the size cut but were never hashed — an
    /// offline disk, a locked file, a read error. Reporting zero duplicates while silently
    /// having skipped a third of the library would be the worst possible failure here.
    /// </summary>
    public long CountUnhashedCandidates()
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM files f
            JOIN (SELECT size FROM files WHERE present=1 GROUP BY size HAVING COUNT(*)>1) d
              ON d.size = f.size
            WHERE f.present=1 AND f.partial_hash IS NULL
            """;
        return (long)cmd.ExecuteScalar()!;
    }
}
