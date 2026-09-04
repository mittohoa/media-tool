namespace MediaTool.Core.Volumes;

public sealed record ScanTarget(VolumeInfo Volume, string Path, bool Recommended, string Note);

/// <summary>
/// Picks which attached volumes a "scan everything" run should actually touch.
///
/// "Everything" cannot mean literally every mounted volume: a cloud provider's virtual drive
/// looks like an ordinary local disk to Windows, and walking it streams the entire account
/// over the network. So the selection is made explicit, each decision is explained, and the
/// ones held back require the user to say so.
/// </summary>
public static class ScanTargetSelector
{
    /// <summary>
    /// Volume labels used by desktop sync clients that mount themselves as a drive. Matching
    /// on the label is crude, but Windows reports these as fixed local disks and offers
    /// nothing better to go on.
    /// </summary>
    private static readonly string[] CloudLabels =
        ["google drive", "onedrive", "dropbox", "icloud", "mega", "pcloud", "box", "sync", "nextcloud"];

    public static List<ScanTarget> Choose(IEnumerable<VolumeInfo> volumes)
    {
        var targets = new List<ScanTarget>();

        foreach (var volume in volumes)
        {
            if (volume.PrimaryMountPoint is not { } mount)
            {
                targets.Add(new ScanTarget(volume, "", false, "no drive letter or mount point"));
                continue;
            }

            if (volume.StorageKind == StorageKind.Remote)
            {
                targets.Add(new ScanTarget(volume, mount, false, "network drive"));
                continue;
            }

            string label = (volume.Label ?? "").ToLowerInvariant();
            if (CloudLabels.Any(c => label.Contains(c, StringComparison.Ordinal)))
            {
                targets.Add(new ScanTarget(volume, mount, false,
                    $"looks like a cloud sync drive (\"{volume.Label}\") — scanning it would stream the account over the network"));
                continue;
            }

            // A tiny volume is an EFI or recovery partition that happened to get a letter.
            if (volume.TotalBytes < 2L * 1024 * 1024 * 1024)
            {
                targets.Add(new ScanTarget(volume, mount, false, "smaller than 2 GB — system or recovery partition"));
                continue;
            }

            targets.Add(new ScanTarget(volume, mount, true, ""));
        }

        return targets;
    }
}
