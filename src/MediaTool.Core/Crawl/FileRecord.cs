namespace MediaTool.Core.Crawl;

/// <summary>
/// One catalogued file. Paths are stored relative to the volume mount point so the record
/// stays valid when the disk comes back on a different drive letter.
/// </summary>
public sealed class FileRecord
{
    /// <summary>Path relative to the volume root, backslash separated, e.g. <c>Photos\2019\a.jpg</c>.</summary>
    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    /// <summary>Lowercase, with dot. Null when the file has none.</summary>
    public string? Extension { get; init; }

    public long Size { get; init; }

    /// <summary>FILETIME ticks, UTC. Kept raw — converting to DateTime loses nothing but costs per file.</summary>
    public long LastWriteTimeUtc { get; init; }
    public long CreationTimeUtc { get; init; }

    public uint Attributes { get; init; }

    /// <summary>
    /// NTFS 128-bit file id, low half. Together with the volume this survives renames and
    /// moves within the volume, which is what makes an incremental rescan nearly free.
    /// </summary>
    public ulong FileIdLow { get; init; }
    public ulong FileIdHigh { get; init; }
    public bool HasFileId { get; init; }
}
