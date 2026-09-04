using MediaTool.Core.Util;

namespace MediaTool.Core.Metadata;

/// <summary>
/// Moves an EXIF block from one JPEG into another without touching a single pixel.
///
/// A JPEG is a sequence of segments: metadata blocks, then the compressed image, then the
/// end marker. Metadata and image data never overlap, so replacing the metadata segment is a
/// splice — copy the bytes on either side of it unchanged and put a different block in the
/// middle. No decoding, no re-encoding, and therefore no generational loss.
///
/// That property is what makes this worth doing at all. Recovering a capture date by
/// re-saving the file through an image library would cost a round of lossy compression, and
/// the point of the exercise is to lose nothing.
/// </summary>
public static class ExifTransplant
{
    private const byte Marker = 0xFF;
    private const byte SOI = 0xD8;
    private const byte SOS = 0xDA;
    private const byte EOI = 0xD9;
    private const byte APP1 = 0xE1;
    private const byte COM = 0xFE;

    /// <summary>
    /// Returns the donor's complete APP1 EXIF segment, marker and length included, ready to
    /// be inserted verbatim. Null when the file has none.
    /// </summary>
    public static byte[]? ExtractExifSegment(string jpegPath)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(LongPath.Prefix(jpegPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var segment in Segments(data))
        {
            if (segment.MarkerByte != APP1) continue;
            if (!IsExif(data, segment)) continue;

            byte[] copy = new byte[segment.TotalLength];
            Array.Copy(data, segment.Start, copy, 0, segment.TotalLength);
            return copy;
        }

        return null;
    }

    /// <summary>
    /// Produces a copy of <paramref name="jpegPath"/> carrying <paramref name="exifSegment"/>
    /// in place of whatever EXIF it had. Every other byte, including all compressed image
    /// data, is copied across untouched.
    /// </summary>
    public static byte[] Splice(string jpegPath, byte[] exifSegment)
    {
        byte[] data = File.ReadAllBytes(LongPath.Prefix(jpegPath));

        if (data.Length < 2 || data[0] != Marker || data[1] != SOI)
            throw new InvalidDataException("Not a JPEG: no start-of-image marker.");

        using var output = new MemoryStream(data.Length + exifSegment.Length);

        output.Write(data, 0, 2);                                   // SOI
        output.Write(exifSegment, 0, exifSegment.Length);           // the donor's EXIF, first

        int cursor = 2;
        foreach (var segment in Segments(data))
        {
            // Drop the recipient's own EXIF; anything else — JFIF, ICC profile, comments,
            // quantization and Huffman tables — is part of the file and stays.
            if (segment.MarkerByte == APP1 && IsExif(data, segment))
            {
                output.Write(data, cursor, segment.Start - cursor);
                cursor = segment.Start + segment.TotalLength;
                continue;
            }

            if (segment.MarkerByte == SOS) break;
        }

        output.Write(data, cursor, data.Length - cursor);           // everything from here on
        return output.ToArray();
    }

    /// <summary>True when this file can carry a transplanted EXIF block.</summary>
    public static bool IsSupported(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jfif", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct Segment(int Start, byte MarkerByte, int TotalLength)
    {
        /// <summary>Offset of the payload, past the marker and the two length bytes.</summary>
        public int PayloadStart => Start + 4;
    }

    private static IEnumerable<Segment> Segments(byte[] data)
    {
        int i = 2;

        while (i + 4 <= data.Length)
        {
            if (data[i] != Marker) yield break;
            byte marker = data[i + 1];

            // Standalone markers carry no length field.
            if (marker == SOI || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            if (marker == EOI) yield break;

            int length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length) yield break;

            yield return new Segment(i, marker, length + 2);

            // Everything after the start-of-scan marker is entropy-coded image data, which
            // has no segment structure and must be copied wholesale.
            if (marker == SOS) yield break;

            i += 2 + length;
        }
    }

    private static bool IsExif(byte[] data, Segment segment)
    {
        int at = segment.PayloadStart;
        return at + 6 <= data.Length
            && data[at] == 'E' && data[at + 1] == 'x' && data[at + 2] == 'i' && data[at + 3] == 'f'
            && data[at + 4] == 0 && data[at + 5] == 0;
    }

    private static bool IsComment(byte marker) => marker == COM;
}
