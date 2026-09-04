using MediaTool.Core.Dedupe;
using MediaTool.Core.Imaging;
using MediaTool.Core.Metadata;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Core.Actions;

public sealed class PlanOptions
{
    public GroupKind Kind { get; set; } = GroupKind.ExactBytes;
    public KeeperOptions Keeper { get; set; } = new();

    /// <summary>Ignore groups whose reclaimable space is below this. Keeps the plan reviewable.</summary>
    public long MinReclaimableBytes { get; set; }

    /// <summary>
    /// Refuse to plan a group that has a copy on a disk which is not currently attached.
    /// On by default: the file cannot be verified, and "there is another copy somewhere" is
    /// not a safe basis for moving anything.
    /// </summary>
    public bool SkipOfflineGroups { get; set; } = true;

    public SimilarityOptions Similarity { get; set; } = new();

    /// <summary>
    /// Which part of the catalog to plan for. Scanning is the expensive step and is done
    /// once over everything; narrowing happens here, where it costs nothing.
    /// </summary>
    public CatalogScope Scope { get; set; } = new();
}

/// <summary>
/// Turns duplicate groups into a reviewable plan: one keeper per group, the rest marked for
/// quarantine, each row carrying the reason it was decided that way.
///
/// Nothing here touches a file. The plan is written out for a human to read — and edit —
/// before <see cref="PlanExecutor"/> is allowed to act on it.
/// </summary>
public sealed class PlanBuilder
{
    /// <summary>
    /// Bumped whenever the metadata reader changes what it can extract. Rows stamped with an
    /// older version are re-read rather than trusted — a catalog that cached "no metadata"
    /// from a broken reader would otherwise keep feeding that to the keeper policy forever.
    /// </summary>
    private const int MetadataVersion = Metadata.MetadataPipeline.MetadataVersion;

    private readonly CatalogDatabase _db;
    private readonly PlanOptions _options;

    public PlanBuilder(CatalogDatabase db, PlanOptions options)
    {
        _db = db;
        _options = options;
    }

    public (List<PlanRow> Rows, PlanSummary Summary) Build(Action<string>? log = null)
    {
        var onlineGuids = VolumeScanner.EnumerateVolumes()
            .Select(v => v.VolumeGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        log?.Invoke("collecting groups");
        var groups = _options.Kind switch
        {
            GroupKind.ExactBytes => LoadHashGroups("content_hash"),
            GroupKind.IdenticalPicture => LoadHashGroups("pixel_hash"),
            GroupKind.NearDuplicate => LoadSimilarGroups(log),
            _ => [],
        };

        log?.Invoke($"reading metadata for {groups.Sum(g => g.Count):N0} files");
        FillMetadata(groups, log);

        log?.Invoke("scoring");
        var rows = new List<PlanRow>();
        var summary = new PlanSummary();
        int index = 0;

        foreach (var group in groups)
        {
            // Hardlinks are one file wearing several names. Deleting one frees nothing, so
            // collapse them to a single candidate before anything is decided.
            var distinct = CollapseHardlinks(group);
            if (distinct.Count < 2) continue;

            // A RAW and the JPEG rendered from it are the same picture but not the same
            // asset, so they are never each other's duplicate. Splitting by format first
            // means a folder holding NEF+JPG twice yields two decisions - drop the second
            // NEF, drop the second JPG - instead of one that discards the negative.
            foreach (var byFormat in distinct.GroupBy(c => c.Tier).Select(g => g.ToList()))
            {
                if (byFormat.Count < 2) continue;
                EmitGroup(byFormat, onlineGuids, rows, summary, ref index);
            }
        }

        return (rows, summary);
    }

    private void EmitGroup(
        List<KeeperCandidate> distinct,
        HashSet<string> onlineGuids,
        List<PlanRow> rows,
        PlanSummary summary,
        ref int index)
    {
        {

            bool anyOffline = distinct.Any(c => !onlineGuids.Contains(c.VolumeGuid));
            if (anyOffline)
            {
                summary.GroupsWithOfflineCopies++;
                if (_options.SkipOfflineGroups) return;
            }

            var ranked = KeeperPolicy.Rank(distinct, _options.Keeper);
            var keeper = ranked[0];
            long reclaimable = ranked.Skip(1).Sum(r => r.Candidate.Size);
            if (reclaimable < _options.MinReclaimableBytes) return;

            index++;
            summary.Groups++;
            summary.ReclaimableBytes += reclaimable;

            // Worth flagging loudly: the copy being kept has less metadata than one being
            // removed. That is the case where blind deduplication destroys information.
            bool keeperPoorer = ranked.Skip(1).Any(r =>
                r.Candidate.ExifTags > keeper.Candidate.ExifTags ||
                (r.Candidate.DateTaken is not null && keeper.Candidate.DateTaken is null));
            if (keeperPoorer) summary.GroupsMissingMetadataOnKeeper++;

            rows.Add(new PlanRow
            {
                Group = index,
                Kind = _options.Kind,
                Action = PlannedAction.Keep,
                File = keeper.Candidate,
                Score = keeper.Score,
                Reason = keeper.Reasons.Count > 0 ? string.Join("; ", keeper.Reasons) : "highest score",
            });
            summary.Keep++;

            foreach (var loser in ranked.Skip(1))
            {
                rows.Add(new PlanRow
                {
                    Group = index,
                    Kind = _options.Kind,
                    Action = PlannedAction.Quarantine,
                    File = loser.Candidate,
                    Score = loser.Score,
                    Reason = DescribeLoss(keeper.Candidate, loser.Candidate),
                    KeptFileKey = keeper.Candidate.FileKey,
                });
                summary.Quarantine++;
            }
        }
    }

    private static string DescribeLoss(KeeperCandidate keeper, KeeperCandidate loser)
    {
        var parts = new List<string>();
        if (loser.Tier > keeper.Tier) parts.Add($"{loser.Tier} ORIGINAL, keeper is only {keeper.Tier}");
        if (loser.Pixels < keeper.Pixels) parts.Add($"lower resolution ({loser.Width}x{loser.Height})");
        if (loser.ExifTags < keeper.ExifTags) parts.Add("less metadata");
        if (loser.ExifTags > keeper.ExifTags) parts.Add("MORE metadata than keeper");
        if (loser.DateTaken is not null && keeper.DateTaken is null) parts.Add("HAS capture date, keeper does not");
        if (loser.JpegQuality is { } lq && keeper.JpegQuality is { } kq && lq < kq) parts.Add($"lower quality (~{lq})");
        return parts.Count > 0 ? string.Join("; ", parts) : "duplicate of keeper";
    }

    /// <summary>Files sharing a volume and file id are one physical file under several names.</summary>
    private static List<KeeperCandidate> CollapseHardlinks(List<KeeperCandidate> group) =>
        group.GroupBy(c => $"{c.VolumeGuid}|{c.RelativePath}", StringComparer.OrdinalIgnoreCase)
             .Select(g => g.First())
             .ToList();

    private List<List<KeeperCandidate>> LoadHashGroups(string column)
    {
        var groups = new List<List<KeeperCandidate>>();

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.file_key, v.volume_guid, COALESCE(v.last_mount_point, v.volume_guid), f.rel_path,
                   f.size, f.mtime, f.img_width, f.img_height, f.content_hash, f.pixel_hash,
                   f.has_exif, f.exif_tags, f.date_taken, f.has_gps, f.jpeg_quality, f.meta_bytes,
                   f.camera, f.{column}
            FROM files f JOIN volumes v ON v.volume_id = f.volume_id
            WHERE f.present = 1 AND f.{column} IS NOT NULL{_options.Scope.ToSqlPredicate("f")}
              AND f.{column} IN (
                  SELECT {column} FROM files f2 WHERE f2.present = 1 AND f2.{column} IS NOT NULL
                  {_options.Scope.ToSqlPredicate("f2")}
                  GROUP BY f2.{column} HAVING COUNT(*) > 1)
            ORDER BY f.{column}, f.rel_path
            """;

        using var reader = cmd.ExecuteReader();
        byte[]? currentKey = null;
        var current = new List<KeeperCandidate>();

        while (reader.Read())
        {
            byte[] key = (byte[])reader[17];
            if (currentKey is not null && !key.AsSpan().SequenceEqual(currentKey))
            {
                if (current.Count > 1) groups.Add(current);
                current = [];
            }
            currentKey = key;
            current.Add(ReadCandidate(reader));
        }
        if (current.Count > 1) groups.Add(current);

        return groups;
    }

    private List<List<KeeperCandidate>> LoadSimilarGroups(Action<string>? log)
    {
        _options.Similarity.Scope = _options.Scope;
        var index = new SimilarityIndex(_db, _options.Similarity);
        var result = index.Build(log);

        var byKey = LoadCandidatesByKey(result.Clusters.SelectMany(c => c.Entries).Select(e => e.FileKey));

        return result.Clusters
            .Select(c => c.Entries.Select(e => byKey.GetValueOrDefault(e.FileKey)).OfType<KeeperCandidate>().ToList())
            .Where(g => g.Count > 1)
            .ToList();
    }

    private Dictionary<long, KeeperCandidate> LoadCandidatesByKey(IEnumerable<long> fileKeys)
    {
        var result = new Dictionary<long, KeeperCandidate>();
        var keys = fileKeys.Distinct().ToList();

        const int chunk = 500;
        for (int start = 0; start < keys.Count; start += chunk)
        {
            var slice = keys.Skip(start).Take(chunk);
            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.file_key, v.volume_guid, COALESCE(v.last_mount_point, v.volume_guid), f.rel_path,
                       f.size, f.mtime, f.img_width, f.img_height, f.content_hash, f.pixel_hash,
                       f.has_exif, f.exif_tags, f.date_taken, f.has_gps, f.jpeg_quality, f.meta_bytes, f.camera
                FROM files f JOIN volumes v ON v.volume_id = f.volume_id
                WHERE f.file_key IN ({string.Join(',', slice)})
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var candidate = ReadCandidate(reader);
                result[candidate.FileKey] = candidate;
            }
        }

        return result;
    }

    private static KeeperCandidate ReadCandidate(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        FileKey = r.GetInt64(0),
        VolumeGuid = r.GetString(1),
        VolumeName = r.GetString(2),
        RelativePath = r.GetString(3),
        Size = r.GetInt64(4),
        MTime = r.GetInt64(5),
        Width = r.IsDBNull(6) ? 0 : r.GetInt32(6),
        Height = r.IsDBNull(7) ? 0 : r.GetInt32(7),
        ContentHash = r.IsDBNull(8) ? null : (byte[])r[8],
        PixelHash = r.IsDBNull(9) ? null : Convert.ToHexString((byte[])r[9]),
        HasExif = !r.IsDBNull(10) && r.GetInt32(10) != 0,
        ExifTags = r.IsDBNull(11) ? 0 : r.GetInt32(11),
        DateTaken = r.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)).UtcDateTime,
        HasGps = !r.IsDBNull(13) && r.GetInt32(13) != 0,
        JpegQuality = r.IsDBNull(14) ? null : r.GetInt32(14),
        MetadataBytes = r.IsDBNull(15) ? 0 : r.GetInt32(15),
        Camera = r.IsDBNull(16) ? null : r.GetString(16),
    };

    /// <summary>
    /// Reads EXIF for the files in the plan and caches it. Only files inside a duplicate
    /// group ever need this, which is a fraction of the catalog — and it reads the front of
    /// each file rather than decoding it, so the pass is cheap.
    /// </summary>
    private void FillMetadata(List<List<KeeperCandidate>> groups, Action<string>? log)
    {
        var candidates = groups.SelectMany(g => g).ToList();
        var pending = new List<KeeperCandidate>();

        using (var check = _db.Connection.CreateCommand())
        {
            check.CommandText = "SELECT meta_state FROM files WHERE file_key = @k";
            check.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@k", 0L));
            foreach (var c in candidates)
            {
                check.Parameters["@k"].Value = c.FileKey;
                if (Convert.ToInt32(check.ExecuteScalar() ?? 0) < MetadataVersion) pending.Add(c);
            }
        }

        if (pending.Count == 0) return;
        log?.Invoke($"  {pending.Count:N0} files need a metadata read");

        var results = new System.Collections.Concurrent.ConcurrentBag<(long Key, ImageMetadata Meta)>();
        Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            c => results.Add((c.FileKey, ImageMetadataReader.Read(c.FullPath))));

        using var tx = _db.Connection.BeginTransaction();
        using var update = _db.Connection.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE files SET has_exif=@e, exif_tags=@t, date_taken=@d, camera=@c,
                             has_gps=@g, jpeg_quality=@q, meta_bytes=@b, sub_sec=@ss, meta_state=4
            WHERE file_key=@k
            """;
        foreach (string p in new[] { "@e", "@t", "@d", "@c", "@g", "@q", "@b", "@ss", "@k" })
            update.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(p, DBNull.Value));

        foreach (var (key, meta) in results)
        {
            update.Parameters["@e"].Value = meta.HasExif ? 1 : 0;
            update.Parameters["@t"].Value = meta.TagCount;
            update.Parameters["@d"].Value = meta.DateTaken is { } d
                ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : DBNull.Value;
            update.Parameters["@c"].Value = (object?)meta.Camera ?? DBNull.Value;
            update.Parameters["@g"].Value = meta.HasGps ? 1 : 0;
            update.Parameters["@q"].Value = (object?)meta.JpegQuality ?? DBNull.Value;
            update.Parameters["@b"].Value = meta.MetadataBytes;
            update.Parameters["@ss"].Value = (object?)meta.SubSecond ?? DBNull.Value;
            update.Parameters["@k"].Value = key;
            update.ExecuteNonQuery();
        }
        tx.Commit();

        // Re-read and substitute inside the group lists themselves — replacing entries in a
        // flattened copy would score the pre-metadata values and silently ignore this pass.
        var refreshed = LoadCandidatesByKey(candidates.Select(c => c.FileKey));
        foreach (var group in groups)
            for (int i = 0; i < group.Count; i++)
                if (refreshed.TryGetValue(group[i].FileKey, out var updated))
                    group[i] = updated;
    }
}
