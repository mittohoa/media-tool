namespace MediaTool.Core.Volumes;

/// <summary>How the storage behind a volume responds to random access.</summary>
public enum StorageKind
{
    Unknown = 0,
    /// <summary>Spinning disk. Random reads are ~100x more expensive than sequential.</summary>
    Hdd = 1,
    /// <summary>SSD / NVMe. Deep parallel queues are a win.</summary>
    Ssd = 2,
    /// <summary>Network share or virtual disk — latency-bound, treat like an HDD.</summary>
    Remote = 3,
}

/// <summary>
/// A volume identified the only way that survives replugging: by its volume GUID path.
///
/// Drive letters are assignments, not identity. Unplug two external disks and plug them
/// back in the other order and every path in the catalog would point at the wrong disk.
/// The GUID path (\\?\Volume{...}\) is stamped on the volume itself and does not move.
/// </summary>
public sealed class VolumeInfo
{
    /// <summary>Canonical identity, e.g. <c>\\?\Volume{ab12...}\</c> (trailing backslash kept).</summary>
    public required string VolumeGuid { get; init; }

    /// <summary>NTFS serial. Secondary identity only — it is not unique across cloned disks.</summary>
    public uint SerialNumber { get; init; }

    public string? Label { get; init; }

    /// <summary>NTFS, ReFS, exFAT, FAT32...</summary>
    public string? FileSystem { get; init; }

    public ulong TotalBytes { get; init; }
    public ulong FreeBytes { get; init; }

    /// <summary>Every path this volume is currently reachable through (letters and mounted folders).</summary>
    public IReadOnlyList<string> MountPoints { get; init; } = [];

    public StorageKind StorageKind { get; init; }

    /// <summary>False for FAT32/exFAT, where file identity has to fall back to path.</summary>
    public bool SupportsFileId { get; init; }

    public bool IsOnline => MountPoints.Count > 0;

    /// <summary>Preferred path to reach the volume: a drive letter if it has one.</summary>
    public string? PrimaryMountPoint =>
        MountPoints.FirstOrDefault(m => m.Length == 3 && m[1] == ':') ?? MountPoints.FirstOrDefault();

    public override string ToString()
    {
        string mount = PrimaryMountPoint ?? "(no mount point)";
        string label = string.IsNullOrEmpty(Label) ? "" : $" \"{Label}\"";
        return $"{mount}{label} [{FileSystem}] {StorageKind} {TotalBytes / 1_000_000_000.0:F1} GB";
    }
}
