using System.Text.RegularExpressions;

namespace MediaTool.Core.Dedupe;

/// <summary>One candidate inside a duplicate group, with everything the policy scores on.</summary>
public sealed class KeeperCandidate
{
    public required long FileKey { get; init; }
    public required string VolumeGuid { get; init; }
    public required string VolumeName { get; init; }
    public required string RelativePath { get; init; }
    public required long Size { get; init; }
    public required long MTime { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public byte[]? ContentHash { get; init; }
    public string? PixelHash { get; init; }

    public bool HasExif { get; init; }
    public int ExifTags { get; init; }
    public DateTime? DateTaken { get; init; }
    public bool HasGps { get; init; }
    public string? Camera { get; init; }
    public int? JpegQuality { get; init; }
    public int MetadataBytes { get; init; }

    public string Name => Path.GetFileName(RelativePath);

    /// <summary>
    /// How much of the original the file still holds. A camera RAW is the negative; a JPEG
    /// rendered from it is a print. Both may be worth keeping, but they are not
    /// interchangeable, and discarding the negative to keep the print is the one mistake
    /// here that cannot be undone.
    /// </summary>
    public FormatTier Tier => FormatTiers.Of(RelativePath);
    public long Pixels => (long)Width * Height;
    public string FullPath => VolumeName.EndsWith('\\') ? VolumeName + RelativePath : VolumeName + '\\' + RelativePath;
}

public enum FormatTier
{
    Unknown = 0,
    /// <summary>JPEG, HEIC, WebP — lossy renderings.</summary>
    Lossy = 1,
    /// <summary>PNG, TIFF, BMP — every pixel preserved, but already demosaiced.</summary>
    Lossless = 2,
    /// <summary>Camera RAW. The only tier that keeps the sensor data.</summary>
    Raw = 3,
}

public static class FormatTiers
{
    private static readonly HashSet<string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".dng", ".orf", ".rw2",
        ".raf", ".pef", ".srw", ".raw", ".rwl", ".3fr", ".iiq", ".x3f",
    };

    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".tif", ".tiff", ".bmp",
    };

    private static readonly HashSet<string> Lossy = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif", ".avif", ".webp", ".gif",
    };

    public static FormatTier Of(string path)
    {
        string ext = Path.GetExtension(path);
        if (Raw.Contains(ext)) return FormatTier.Raw;
        if (Lossless.Contains(ext)) return FormatTier.Lossless;
        if (Lossy.Contains(ext)) return FormatTier.Lossy;
        return FormatTier.Unknown;
    }
}

public sealed class KeeperOptions
{
    /// <summary>
    /// Path fragments that mark a curated library. A copy living here outranks the same photo
    /// sitting in a download folder or a nested backup.
    /// </summary>
    public List<string> PreferredPathFragments { get; set; } = [];

    /// <summary>Path fragments that mark scratch space — backups of backups, temp dumps.</summary>
    public List<string> DemotedPathFragments { get; set; } =
        ["\\backup", "\\temp", "\\tmp", "\\new folder", "\\downloads", "\\recycle", "\\cache", " - copy"];
}

public sealed record ScoredCandidate(KeeperCandidate Candidate, int Score, List<string> Reasons);

/// <summary>
/// Decides which copy of a photo to keep.
///
/// The ordering is deliberate: resolution and metadata come first because they are what a
/// library actually loses when the wrong copy survives. File size is never used on its own —
/// a bigger file can simply be a worse re-encode of the same picture, and rewarding it would
/// systematically keep the degraded copy.
/// </summary>
public static class KeeperPolicy
{
    private static readonly Regex GenericName = new(
        @"^(img|dsc|dscn|photo|image|picture|untitled|unnamed|download|screenshot|received|"
        + @"fb_img|whatsapp|zalo|viber|scan|capture|_mg)[-_ ]?\d*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Only the markers a copy operation actually leaves behind. A bare numeric suffix is
    // NOT one of them: IMG_9999 is a camera counter, and treating it as a copy marker
    // penalises exactly the camera originals this policy is supposed to protect.
    private static readonly Regex CopySuffix = new(
        @"(\(\d+\)|[-_ ]cop(y|ia)|[-_ ]\d+\s*-\s*cop(y|ia))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Ranks candidates by a chain of comparisons rather than one summed score.
    ///
    /// A single score lets weak evidence outvote strong evidence once enough of it piles up:
    /// with additive scoring, a folder preference plus a tidy filename can outweigh the fact
    /// that one copy still has its capture date and the other does not. Ordering the
    /// criteria instead means a lower tier can only ever break a tie in the tier above it.
    /// </summary>
    public static List<ScoredCandidate> Rank(IEnumerable<KeeperCandidate> candidates, KeeperOptions options)
    {
        var scored = candidates.Select(c => Score(c, options)).ToList();
        scored.Sort(Compare);
        return scored;
    }

    private static int Compare(ScoredCandidate x, ScoredCandidate y)
    {
        var a = x.Candidate;
        var b = y.Candidate;

        // 1. Resolution, when it differs materially. Discarded pixels cannot be recovered by
        //    any later step, so nothing below is allowed to trade them away. The 20% band
        //    keeps trivial differences from pre-empting the metadata comparison.
        if (a.Pixels > 0 && b.Pixels > 0)
        {
            double ratio = (double)a.Pixels / b.Pixels;
            if (ratio > 1.2) return -1;
            if (ratio < 1 / 1.2) return 1;
        }
        else if (a.Pixels != b.Pixels) return a.Pixels > b.Pixels ? -1 : 1;

        // 2. Capture date. This is the single fact a stripped copy has lost and no other
        //    copy can supply, so it outranks every preference below.
        int date = (b.DateTaken is not null ? 1 : 0) - (a.DateTaken is not null ? 1 : 0);
        if (date != 0) return date;

        // 3. Never trade a camera original for a rendering of it. This sits above EXIF
        //    richness because losing a RAW is irreversible while losing tags is not.
        if (a.Tier != b.Tier) return b.Tier.CompareTo(a.Tier);

        // 4. How much EXIF survived, when the difference is real rather than incidental.
        if (a.ExifTags != b.ExifTags)
        {
            int high = Math.Max(a.ExifTags, b.ExifTags);
            if (high > 0 && Math.Abs(a.ExifTags - b.ExifTags) * 4 > high)
                return b.ExifTags.CompareTo(a.ExifTags);
        }

        // 5. Compression. A re-saved copy quantises harder than the file it came from.
        if (a.JpegQuality is { } qa && b.JpegQuality is { } qb && Math.Abs(qa - qb) > 5)
            return qb.CompareTo(qa);

        // 6. Everything the user can express about where a file should live, and what a
        //    sensible name looks like. Only reached when the copies are equal on substance.
        if (x.Score != y.Score) return y.Score.CompareTo(x.Score);

        // 7. Path, purely so the same catalog always produces the same plan. A tool whose
        //    decisions move between runs cannot be reviewed before it is applied.
        return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tiebreaker score: only location and naming, the things that are pure preference.
    /// Evidence about the picture and its metadata is handled by <see cref="Compare"/> and
    /// deliberately kept out of here, so no amount of preference can outweigh it.
    /// </summary>
    private static ScoredCandidate Score(KeeperCandidate c, KeeperOptions options)
    {
        int score = 0;
        var reasons = new List<string>();

        // Recorded as reasons rather than points: these decide the ranking one tier up, and
        // the plan has to say so where a human will read it.
        if (c.DateTaken is { } taken) reasons.Add($"capture date {taken:yyyy-MM-dd}");
        else if (c.HasExif) reasons.Add($"EXIF but no capture date");
        else reasons.Add("NO metadata");

        if (c.HasGps) reasons.Add("has GPS");
        if (c.JpegQuality is { } quality)
        {
            if (quality >= 90) reasons.Add($"quality ~{quality}");
            else if (quality <= 70) reasons.Add($"re-compressed ~{quality}");
        }

        // --- where it lives ---
        string lowerPath = "\\" + c.RelativePath.ToLowerInvariant();

        foreach (string fragment in options.PreferredPathFragments)
            if (lowerPath.Contains(fragment.ToLowerInvariant(), StringComparison.Ordinal))
            {
                score += 100;
                reasons.Add("in a preferred folder");
                break;
            }

        foreach (string fragment in options.DemotedPathFragments)
            if (lowerPath.Contains(fragment, StringComparison.Ordinal))
            {
                score -= 60;
                reasons.Add("in a scratch/backup folder");
                break;
            }

        // Depth: a photo buried ten levels down inside nested backups is rarely the original.
        int depth = c.RelativePath.Count(ch => ch == '\\');
        score -= Math.Min(depth, 12) * 2;

        // --- the name ---
        string stem = Path.GetFileNameWithoutExtension(c.Name);
        if (!GenericName.IsMatch(stem))
        {
            score += 30;
            reasons.Add("descriptive filename");
        }
        if (CopySuffix.IsMatch(stem))
        {
            score -= 40;
            reasons.Add("looks like a copy");
        }

        // Age, weakest signal of all: copying rewrites filesystem timestamps, so an older
        // mtime is a hint about this file, not evidence about the photo.
        score += (int)Math.Clamp((long)(4_000_000_000 - c.MTime / 10_000_000) / 50_000_000, -5, 5);

        return new ScoredCandidate(c, score, reasons);
    }
}
