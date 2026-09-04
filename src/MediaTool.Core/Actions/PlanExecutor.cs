using MediaTool.Core.Hashing;
using MediaTool.Core.Imaging;
using MediaTool.Core.Storage;
using MediaTool.Core.Util;
using Microsoft.Data.Sqlite;

namespace MediaTool.Core.Actions;

public sealed class ExecutionResult
{
    public string BatchId = "";
    public int Moved;
    public int VerificationFailed;
    public int Errors;
    public long BytesFreed;
    public List<string> Problems = [];
}

/// <summary>
/// Carries out a plan, and can put it back.
///
/// Two rules govern everything here. Files are moved into quarantine, never deleted — the
/// tool's job is to propose, the user's is to empty the quarantine once they are satisfied.
/// And nothing moves until the copy being kept has been checked against it *right now*, on
/// disk, by the strictest test the group's relation allows. A hash recorded during an
/// earlier scan is a claim about the past; it is not evidence that the two files are still
/// the same.
/// </summary>
public sealed class PlanExecutor
{
    private readonly CatalogDatabase _db;

    public PlanExecutor(CatalogDatabase db) => _db = db;

    public ExecutionResult Execute(
        IReadOnlyList<PlanRow> rows,
        string quarantineRoot,
        bool dryRun,
        Action<string>? log,
        CancellationToken ct)
    {
        var result = new ExecutionResult { BatchId = NewBatchId() };

        var keepers = rows.Where(r => r.Action == PlannedAction.Keep)
                          .ToDictionary(r => r.File.FileKey, r => r.File);

        var toMove = rows.Where(r => r.Action == PlannedAction.Quarantine).ToList();
        if (toMove.Count == 0) return result;

        Directory.CreateDirectory(quarantineRoot);
        string batchDir = Path.Combine(quarantineRoot, result.BatchId);

        foreach (var row in toMove)
        {
            ct.ThrowIfCancellationRequested();

            if (row.KeptFileKey is not { } keptKey || !keepers.TryGetValue(keptKey, out var keeper))
            {
                result.Errors++;
                result.Problems.Add($"no keeper recorded for {row.File.FullPath}");
                continue;
            }

            string source = row.File.FullPath;
            string keeperPath = keeper.FullPath;

            if (!File.Exists(source))
            {
                result.Errors++;
                result.Problems.Add($"gone since the scan: {source}");
                continue;
            }
            if (!File.Exists(keeperPath))
            {
                // Never move a file because of a copy that is not there any more.
                result.Errors++;
                result.Problems.Add($"keeper missing, refusing to touch {source}: {keeperPath}");
                continue;
            }

            var (verified, why) = Verify(row.Kind, keeperPath, source, row.File.PixelHash);
            if (!verified)
            {
                result.VerificationFailed++;
                result.Problems.Add($"verification failed ({why}): {source}");
                continue;
            }

            // Quarantine mirrors the original layout, volume label included, so the path a
            // file came from stays readable and an undo is unambiguous.
            string relative = Path.Combine(SanitiseVolume(row.File.VolumeName), row.File.RelativePath);
            string destination = Path.Combine(batchDir, relative);

            if (dryRun)
            {
                result.Moved++;
                result.BytesFreed += row.File.Size;
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                destination = MakeUnique(destination);

                // Recorded before the move: if the process dies mid-operation the catalog
                // still shows exactly which file was in flight and where it was headed.
                long actionId = RecordAction(result.BatchId, row, destination, "planned");

                File.Move(LongPath.Prefix(source), LongPath.Prefix(destination));

                MarkAction(actionId, "done", null);
                MarkFileMissing(row.File.FileKey);

                result.Moved++;
                result.BytesFreed += row.File.Size;

                if (result.Moved % 500 == 0) log?.Invoke($"  moved {result.Moved:N0}");
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} moving {source}: {ex.Message}");
            }
        }

        if (!dryRun) WriteManifest(batchDir, result.BatchId);
        return result;
    }

    /// <summary>
    /// Confirms the two files really are what the plan says, using the strongest check the
    /// relation supports. A near-duplicate has no exact check by definition, which is why
    /// that kind is refused here rather than approximated.
    /// </summary>
    private static (bool Ok, string Why) Verify(
        GroupKind kind, string keeperPath, string victimPath, string? expectedVictimPixelHash)
    {
        switch (kind)
        {
            case GroupKind.ExactBytes:
                return ContentHasher.ContentsEqual(keeperPath, victimPath)
                    ? (true, "bytes match")
                    : (false, "bytes differ");

            case GroupKind.IdenticalPicture:
                // These files differ in bytes on purpose - that is the whole point of the
                // tier - so the picture itself is what has to be re-checked.
                try
                {
                    var a = PerceptualHash.Compute(ImageDecoder.Decode(keeperPath));
                    var b = PerceptualHash.Compute(ImageDecoder.Decode(victimPath));
                    return a.PixelHash.AsSpan().SequenceEqual(b.PixelHash)
                        ? (true, "pixels match")
                        : (false, "pixels differ");
                }
                catch (Exception ex)
                {
                    return (false, $"could not re-decode: {ex.Message}");
                }

            case GroupKind.ReviewedByHuman:
                // The duplicate judgement was the reviewer's and is not re-litigated here.
                // What is checked is that neither file has changed since they saw it: both
                // must still decode, and the one being moved must still be the same picture
                // it was on screen. A file edited or replaced in the meantime is refused.
                try
                {
                    var keeper = PerceptualHash.Compute(ImageDecoder.Decode(keeperPath));
                    var victim = PerceptualHash.Compute(ImageDecoder.Decode(victimPath));

                    if (expectedVictimPixelHash is { Length: > 0 } &&
                        !string.Equals(Convert.ToHexString(victim.PixelHash), expectedVictimPixelHash,
                                       StringComparison.OrdinalIgnoreCase))
                        return (false, "the file changed since it was reviewed");

                    _ = keeper;
                    return (true, "unchanged since review");
                }
                catch (Exception ex)
                {
                    return (false, $"could not re-decode: {ex.Message}");
                }

            default:
                return (false, "unknown group kind");
        }
    }

    /// <summary>
    /// Restores a batch using only the manifest left inside the quarantine folder, with no
    /// catalog involved.
    ///
    /// The catalog is a convenience; the manifest is the guarantee. If the database is lost,
    /// corrupted, or simply on another machine, the folder full of quarantined files still
    /// carries everything needed to put them back — which is the point of writing it in
    /// plain CSV next to them rather than only into SQLite.
    /// </summary>
    public ExecutionResult UndoFromManifest(string manifestPath, Action<string>? log, CancellationToken ct)
    {
        var result = new ExecutionResult { BatchId = Path.GetFileName(Path.GetDirectoryName(manifestPath) ?? "") };

        var mounts = Volumes.VolumeScanner.EnumerateVolumes()
            .Where(v => v.PrimaryMountPoint is not null)
            .ToDictionary(v => v.VolumeGuid, v => v.PrimaryMountPoint!, StringComparer.OrdinalIgnoreCase);

        foreach (string line in File.ReadLines(manifestPath).Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var f = SplitCsvLine(line);
            if (f.Count < 6) continue;
            if (!long.TryParse(f[2], out long size)) continue;

            string volumeGuid = f[3], relPath = f[4], destination = f[5];

            if (!mounts.TryGetValue(volumeGuid, out string? mount))
            {
                result.Errors++;
                result.Problems.Add($"volume not attached, cannot restore: {relPath}");
                continue;
            }

            string target = Path.Combine(mount, relPath);

            if (!File.Exists(destination))
            {
                result.Errors++;
                result.Problems.Add($"quarantined file is gone (purged?): {destination}");
                continue;
            }
            if (File.Exists(target))
            {
                result.Errors++;
                result.Problems.Add($"original path is occupied, skipped: {target}");
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(LongPath.Prefix(destination), LongPath.Prefix(target));
                result.Moved++;
                result.BytesFreed += size;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} restoring {target}: {ex.Message}");
            }
        }

        log?.Invoke($"restored {result.Moved:N0} files from the manifest");
        return result;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>Puts a batch back where it came from. Refuses to overwrite anything.</summary>
    public ExecutionResult Undo(string batchId, Action<string>? log, CancellationToken ct)
    {
        var result = new ExecutionResult { BatchId = batchId };

        var actions = new List<(long Id, string Destination, string VolumeGuid, string Rel, long Size, long FileKey)>();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT action_id, destination, origin_volume, origin_rel, size, file_key
                FROM actions WHERE batch_id=@b AND state='done'
                """;
            cmd.Parameters.AddWithValue("@b", batchId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                actions.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                             reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5)));
        }

        if (actions.Count == 0)
        {
            result.Problems.Add($"no completed actions found for batch {batchId}");
            return result;
        }

        var mounts = Volumes.VolumeScanner.EnumerateVolumes()
            .Where(v => v.PrimaryMountPoint is not null)
            .ToDictionary(v => v.VolumeGuid, v => v.PrimaryMountPoint!, StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();

            if (!mounts.TryGetValue(action.VolumeGuid, out string? mount))
            {
                result.Errors++;
                result.Problems.Add($"volume not attached, cannot restore: {action.Rel}");
                continue;
            }

            string target = Path.Combine(mount, action.Rel);

            if (!File.Exists(action.Destination))
            {
                result.Errors++;
                result.Problems.Add($"quarantined file is gone: {action.Destination}");
                continue;
            }
            if (File.Exists(target))
            {
                // Something already occupies the original path. Overwriting it would be the
                // one genuinely destructive thing an undo could do.
                result.Errors++;
                result.Problems.Add($"original path is occupied, skipped: {target}");
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(LongPath.Prefix(action.Destination), LongPath.Prefix(target));
                MarkAction(action.Id, "undone", null);
                MarkFilePresent(action.FileKey);
                result.Moved++;
                result.BytesFreed += action.Size;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} restoring {target}: {ex.Message}");
            }
        }

        log?.Invoke($"restored {result.Moved:N0} files");
        return result;
    }

    private long RecordAction(string batchId, PlanRow row, string destination, string state)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO actions (batch_id, file_key, kind, origin_volume, origin_rel, destination,
                                 size, content_hash, kept_file_key, state, acted_utc)
            VALUES (@b, @k, 'quarantine', @vol, @rel, @dst, @size, @hash, @kept, @state, @utc)
            RETURNING action_id
            """;
        cmd.Parameters.AddWithValue("@b", batchId);
        cmd.Parameters.AddWithValue("@k", row.File.FileKey);
        cmd.Parameters.AddWithValue("@vol", row.File.VolumeGuid);
        cmd.Parameters.AddWithValue("@rel", row.File.RelativePath);
        cmd.Parameters.AddWithValue("@dst", destination);
        cmd.Parameters.AddWithValue("@size", row.File.Size);
        cmd.Parameters.AddWithValue("@hash", (object?)row.File.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kept", (object?)row.KeptFileKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@state", state);
        cmd.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("O"));
        return (long)cmd.ExecuteScalar()!;
    }

    private void MarkAction(long actionId, string state, string? message)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE actions SET state=@s, message=@m WHERE action_id=@id";
        cmd.Parameters.AddWithValue("@s", state);
        cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", actionId);
        cmd.ExecuteNonQuery();
    }

    private void MarkFileMissing(long fileKey)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET present=0 WHERE file_key=@k";
        cmd.Parameters.AddWithValue("@k", fileKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Puts a file back into the catalog's view of the library.
    ///
    /// Quarantining marks a file missing, and undo used to move it back without saying so.
    /// The result was a catalog quietly disagreeing with the disk: the file was there, but
    /// every later command behaved as though it were not, so an undone batch left the
    /// library looking smaller than it was and its duplicates unfindable.
    /// </summary>
    private void MarkFilePresent(long fileKey)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET present=1 WHERE file_key=@k";
        cmd.Parameters.AddWithValue("@k", fileKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A plain-text copy of the batch, written into the quarantine folder itself, so the
    /// move can be reversed by hand even if the catalog is lost.
    /// </summary>
    private void WriteManifest(string batchDir, string batchId)
    {
        Directory.CreateDirectory(batchDir);
        string path = Path.Combine(batchDir, "manifest.csv");

        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
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

    private static string NewBatchId() => DateTime.Now.ToString("yyyyMMdd-HHmmss");
}
