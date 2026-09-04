using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaTool.Core.Metadata;

/// <summary>Which application a file's name says it came from.</summary>
public enum MediaSource
{
    Unknown = 0,
    /// <summary>A camera or phone camera app: the original capture.</summary>
    Camera,
    /// <summary>A screen capture, not a photograph.</summary>
    Screenshot,
    /// <summary>Received through a messaging app, which means an already re-compressed copy.</summary>
    Messaging,
    /// <summary>Saved out of a browser or a download folder; provenance unknown.</summary>
    Download,
}

public readonly record struct FilenameFacts(DateTime? Taken, string? Pattern, MediaSource Source);

/// <summary>
/// Recovers what a filename says about a photo.
///
/// Messaging apps strip EXIF on purpose — it carries the sender's location and equipment —
/// and then write the timestamp into the filename instead. So the files most likely to have
/// no embedded metadata are often the ones whose names are most informative. In a library
/// that has passed through Zalo, Messenger or WhatsApp, this recovers dates for photos that
/// otherwise look like they have no history at all.
///
/// A filename is the weakest evidence available: anyone can rename a file, and a messaging
/// app's timestamp is when the message was sent rather than when the shutter fired. It is
/// therefore kept in its own column and deliberately NOT used to decide that two photos are
/// separate exposures — see the note on <see cref="Taken"/>.
/// </summary>
public static class FilenameDate
{
    private sealed record Pattern(string Name, Regex Regex, string? Format, MediaSource Source);

    private static readonly Pattern[] Patterns =
    [
        // Android camera and Google Photos: IMG_20210501_172347, VID_20210501_172347
        new("android-camera", new(@"(?:IMG|VID|PANO|MVIMG)[_-](\d{8})[_-](\d{6})", RegexOptions.IgnoreCase),
            "yyyyMMddHHmmss", MediaSource.Camera),

        // Google Pixel: PXL_20210501_172347123
        new("pixel", new(@"PXL[_-](\d{8})[_-](\d{6})\d*", RegexOptions.IgnoreCase),
            "yyyyMMddHHmmss", MediaSource.Camera),

        // Samsung and many others: 20210501_172347
        new("date-time", new(@"(?<!\d)(\d{8})[_-](\d{6})(?!\d)"),
            "yyyyMMddHHmmss", MediaSource.Camera),

        // WhatsApp: IMG-20210501-WA0012 — date only, no time
        new("whatsapp", new(@"(?:IMG|VID)-(\d{8})-WA\d+", RegexOptions.IgnoreCase),
            "yyyyMMdd", MediaSource.Messaging),

        // Viber: viber_image_2021-05-01_17-23-47
        new("viber", new(@"viber_(?:image|video)_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})", RegexOptions.IgnoreCase),
            "yyyy-MM-ddHH-mm-ss", MediaSource.Messaging),

        // Telegram: photo_2021-05-01_17-23-47
        new("telegram", new(@"(?:photo|video)_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})", RegexOptions.IgnoreCase),
            "yyyy-MM-ddHH-mm-ss", MediaSource.Messaging),

        // Signal: signal-2021-05-01-172347
        new("signal", new(@"signal-(\d{4}-\d{2}-\d{2})-(\d{6})", RegexOptions.IgnoreCase),
            "yyyy-MM-ddHHmmss", MediaSource.Messaging),

        // Screenshots: Screenshot_20210501-172347, Screenshot 2021-05-01 172347
        new("screenshot", new(@"Screen(?:shot|_?Shot)[ _-](\d{4}-?\d{2}-?\d{2})[ _-](\d{2}-?\d{2}-?\d{2})",
            RegexOptions.IgnoreCase), null, MediaSource.Screenshot),

        // Facebook Messenger: FB_IMG_1619875427123 — unix milliseconds, handled separately
        new("messenger", new(@"FB_IMG_(\d{13})", RegexOptions.IgnoreCase), null, MediaSource.Messaging),
    ];

    // A catalogued path is relative to the volume root, so the folder being looked for can
    // be the very first segment, with no separator in front of it.
    private const string FolderStart = @"(?:^|[\\/])";

    private static readonly (Regex Regex, MediaSource Source)[] SourceOnlyHints =
    [
        (new(FolderStart + @"(?:Zalo|ZaloPC)[\\/]", RegexOptions.IgnoreCase), MediaSource.Messaging),
        (new(FolderStart + @"(?:Messenger|WhatsApp|Viber|Telegram|Signal|Line|WeChat)[\\/]",
            RegexOptions.IgnoreCase), MediaSource.Messaging),
        (new(FolderStart + @"Screenshots?[\\/]", RegexOptions.IgnoreCase), MediaSource.Screenshot),
        (new(FolderStart + @"(?:Downloads?|Tai xuong)[\\/]", RegexOptions.IgnoreCase), MediaSource.Download),
        (new(FolderStart + @"DCIM[\\/]", RegexOptions.IgnoreCase), MediaSource.Camera),
    ];

    /// <summary>Dates outside this range are a coincidence in the name, not a capture time.</summary>
    private static readonly DateTime Earliest = new(1990, 1, 1);

    public static FilenameFacts Read(string relativePathOrName)
    {
        string name = Path.GetFileNameWithoutExtension(relativePathOrName);
        MediaSource source = SourceFromPath(relativePathOrName);

        foreach (var pattern in Patterns)
        {
            var match = pattern.Regex.Match(name);
            if (!match.Success) continue;

            DateTime? taken = pattern.Name == "messenger"
                ? FromUnixMilliseconds(match.Groups[1].Value)
                : FromParts(match, pattern.Format);

            if (taken is null) continue;

            return new FilenameFacts(taken, pattern.Name,
                source == MediaSource.Unknown ? pattern.Source : source);
        }

        return new FilenameFacts(null, null, source);
    }

    private static MediaSource SourceFromPath(string path)
    {
        foreach (var (regex, source) in SourceOnlyHints)
            if (regex.IsMatch(path)) return source;

        return MediaSource.Unknown;
    }

    private static DateTime? FromParts(Match match, string? format)
    {
        string joined = string.Concat(match.Groups.Cast<Group>().Skip(1).Select(g => g.Value));

        if (format is not null)
            return DateTime.TryParseExact(joined, format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact) && Plausible(exact) ? exact : null;

        // Screenshots use several separator styles; strip them and read the digits.
        string digits = new(joined.Where(char.IsDigit).ToArray());
        if (digits.Length != 14) return null;

        return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) && Plausible(parsed) ? parsed : null;
    }

    private static DateTime? FromUnixMilliseconds(string text)
    {
        if (!long.TryParse(text, out long milliseconds)) return null;

        var value = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
        return Plausible(value) ? value : null;
    }

    private static bool Plausible(DateTime value) =>
        value >= Earliest && value <= DateTime.UtcNow.AddDays(1);
}
