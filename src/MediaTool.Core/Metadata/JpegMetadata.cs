using System.Buffers.Binary;
using System.Globalization;
using MediaTool.Core.Util;

namespace MediaTool.Core.Metadata;

/// <summary>
/// Reads EXIF and quantization tables straight out of the JPEG container.
///
/// Written by hand rather than taken from a library because this only needs the segment
/// header and the first few IFDs — a few hundred KB from the front of the file, never a
/// decode. Over a library of duplicates that is the difference between minutes and hours.
///
/// RAW formats are TIFF containers, so the same IFD walk reads them once the TIFF header is
/// found. Other containers return <see cref="ImageMetadata.None"/>, which the keeper policy
/// treats as "no evidence" rather than as evidence of absence.
/// </summary>
public static class JpegMetadata
{
    /// <summary>
    /// How much of the front of the file to read.
    ///
    /// Everything this class needs — the EXIF APP1 segment, the quantization tables, the
    /// frame header — sits before the compressed scan data, which is to say in the first few
    /// tens of KB. Reading further buys nothing: a JPEG segment cannot exceed 64KB, and TIFF
    /// containers keep IFD0 near the header. Over the tens of thousands of files inside a
    /// library's duplicate groups the difference is hundreds of gigabytes of pointless HDD
    /// reads versus a few.
    /// </summary>
    private const int MaxScanBytes = 256 * 1024;

    // EXIF tags
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagOrientation = 0x0112;
    private const ushort TagDateTime = 0x0132;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagSubSecTimeOriginal = 0x9291;

    public static ImageMetadata Read(string fullPath)
    {
        try
        {
            using var stream = new FileStream(LongPath.Prefix(fullPath), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);

            byte[] buffer = new byte[(int)Math.Min(stream.Length, MaxScanBytes)];

            // Read may return short; a partial buffer would truncate a segment mid-walk and
            // look like a file with no metadata rather than like an incomplete read.
            int read = 0;
            while (read < buffer.Length)
            {
                int got = stream.Read(buffer, read, buffer.Length - read);
                if (got == 0) break;
                read += got;
            }

            return Parse(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                         or ArgumentException or NotSupportedException)
        {
            // Only expected I/O failures are absorbed. A programming error must not be able
            // to disguise itself as "this file has no metadata" - that reads as evidence,
            // and the keeper policy would act on it.
            return ImageMetadata.None;
        }
    }

    private static ImageMetadata Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return ImageMetadata.None;

        // TIFF container (RAW): the IFD walk applies directly, no segment scan needed.
        if ((data[0] == 'I' && data[1] == 'I') || (data[0] == 'M' && data[1] == 'M'))
            // Zero, not the buffer length: in a TIFF container the metadata is not a
            // separable block, so any byte count here would be the read size masquerading
            // as a measurement.
            return ReadTiff(data, 0, metadataBytes: 0);

        if (data[0] != 0xFF || data[1] != 0xD8) return ImageMetadata.None;

        int metadataBytes = 0;
        int? quality = null;
        ImageMetadata? exif = null;

        int i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) break;
            byte marker = data[i + 1];

            // Standalone markers carry no length field.
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xDA || marker == 0xD9) break;   // start of scan: past all metadata

            int length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length) break;

            var payload = data.Slice(i + 4, length - 2);

            if (marker >= 0xE0 && marker <= 0xEF || marker == 0xFE) metadataBytes += length + 2;

            if (marker == 0xE1 && payload.Length > 6 &&
                payload[0] == 'E' && payload[1] == 'x' && payload[2] == 'i' && payload[3] == 'f')
                exif = ReadTiff(payload[6..], 0, metadataBytes: 0);

            if (marker == 0xDB) quality ??= EstimateQuality(payload);

            i += 2 + length;
        }

        return new ImageMetadata
        {
            HasExif = exif?.HasExif ?? false,
            TagCount = exif?.TagCount ?? 0,
            DateTaken = exif?.DateTaken,
            SubSecond = exif?.SubSecond,
            Camera = exif?.Camera,
            HasGps = exif?.HasGps ?? false,
            Orientation = exif?.Orientation ?? 0,
            JpegQuality = quality,
            MetadataBytes = metadataBytes,
        };
    }

    private static ImageMetadata ReadTiff(ReadOnlySpan<byte> tiff, int _, int metadataBytes)
    {
        if (tiff.Length < 8) return ImageMetadata.None;

        bool bigEndian = tiff[0] == 'M';
        if (ReadU16(tiff[2..], bigEndian) != 42) return ImageMetadata.None;

        uint ifd0 = ReadU32(tiff[4..], bigEndian);

        var state = new IfdState();
        WalkIfd(tiff, ifd0, bigEndian, state);
        if (state.ExifIfd != 0) WalkIfd(tiff, state.ExifIfd, bigEndian, state);

        string? camera = (state.Make, state.Model) switch
        {
            (null, null) => null,
            (null, var m) => m,
            (var mk, null) => mk,
            // "NIKON CORPORATION" + "NIKON D750" should read as "NIKON D750", not repeat the
            // brand. Manufacturers put the legal entity in Make and the brand in Model, so
            // comparing whole strings misses the overlap that a reader actually sees.
            var (mk, m) => SharesLeadingWord(mk!, m!) ? m : $"{mk} {m}",
        };

        return new ImageMetadata
        {
            HasExif = state.TagCount > 0,
            TagCount = state.TagCount,
            DateTaken = state.Date,
            SubSecond = state.SubSecond,
            Camera = camera,
            HasGps = state.HasGps,
            Orientation = state.Orientation,
            MetadataBytes = metadataBytes,
        };
    }

    private static bool SharesLeadingWord(string make, string model)
    {
        if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return true;

        string firstWord = make.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return firstWord.Length >= 3
            && model.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accumulates tags across IFD0 and the EXIF sub-IFD.</summary>
    private sealed class IfdState
    {
        public string? Make;
        public string? Model;
        public DateTime? Date;
        public bool DateIsOriginal;
        public int Orientation;
        public bool HasGps;
        public int TagCount;
        public uint ExifIfd;
        public int? SubSecond;
    }

    private static void WalkIfd(ReadOnlySpan<byte> tiff, uint offset, bool bigEndian, IfdState state)
    {
        if (offset == 0 || offset + 2 > tiff.Length) return;

        int count = ReadU16(tiff[(int)offset..], bigEndian);
        // A corrupt or hostile offset can point at anything; cap rather than walk off.
        if (count is <= 0 or > 512) return;

        for (int e = 0; e < count; e++)
        {
            int at = (int)offset + 2 + e * 12;
            if (at + 12 > tiff.Length) return;

            ushort tag = ReadU16(tiff[at..], bigEndian);
            ushort type = ReadU16(tiff[(at + 2)..], bigEndian);
            uint valueCount = ReadU32(tiff[(at + 4)..], bigEndian);
            state.TagCount++;

            switch (tag)
            {
                case TagExifIfd: state.ExifIfd = ReadU32(tiff[(at + 8)..], bigEndian); break;
                case TagGpsIfd: state.HasGps = ReadU32(tiff[(at + 8)..], bigEndian) != 0; break;
                case TagOrientation when type == 3: state.Orientation = ReadU16(tiff[(at + 8)..], bigEndian); break;
                case TagSubSecTimeOriginal:
                    string? fraction = ReadAscii(tiff, at, valueCount, bigEndian);
                    if (int.TryParse(fraction, out int subsec)) state.SubSecond = subsec;
                    break;

                case TagMake: state.Make = ReadAscii(tiff, at, valueCount, bigEndian); break;
                case TagModel: state.Model = ReadAscii(tiff, at, valueCount, bigEndian); break;

                case TagDateTime or TagDateTimeOriginal:
                    // DateTimeOriginal wins over DateTime: a re-save rewrites DateTime, but
                    // DateTimeOriginal is when the shutter actually fired — which is exactly
                    // the value that makes one copy worth more than another.
                    var parsed = ParseExifDate(ReadAscii(tiff, at, valueCount, bigEndian));
                    if (parsed is null) break;
                    bool isOriginal = tag == TagDateTimeOriginal;
                    if (state.Date is null || (isOriginal && !state.DateIsOriginal))
                    {
                        state.Date = parsed;
                        state.DateIsOriginal = isOriginal;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Recovers the quality setting from the luminance quantization table.
    ///
    /// The encoder derived the table from quality by a fixed formula, so the ratio between
    /// the table and the standard one inverts back to it. Entries that saturated at 1 or 255
    /// are dropped: they carry no ratio, and including them drags the estimate toward the
    /// middle regardless of the true quality.
    /// </summary>
    private static int? EstimateQuality(ReadOnlySpan<byte> dqt)
    {
        if (dqt.Length < 65) return null;

        int precisionAndId = dqt[0];
        if ((precisionAndId >> 4) != 0) return null;   // 16-bit tables are rare; skip rather than guess

        double ratioSum = 0;
        int used = 0;

        for (int i = 0; i < 64; i++)
        {
            int value = dqt[1 + i];
            if (value is <= 1 or >= 255) continue;
            ratioSum += 100.0 * value / StandardLuminanceZigzag[i];
            used++;
        }

        if (used < 8) return null;

        double scale = ratioSum / used;
        double quality = scale > 100 ? 5000.0 / scale : (200.0 - scale) / 2.0;
        return Math.Clamp((int)Math.Round(quality), 1, 100);
    }

    /// <summary>
    /// Declared before the table that consumes it: static fields initialise in declaration
    /// order, so building the zigzag table above this point would read a null array and kill
    /// the type initialiser - which then surfaces on every later call, not this one.
    /// </summary>
    private static readonly int[] ZigzagOrder =
    [
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Annex K luminance table, reordered into the zigzag sequence DQT stores.</summary>
    private static readonly int[] StandardLuminanceZigzag = BuildZigzag(
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ]);


    private static int[] BuildZigzag(int[] natural)
    {
        var result = new int[64];
        for (int i = 0; i < 64; i++) result[i] = natural[ZigzagOrder[i]];
        return result;
    }

    private static DateTime? ParseExifDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParseExact(text.Trim().TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> tiff, int entryAt, uint count, bool bigEndian)
    {
        if (count is 0 or > 256) return null;

        // Values of four bytes or fewer are stored inline in the entry itself.
        int at = count <= 4 ? entryAt + 8 : (int)ReadU32(tiff[(entryAt + 8)..], bigEndian);
        if (at < 0 || at + (int)count > tiff.Length) return null;

        var bytes = tiff.Slice(at, (int)count);
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];

        string value = System.Text.Encoding.ASCII.GetString(bytes).Trim();
        return value.Length == 0 ? null : value;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt16BigEndian(s)
        : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint ReadU32(ReadOnlySpan<byte> s, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(s)
        : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
