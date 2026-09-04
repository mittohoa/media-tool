namespace MediaTool.Core.Crawl;

public sealed class CrawlOptions
{
    /// <summary>
    /// Lowercase extensions including the dot. Empty means "every file".
    /// Defaults to the formats a photo library actually contains, RAW included.
    /// </summary>
    public HashSet<string> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // common web / camera output
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        // modern phone formats
        ".heic", ".heif", ".avif",
        // camera RAW
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2",
        ".raf", ".pef", ".srw", ".raw", ".rwl", ".3fr", ".iiq", ".x3f",
    };

    /// <summary>Files below this are thumbnails, icons and sprite junk, not library photos.</summary>
    public long MinSizeBytes { get; set; } = 16 * 1024;

    public bool SkipHidden { get; set; } = true;
    public bool SkipSystem { get; set; } = true;

    /// <summary>
    /// Keep this on. A OneDrive/Dropbox placeholder has no local bytes; reading it to hash
    /// triggers a download. On a multi-TB library that is the difference between a scan and
    /// an accidental full re-sync of the cloud account.
    /// </summary>
    public bool SkipCloudPlaceholders { get; set; } = true;

    /// <summary>
    /// Junctions and symlinks are skipped: they create cycles, and their targets are
    /// enumerated on their own anyway — following them double-counts every file.
    /// </summary>
    public bool FollowReparsePoints { get; set; } = false;

    /// <summary>Directory names skipped anywhere in the tree (case-insensitive).</summary>
    public HashSet<string> ExcludedDirectoryNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "$Extend", "Config.Msi",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "node_modules", ".git", ".svn", "AppData",
    };

    /// <summary>
    /// Path fragments that exclude a directory anywhere in the tree, matched as a substring
    /// of the path so far. Unlike ExcludedDirectoryNames this catches nested structures like
    /// a whole source-code tree, not just one folder name.
    /// </summary>
    public List<string> ExcludedPathFragments { get; set; } = [];

    public bool IsExcludedPath(string relativePath)
    {
        if (ExcludedPathFragments.Count == 0) return false;
        string lower = relativePath.ToLowerInvariant();
        foreach (string fragment in ExcludedPathFragments)
            if (lower.Contains(fragment.ToLowerInvariant(), StringComparison.Ordinal)) return true;
        return false;
    }

    public bool MatchesExtension(string name)
    {
        if (Extensions.Count == 0) return true;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return false;
        return Extensions.Contains(name[dot..]);
    }
}

/// <summary>Why a file or directory was not catalogued. Surfaced so a scan is auditable.</summary>
public enum SkipReason
{
    Extension,
    TooSmall,
    Hidden,
    System,
    CloudPlaceholder,
    ReparsePoint,
    ExcludedDirectory,
    AccessDenied,
    Error,
}

public sealed class CrawlStats
{
    public long DirectoriesVisited;
    public long FilesSeen;
    public long FilesAccepted;
    public long BytesAccepted;
    public long AccessDenied;
    public long CloudPlaceholdersSkipped;
    public long ReparsePointsSkipped;
    public long Errors;
}
