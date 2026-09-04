using System.Runtime.InteropServices;
using MediaTool.Core.Hashing;
using MediaTool.Core.Native;
using MediaTool.Core.Storage;
using MediaTool.Core.Util;

namespace MediaTool.Core.Actions;

public sealed class HardlinkResult
{
    public string BatchId = "";
    public int Linked;
    public int Skipped;
    public int VerificationFailed;
    public int Errors;
    public long BytesFreed;
    public List<string> Problems = [];
}

/// <summary>
/// Reclaims the space a duplicate occupies without removing it from the library.
///
/// A hardlink is a second name for one file. Where two paths hold byte-identical content on
/// the same volume, replacing one with a link to the other frees the disk space while both
/// paths keep opening, keep appearing in every folder listing, and keep working in every
/// application. Nothing looks deleted, because nothing is.
///
/// The catch is that it makes them genuinely one file. Editing either path in place changes
/// both — most photo software writes a new file rather than editing in place, but not all of
/// it does — and the duplicate's own timestamps are replaced by the keeper's. That is a real
/// trade, so this is offered as an alternative to quarantining, never as the default.
///
/// Creating a link at an occupied path requires clearing it first, which is the one moment
/// the file could be lost. So the same rule as everywhere else applies: the duplicate is
/// moved into quarantine and recorded before the link is made, and if anything fails it is
/// moved straight back.
/// </summary>
public sealed class HardlinkExecutor
{
    private readonly CatalogDatabase _db;

    public HardlinkExecutor(CatalogDatabase db) => _db = db;

    public HardlinkResult Execute(
        IReadOnlyList<PlanRow> rows,
        string quarantineRoot,
        bool dryRun,
        Action<string>? log,
        CancellationToken ct)
    {
        var result = new HardlinkResult { BatchId = "link-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };

        var keepers = rows.Where(r => r.Action == PlannedAction.Keep)
                          .ToDictionary(r => r.File.FileKey, r => r.File);

        var toLink = rows.Where(r => r.Action == PlannedAction.Quarantine).ToList();
        if (toLink.Count == 0) return result;

        string batchDir = Path.Combine(quarantineRoot, result.BatchId);

        foreach (var row in toLink)
        {
            ct.ThrowIfCancellationRequested();

            // Only byte-identical files may be collapsed into one. For anything looser the
            // two paths hold different pictures, and a link would silently discard one.
            if (row.Kind != GroupKind.ExactBytes)
            {
                result.Skipped++;
                continue;
            }

            if (row.KeptFileKey is not { } keptKey || !keepers.TryGetValue(keptKey, out var keeper))
            {
                result.Errors++;
                result.Problems.Add($"no keeper recorded for {row.File.FullPath}");
                continue;
            }

            string duplicate = row.File.FullPath;
            string original = keeper.FullPath;

            var (eligible, why) = CheckEligible(row.File, keeper, original, duplicate);
            if (!eligible)
            {
                result.Skipped++;
                result.Problems.Add($"skipped ({why}): {duplicate}");
                continue;
            }

            if (!ContentHasher.ContentsEqual(original, duplicate))
            {
                result.VerificationFailed++;
                result.Problems.Add($"verification failed (bytes differ): {duplicate}");
                continue;
            }

            if (dryRun)
            {
                result.Linked++;
                result.BytesFreed += row.File.Size;
                continue;
            }

            string backup = Path.Combine(batchDir, SanitiseVolume(row.File.VolumeName), row.File.RelativePath);
            long actionId = 0;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                backup = MakeUnique(backup);

                actionId = RecordAction(result.BatchId, row, backup);
                File.Move(LongPath.Prefix(duplicate), LongPath.Prefix(backup));

                if (!Win32.CreateHardLink(LongPath.Prefix(duplicate), LongPath.Prefix(original), IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    File.Move(LongPath.Prefix(backup), LongPath.Prefix(duplicate));   // put it straight back
                    MarkAction(actionId, "failed", $"CreateHardLink failed: 0x{error:X8}");
                    result.Errors++;
                    result.Problems.Add($"could not link ({new System.ComponentModel.Win32Exception(error).Message.Trim()}): {duplicate}");
                    continue;
                }

                // The proof the link worked: both paths must now name the same physical file.
                var originalId = FileIdentityReader.Read(original);
                var linkedId = FileIdentityReader.Read(duplicate);

                if (!originalId.IsKnown || originalId != linkedId)
                {
                    File.Delete(LongPath.Prefix(duplicate));                          // remove the bad link
                    File.Move(LongPath.Prefix(backup), LongPath.Prefix(duplicate));   // restore the original
                    MarkAction(actionId, "failed", "the link did not resolve to the kept file");
                    result.VerificationFailed++;
                    result.Problems.Add($"link did not resolve to the kept file, reverted: {duplicate}");
                    continue;
                }

                MarkAction(actionId, "done", null);
                result.Linked++;
                result.BytesFreed += row.File.Size;

                if (result.Linked % 500 == 0) log?.Invoke($"  linked {result.Linked:N0}");
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} linking {duplicate}: {ex.Message}");
                if (actionId != 0) MarkAction(actionId, "failed", ex.Message);
            }
        }

        if (!dryRun && result.Linked > 0) WriteManifest(batchDir, result.BatchId);
        return result;
    }

    /// <summary>
    /// Whether these two paths can be collapsed into one file at all. Every reason here is a
    /// hard limit of the filesystem rather than a judgement.
    /// </summary>
    private (bool Ok, string Why) CheckEligible(
        Dedupe.KeeperCandidate duplicate, Dedupe.KeeperCandidate keeper, string originalPath, string duplicatePath)
    {
        if (!string.Equals(duplicate.VolumeGuid, keeper.VolumeGuid, StringComparison.OrdinalIgnoreCase))
            return (false, "the two copies are on different volumes; a hardlink cannot cross one");

        if (!File.Exists(originalPath)) return (false, "the copy being kept is missing");
        if (!File.Exists(duplicatePath)) return (false, "gone since the scan");

        var originalId = FileIdentityReader.Read(originalPath);
        var duplicateId = FileIdentityReader.Read(duplicatePath);

        if (!originalId.IsKnown)
            return (false, "the filesystem does not report file ids, so a link cannot be verified");

        if (originalId == duplicateId)
            return (false, "already the same file");

        if (!SupportsHardlinks(duplicate.VolumeGuid))
            return (false, "the filesystem does not support hardlinks");

        return (true, "");
    }

    private bool SupportsHardlinks(string volumeGuid)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT file_system FROM volumes WHERE volume_guid=@g";
        cmd.Parameters.AddWithValue("@g", volumeGuid);

        string? fileSystem = cmd.ExecuteScalar() as string;
        return fileSystem is not null
            && (fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
             || fileSystem.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Undoes a hardlink batch: removes each link and moves the original back.
    ///
    /// Removing a link is safe precisely because it is a link — the bytes belong to the copy
    /// being kept, which is checked first. That check is the whole difference between undoing
    /// this and deleting a photo.
    /// </summary>
    public HardlinkResult Undo(string batchId, Action<string>? log, CancellationToken ct)
    {
        var result = new HardlinkResult { BatchId = batchId };

        var actions = new List<(long Id, string Destination, string VolumeGuid, string Rel, long Size)>();
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT action_id, destination, origin_volume, origin_rel, size
                FROM actions WHERE batch_id=@b AND state='done' AND kind='hardlink'
                """;
            cmd.Parameters.AddWithValue("@b", batchId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                actions.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                             reader.GetString(3), reader.GetInt64(4)));
        }

        if (actions.Count == 0)
        {
            result.Problems.Add($"no completed hardlink actions found for batch {batchId}");
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
                result.Problems.Add($"volume not attached: {action.Rel}");
                continue;
            }

            string linkPath = Path.Combine(mount, action.Rel);

            if (!File.Exists(action.Destination))
            {
                result.Errors++;
                result.Problems.Add($"the saved original is gone: {action.Destination}");
                continue;
            }

            try
            {
                if (File.Exists(linkPath))
                {
                    // Only remove what is provably a link: it must share its identity with
                    // the copy that was kept. Anything else is a real file and stays.
                    var backupId = FileIdentityReader.Read(action.Destination);
                    var occupantId = FileIdentityReader.Read(linkPath);

                    if (occupantId == backupId)
                    {
                        result.Skipped++;   // already restored
                        continue;
                    }

                    // Removing the occupant is safe exactly when it holds the same bytes as
                    // the file about to take its place — which our own link does by
                    // construction. Anything else is somebody's work and stays put.
                    if (!ContentHasher.ContentsEqual(action.Destination, linkPath))
                    {
                        result.Errors++;
                        result.Problems.Add($"a different file occupies {linkPath}; left alone");
                        continue;
                    }

                    ClearReadOnly(linkPath);
                    File.Delete(LongPath.Prefix(linkPath));
                }

                File.Move(LongPath.Prefix(action.Destination), LongPath.Prefix(linkPath));
                MarkAction(action.Id, "undone", null);
                result.Linked++;
                result.BytesFreed += action.Size;
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.Problems.Add($"{ex.GetType().Name} restoring {linkPath}: {ex.Message}");
            }
        }

        log?.Invoke($"restored {result.Linked:N0} files");
        return result;
    }

    /// <summary>
    /// Takes the read-only flag off a link so it can be replaced by the original.
    ///
    /// Undo used to fail outright on these. The flag belongs to the *name* being removed,
    /// not to the data - our own link is what carries it - and refusing to clear it meant a
    /// read-only file could be linked but never put back, which is a one-way door in a
    /// command whose whole purpose is reversal. Old archives are full of them: the case that
    /// found this was a folder of read-only bitmaps from a 1990s game.
    /// </summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            string prefixed = LongPath.Prefix(path);
            var attributes = File.GetAttributes(prefixed);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(prefixed, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception)
        {
            // Not being able to read the attributes is not itself a reason to stop; the
            // delete below will report the real problem if there is one.
        }
    }

    private long RecordAction(string batchId, PlanRow row, string backup)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO actions (batch_id, file_key, kind, origin_volume, origin_rel, destination,
                                 size, content_hash, kept_file_key, state, acted_utc)
            VALUES (@b, @k, 'hardlink', @vol, @rel, @dst, @size, @hash, @kept, 'planned', @utc)
            RETURNING action_id
            """;
        cmd.Parameters.AddWithValue("@b", batchId);
        cmd.Parameters.AddWithValue("@k", row.File.FileKey);
        cmd.Parameters.AddWithValue("@vol", row.File.VolumeGuid);
        cmd.Parameters.AddWithValue("@rel", row.File.RelativePath);
        cmd.Parameters.AddWithValue("@dst", backup);
        cmd.Parameters.AddWithValue("@size", row.File.Size);
        cmd.Parameters.AddWithValue("@hash", (object?)row.File.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@kept", (object?)row.KeptFileKey ?? DBNull.Value);
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

    private void WriteManifest(string batchDir, string batchId)
    {
        Directory.CreateDirectory(batchDir);

        using var writer = new StreamWriter(Path.Combine(batchDir, "manifest.csv"), false,
                                            System.Text.Encoding.UTF8);
        writer.WriteLine("action_id,state,size,origin_volume_guid,origin_relative_path,quarantined_to");

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT action_id, state, size, origin_volume, origin_rel, destination
            FROM actions WHERE batch_id=@b AND state='done' ORDER BY action_id
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
