using System.Globalization;
using System.Text.Json;
using MediaTool.Core.Util;

namespace MediaTool.Core.Metadata;

/// <summary>
/// Reads the metadata that lives beside a photo instead of inside it.
///
/// Google Photos exports are the reason this exists. A Takeout archive ships each image with
/// a JSON file carrying the capture time, location and description, because the exported
/// image itself frequently has none — the metadata was moved out, not copied out. To
/// anything that only reads EXIF, an entire Takeout library looks like photos with no
/// history at all, which is exactly the state this tool treats as "least worth keeping".
///
/// Lightroom and darktable do the same thing with XMP sidecars beside RAW files, for a
/// different reason: they will not modify the original, so edits and metadata go alongside.
/// </summary>
public static class SidecarMetadata
{
    /// <summary>Where a piece of metadata was found. Reported so a surprising result can be traced.</summary>
    public enum Source { None, GoogleTakeout, Xmp }

    public readonly record struct SidecarResult(
        Source Source, string? Path, DateTime? DateTaken, bool HasGps, string? Description);

    public static readonly SidecarResult NotFound = new(Source.None, null, null, false, null);

    /// <summary>
    /// Finds and reads a sidecar for <paramref name="imagePath"/>, if one exists.
    /// </summary>
    public static SidecarResult Read(string imagePath)
    {
        foreach (string candidate in CandidatePaths(imagePath))
        {
            if (!File.Exists(LongPath.Prefix(candidate))) continue;

            var result = candidate.EndsWith(".xmp", StringComparison.OrdinalIgnoreCase)
                ? ReadXmp(candidate)
                : ReadTakeoutJson(candidate);

            if (result.Source != Source.None) return result;
        }

        return NotFound;
    }

    /// <summary>
    /// The names a sidecar for this image could have.
    ///
    /// Takeout has used several conventions over the years and truncates long names to fit a
    /// filesystem limit, so this covers the common shapes rather than claiming to be
    /// exhaustive — a missed sidecar costs a photo its date, which is a real loss but not a
    /// wrong action.
    /// </summary>
    public static IEnumerable<string> CandidatePaths(string imagePath)
    {
        string withoutExtension = System.IO.Path.ChangeExtension(imagePath, null) ?? imagePath;

        yield return imagePath + ".json";                              // IMG_1234.jpg.json
        yield return imagePath + ".supplemental-metadata.json";        // newer Takeout
        yield return withoutExtension + ".json";                       // IMG_1234.json
        yield return imagePath + ".xmp";                               // IMG_1234.jpg.xmp
        yield return withoutExtension + ".xmp";                        // IMG_1234.xmp
    }

    /// <summary>True when this file is a sidecar rather than a photo in its own right.</summary>
    public static bool IsSidecar(string path)
    {
        string extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xmp", StringComparison.OrdinalIgnoreCase);
    }

    private static SidecarResult ReadTakeoutJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(LongPath.Prefix(path));
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return NotFound;

            // photoTakenTime is when the shutter fired. creationTime is when Google received
            // the upload, which for a photo taken years earlier is simply wrong, so it is
            // only used when there is nothing better.
            DateTime? taken = ReadTimestamp(root, "photoTakenTime")
                           ?? ReadTimestamp(root, "creationTime");

            bool hasGps = false;
            if (root.TryGetProperty("geoData", out var geo) && geo.ValueKind == JsonValueKind.Object)
            {
                double latitude = ReadDouble(geo, "latitude");
                double longitude = ReadDouble(geo, "longitude");
                // Takeout writes zeroes rather than omitting the block when there is no fix.
                hasGps = latitude != 0 || longitude != 0;
            }

            string? description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            if (taken is null && !hasGps && string.IsNullOrEmpty(description)) return NotFound;

            return new SidecarResult(Source.GoogleTakeout, path, taken, hasGps,
                                     string.IsNullOrWhiteSpace(description) ? null : description);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return NotFound;
        }
    }

    private static DateTime? ReadTimestamp(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
            return null;

        if (!node.TryGetProperty("timestamp", out var timestamp)) return null;

        string? text = timestamp.ValueKind == JsonValueKind.String
            ? timestamp.GetString()
            : timestamp.ValueKind == JsonValueKind.Number ? timestamp.GetInt64().ToString() : null;

        return long.TryParse(text, out long seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    private static double ReadDouble(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    /// <summary>
    /// Pulls the capture date out of an XMP sidecar by text search rather than by parsing the
    /// RDF properly. XMP is verbose XML with several equivalent spellings of the same field,
    /// and only one value is wanted here; a full parse would be a lot of machinery for it.
    /// </summary>
    private static SidecarResult ReadXmp(string path)
    {
        try
        {
            string text = File.ReadAllText(LongPath.Prefix(path));

            DateTime? taken = FindXmpDate(text, "exif:DateTimeOriginal")
                           ?? FindXmpDate(text, "photoshop:DateCreated")
                           ?? FindXmpDate(text, "xmp:CreateDate");

            bool hasGps = text.Contains("exif:GPSLatitude", StringComparison.Ordinal);

            return taken is null && !hasGps
                ? NotFound
                : new SidecarResult(Source.Xmp, path, taken, hasGps, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return NotFound;
        }
    }

    private static DateTime? FindXmpDate(string xml, string field)
    {
        // Both spellings occur: an attribute on rdf:Description, or a child element.
        foreach (string opener in new[] { field + "=\"", "<" + field + ">" })
        {
            int at = xml.IndexOf(opener, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            int start = at + opener.Length;
            int end = xml.IndexOfAny(['"', '<'], start);
            if (end <= start) continue;

            if (DateTime.TryParse(xml[start..end], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out var value))
                return value;
        }

        return null;
    }
}
