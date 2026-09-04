using System.Threading.Channels;
using MediaTool.Core.Native;
using MediaTool.Core.Util;

namespace MediaTool.Core.Crawl;

/// <summary>
/// A single item in the crawl stream: either a catalogued file, or the announcement that a
/// directory is finished and these are the subdirectories still owed.
///
/// Both travel the same channel so the writer sees them in order, which is what lets the
/// frontier checkpoint be transactionally consistent with the file rows it accompanies.
/// </summary>
public readonly struct CrawlEvent
{
    public FileRecord? File { get; init; }
    public string? CompletedDirectory { get; init; }
    public IReadOnlyList<string>? DiscoveredDirectories { get; init; }

    public bool IsDirectoryCompletion => CompletedDirectory is not null;

    public static CrawlEvent ForFile(FileRecord record) => new() { File = record };

    public static CrawlEvent ForDirectory(string relDir, IReadOnlyList<string> children) =>
        new() { CompletedDirectory = relDir, DiscoveredDirectories = children };
}

/// <summary>
/// Walks one scan root and emits accepted files into a bounded channel.
///
/// Iterative, not recursive: an explicit frontier means the traversal state is a plain list
/// of directories, which can be persisted and restored. A scan of several TB across external
/// disks will be interrupted — a disk sleeps, a cable moves, the machine reboots — so resume
/// is a requirement, not a nicety.
/// </summary>
public sealed class DirectoryCrawler
{
    private readonly CrawlOptions _options;
    private readonly CrawlStats _stats = new();

    public CrawlStats Stats => _stats;

    /// <summary>Called on entering each directory. Used for progress display only.</summary>
    public Action<string>? OnDirectory { get; set; }

    public DirectoryCrawler(CrawlOptions options) => _options = options;

    /// <summary>
    /// Enumerates the given frontier and writes accepted files to <paramref name="output"/>.
    /// </summary>
    /// <param name="mountPoint">Volume root, with trailing backslash. Relative paths are computed against it.</param>
    /// <param name="frontier">
    /// Directories still to visit, relative to the mount point. On a fresh scan this is the
    /// scan root; on a resumed scan it is whatever the previous run had not finished.
    /// </param>
    public async Task CrawlAsync(
        string mountPoint,
        IEnumerable<string> frontier,
        ChannelWriter<CrawlEvent> output,
        CancellationToken ct)
    {
        string mountRoot = mountPoint.EndsWith('\\') ? mountPoint : mountPoint + '\\';

        var pending = new Stack<string>(frontier);
        var batch = new List<RawDirEntry>(1024);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            string relDir = pending.Pop();
            string absDir = relDir.Length == 0 ? mountRoot : mountRoot + relDir;

            var children = new List<string>();

            using (var reader = DirectoryReader.Open(LongPath.Prefix(absDir), out int openError))
            {
                if (reader is null)
                {
                    // Access denied on a system folder, or a directory removed since it was
                    // queued. Retire it from the frontier so a resume does not retry forever.
                    if (openError == Win32.ERROR_ACCESS_DENIED) _stats.AccessDenied++;
                    else _stats.Errors++;
                    await output.WriteAsync(CrawlEvent.ForDirectory(relDir, []), ct).ConfigureAwait(false);
                    continue;
                }

                _stats.DirectoriesVisited++;
                OnDirectory?.Invoke(absDir);

                try
                {
                    while (reader.ReadBatch(batch))
                    {
                        for (int i = 0; i < batch.Count; i++)
                        {
                            var entry = batch[i];

                            if (entry.IsDirectory)
                            {
                                string childRel = relDir.Length == 0 ? entry.Name : relDir + '\\' + entry.Name;
                                if (ShouldDescend(entry) && !_options.IsExcludedPath(childRel))
                                    children.Add(childRel);
                                continue;
                            }

                            _stats.FilesSeen++;
                            if (TryAccept(entry, relDir, out var record))
                            {
                                _stats.FilesAccepted++;
                                _stats.BytesAccepted += record!.Size;
                                await output.WriteAsync(CrawlEvent.ForFile(record), ct).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    // A disk yanked mid-scan, a corrupt directory entry: count it and move on.
                    // Aborting a multi-hour scan over one bad directory is the wrong trade.
                    _stats.Errors++;
                }
            }

            // Emitted last: the writer removes this directory from the frontier and adds its
            // children in the same transaction as the files above, so a crash anywhere leaves
            // the checkpoint consistent — never "parent done, children lost".
            await output.WriteAsync(CrawlEvent.ForDirectory(relDir, children), ct).ConfigureAwait(false);

            foreach (string child in children) pending.Push(child);
        }
    }

    private bool ShouldDescend(in RawDirEntry entry)
    {
        if (_options.ExcludedDirectoryNames.Contains(entry.Name)) return false;

        if (entry.IsReparsePoint && !_options.FollowReparsePoints)
        {
            _stats.ReparsePointsSkipped++;
            return false;
        }

        if (_options.SkipHidden && (entry.Attributes & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) return false;
        if (_options.SkipSystem && (entry.Attributes & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) return false;

        return true;
    }

    private bool TryAccept(in RawDirEntry entry, string relDir, out FileRecord? record)
    {
        record = null;

        if (!_options.MatchesExtension(entry.Name)) return false;
        if (entry.Size < _options.MinSizeBytes) return false;

        if (_options.SkipCloudPlaceholders && entry.IsCloudPlaceholder)
        {
            _stats.CloudPlaceholdersSkipped++;
            return false;
        }

        if (entry.IsReparsePoint && !_options.FollowReparsePoints)
        {
            // A hydrated cloud file keeps its reparse tag but its bytes are local, so it is a
            // real candidate. A symlink or junction is not: its target is catalogued through
            // its own path, and following it here would invent a duplicate of a real file.
            bool hydratedCloudFile = entry.ReparseTag != 0 && Win32.IsCloudReparseTag(entry.ReparseTag);
            if (!hydratedCloudFile)
            {
                _stats.ReparsePointsSkipped++;
                return false;
            }
        }

        if (_options.SkipHidden && (entry.Attributes & Win32.FILE_ATTRIBUTE_HIDDEN) != 0) return false;
        if (_options.SkipSystem && (entry.Attributes & Win32.FILE_ATTRIBUTE_SYSTEM) != 0) return false;

        int dot = entry.Name.LastIndexOf('.');
        string? ext = dot > 0 && dot < entry.Name.Length - 1 ? entry.Name[dot..].ToLowerInvariant() : null;

        record = new FileRecord
        {
            RelativePath = relDir.Length == 0 ? entry.Name : relDir + '\\' + entry.Name,
            Name = entry.Name,
            Extension = ext,
            Size = entry.Size,
            LastWriteTimeUtc = entry.LastWriteTime,
            CreationTimeUtc = entry.CreationTime,
            Attributes = entry.Attributes,
            FileIdLow = entry.FileIdLow,
            FileIdHigh = entry.FileIdHigh,
            HasFileId = entry.HasFileId,
        };
        return true;
    }
}
