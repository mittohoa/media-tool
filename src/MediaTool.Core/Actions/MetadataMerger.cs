using MediaTool.Core.Dedupe;
using MediaTool.Core.Imaging;
using MediaTool.Core.Metadata;
using MediaTool.Core.Storage;
using MediaTool.Core.Util;

namespace MediaTool.Core.Actions;

public sealed class MergeCandidate
{
    /// <summary>The copy being kept, which is missing a capture date.</summary>
    public required KeeperCandidate Keeper { get; init; }

    /// <summary>A copy about to be quarantined that still carries one.</summary>
    public required KeeperCandidate Donor { get; init; }

    public required DateTime DonorDate { get; init; }
    public required string? DonorCamera { get; init; }
}

public sealed class MergeResult
{
    public string BatchId = "";
    public int Merged;
    public int Skipped;
    public int Errors;
    public List<string> Problems = [];
}

/// <summary>
/// Puts a capture date back onto the copy being kept, taken from the copy about to be
/// removed.
///
/// This is the step that makes deduplication add something rather than only take away. A
/// library accumulates the same photo several times over, and often the copy worth keeping
/// on every other measure — the biggest, the least re-compressed — is the one an editor or a
/// messaging app stripped the metadata from. Removing the others without this would quietly
/// destroy the only remaining record of when the photo was taken.
///
/// It is also the only place in the program that writes into the user's library, so it is
/// built to the same rule as the deletion path: nothing is ever overwritten. The merged file
/// is written beside the original under a new name and verified before anything moves; the
/// original is then quarantined exactly like a duplicate, so 'undo' brings it back.
/// </summary>
public sealed class MetadataMerger
{
    private const string TempSuffix = ".mediatool-merge";

    private readonly CatalogDatabase _db;

    public MetadataMerger(CatalogDatabase db) => _db = db;

    /// <summary>
    /// Finds groups where the keeper has no capture date but something being quarantined
    /// does. Reads the files rather than trusting the catalog, since a plan may have been
    /// written days ago.
    /// </summary>
    public List<MergeCandidate> FindCandidates(IReadOnlyList<PlanRow> rows)
    {
        var candidates = new List<MergeCandidate>();

        foreach (var group in rows.GroupBy(r => r.Group))
        {
            var keeperRow = group.FirstOrDefault(r => r.Action == PlannedAction.Keep);
            if (keeperRow is null) continue;
            if (!ExifTransplant.IsSupported(keeperRow.File.RelativePath)) continue;
            if (!File.Exists(keeperRow.File.FullPath)) continue;

            var keeperMetadata = JpegMetadata.Read(keeperRow.File.FullPath);
            if (keeperMetadata.DateTaken is not null) continue;   // nothing to recover

            // Prefer the richest donor: the most tags, then the earliest date, so a
            // re-saved intermediate does not win over a camera original.
            MergeCandidate? best = null;
            int bestTags = 0;

            foreach (var loser in group.Where(r => r.Action == PlannedAction.Quarantine))
            {
                if (!File.Exists(loser.File.FullPath)) continue;

                var donorMetadata = JpegMetadata.Read(loser.File.FullPath);
                if (donorMetadata.DateTaken is not { } date) continue;
                if (donorMetadata.TagCount <= bestTags) continue;

                bestTags = donorMetadata.TagCount;
                best = new MergeCandidate
                {
                    Keeper = keeperRow.File,
                    Donor = loser.File,
                    DonorDate = date,
                    DonorCamera = donorMetadata.Camera,
                };
            }

            if (best is not null) candidates.Add(best);
        }

        return candidates;
    }

    public MergeResult Merge(
        IReadOnlyList<MergeCandidate> candidates,
        string quarantineRoot,
        bool dryRun,
        Action<string>? log,
        CancellationToken ct)
    {
        var result = new MergeResult { BatchId = "merge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
        if (candidates.Count == 0) return result;

        string batchDir = Path.Combine(quarantineRoot, result.BatchId);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            string keeperPath = candidate.Keeper.FullPath;
            string donorPath = candidate.Donor.FullPath;

            byte[]? exif = ExifTransplant.ExtractExifSegment(donorPath);
            if (exif is null)
            {
                result.Skipped++;
                continue;
            }

            if (dryRun)
            {
                result.Merged++;
                continue;
            }

            string tempPath = keeperPath + TempSuffix;

            try
            {
                if (File.Exists(tempPath))
                {
                    result.Errors++;
                    result.Problems.Add($"a previous merge left {tempPath} behind; not overwriting it");
                    continue;
                }

                // Written to a name nothing occupies, so this creates a file and never
                // replaces one.
                byte[] merged = ExifTransplant.Splice(keeperPath, exif);
                File.WriteAllBytes(LongPath.Prefix(tempPath), merged);

                var (ok, why) = VerifyMerged(keeperPath, tempPath, candidate.DonorDate);
                if (!ok)
                {
                    File.Delete(LongPath.Prefix(tempPath));
                    result.Errors++;
                    result.Problems.Add($"merge rejected ({why}): {keeperPath}");
                    continue;
                }

                // The original is quarantined, not discarded, and recorded first so an
                // interrupted merge leaves a trail pointing at both halves.
                string backup = Path.Combine(batchDir, SanitiseVolume(candidate.Keeper.VolumeName),
                                             candidate.Keeper.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                backup = MakeUnique(backup);

                long actionId = RecordAction(result.BatchId, candidate.Keeper, backup);

                File.Move(LongPath.Prefix(keeperPath), LongPath.Prefix(backup));
                File.Move(LongPath.Prefix(tempPath), LongPath.Prefix(keeperPath));

                MarkDone(actionId);
                result.Merged++;

                if (result.Merged % 200 == 0) log?.Invoke($"  merged {result.Merged:N0}");
            }
            catch (Exception ex)
            {
                TryRemoveTemp(tempPath);
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} merging into {keeperPath}: {ex.Message}");
            }
        }

        if (!dryRun && result.Merged > 0) WriteManifest(batchDir, result.BatchId);
        return result;
    }

    /// <summary>
    /// Confirms the merged file is the same picture with more metadata, and nothing else.
    ///
    /// Comparing the pixel hash is the point: a splice that damaged the compressed data, or
    /// that accidentally re-encoded, would change it. If the picture is bit-identical and the
    /// capture date is now present, the operation did exactly what it claimed.
    /// </summary>
    private static (bool Ok, string Why) VerifyMerged(string originalPath, string mergedPath, DateTime expectedDate)
    {
        try
        {
            var before = PerceptualHash.Compute(ImageDecoder.Decode(originalPath));
            var after = PerceptualHash.Compute(ImageDecoder.Decode(mergedPath));

            if (!before.PixelHash.AsSpan().SequenceEqual(after.PixelHash))
                return (false, "the picture changed");

            var metadata = JpegMetadata.Read(mergedPath);
            if (metadata.DateTaken != expectedDate)
                return (false, "the capture date did not transfer");

            return (true, "same picture, date recovered");
        }
        catch (Exception ex)
        {
            return (false, $"could not verify: {ex.Message}");
        }
    }

    private static void TryRemoveTemp(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(LongPath.Prefix(path));
        }
        catch { /* the caller is already reporting a failure */ }
    }

    private long RecordAction(string batchId, KeeperCandidate keeper, string backup)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO actions (batch_id, file_key, kind, origin_volume, origin_rel, destination,
                                 size, content_hash, kept_file_key, state, acted_utc)
            VALUES (@b, @k, 'merge-exif-backup', @vol, @rel, @dst, @size, NULL, NULL, 'planned', @utc)
            RETURNING action_id
            """;
        cmd.Parameters.AddWithValue("@b", batchId);
        cmd.Parameters.AddWithValue("@k", keeper.FileKey);
        cmd.Parameters.AddWithValue("@vol", keeper.VolumeGuid);
        cmd.Parameters.AddWithValue("@rel", keeper.RelativePath);
        cmd.Parameters.AddWithValue("@dst", backup);
        cmd.Parameters.AddWithValue("@size", keeper.Size);
        cmd.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    private void MarkDone(long actionId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE actions SET state='done' WHERE action_id=@id";
        cmd.Parameters.AddWithValue("@id", actionId);
        cmd.ExecuteNonQuery();
    }

    private void WriteManifest(string batchDir, string batchId)
    {
        Directory.CreateDirectory(batchDir);

        using var writer = new StreamWriter(Path.Combine(batchDir, "manifest.csv"), false,
                                            System.Text.Encoding.UTF8);
        writer.WriteLine("action_id,state,size,origin_volume_guid,origin_relative_path,quarantined_to");

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT action_id, state, size, origin_volume, origin_rel, destination
            FROM actions WHERE batch_id=@b ORDER BY action_id
            """;
        cmd.Parameters.AddWithValue("@b", batchId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            writer.WriteLine($"{reader.GetInt64(0)},{reader.GetString(1)},{reader.GetInt64(2)}," +
                             $"{Csv(reader.GetString(3))},{Csv(reader.GetString(4))},{Csv(reader.GetString(5))}");
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') ? '"' + value.Replace("\"", "\"\"") + '"' : value;

    private static string SanitiseVolume(string volumeName)
    {
        string cleaned = volumeName.Replace(":", "").Replace("\\", "").Trim();
        return cleaned.Length == 0 ? "volume" : cleaned;
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int n = 2; ; n++)
        {
            string candidate = Path.Combine(dir, $"{stem}__{n}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
