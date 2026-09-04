using System.Buffers.Binary;
using System.Text;

namespace MediaTool.Tests;

/// <summary>
/// Builds a valid EXIF APP1 block so the tests can create files with exactly the metadata a
/// case needs, rather than depending on sample photos checked into the repository.
///
/// It follows the container rules the reader relies on — notably that a value of four bytes
/// or fewer is stored inside its directory entry instead of at an offset. Getting that wrong
/// once produced a test that failed against correct code.
/// </summary>
internal static class ExifBuilder
{
    public static byte[] BuildApp1(string make, string model, DateTime taken, int? subSecond)
    {
        byte[] makeBytes = Ascii(make);
        byte[] modelBytes = Ascii(model);
        byte[] dateBytes = Ascii(taken.ToString("yyyy:MM:dd HH:mm:ss"));
        byte[] subSecBytes = subSecond is { } s ? Ascii(s.ToString()) : [];

        const int ifd0Offset = 8;
        int ifd0Size = 2 + 3 * 12 + 4;
        int makeOffset = ifd0Offset + ifd0Size;
        int modelOffset = makeOffset + makeBytes.Length;
        int exifIfdOffset = modelOffset + modelBytes.Length;

        int exifEntries = subSecond is null ? 1 : 2;
        int exifIfdSize = 2 + exifEntries * 12 + 4;
        int dateOffset = exifIfdOffset + exifIfdSize;
        int subSecOffset = dateOffset + dateBytes.Length;

        var tiff = new MemoryStream();
        var writer = new BinaryWriter(tiff);

        writer.Write((byte)'I'); writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)ifd0Offset);

        writer.Write((ushort)3);
        WriteEntry(writer, 0x010F, 2, (uint)makeBytes.Length, (uint)makeOffset, makeBytes);    // Make
        WriteEntry(writer, 0x0110, 2, (uint)modelBytes.Length, (uint)modelOffset, modelBytes);  // Model
        WriteEntry(writer, 0x8769, 4, 1, (uint)exifIfdOffset);                      // ExifIFD pointer
        writer.Write(0u);

        writer.Write(makeBytes);
        writer.Write(modelBytes);

        writer.Write((ushort)exifEntries);
        WriteEntry(writer, 0x9003, 2, (uint)dateBytes.Length, (uint)dateOffset, dateBytes);    // DateTimeOriginal
        if (subSecond is not null)
            WriteEntry(writer, 0x9291, 2, (uint)subSecBytes.Length, (uint)subSecOffset, subSecBytes);
        writer.Write(0u);

        writer.Write(dateBytes);
        if (subSecond is not null) writer.Write(subSecBytes);

        byte[] tiffBytes = tiff.ToArray();
        byte[] payload = new byte[6 + tiffBytes.Length];
        Encoding.ASCII.GetBytes("Exif\0\0").CopyTo(payload, 0);
        tiffBytes.CopyTo(payload, 6);

        byte[] segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);

        return segment;
    }

    /// <summary>
    /// Writes one IFD entry. Values of four bytes or fewer live inside the entry itself
    /// rather than at an offset — a detail the reader honours, so a builder that ignored it
    /// would be testing the wrong file format.
    /// </summary>
    private static void WriteEntry(BinaryWriter writer, ushort tag, ushort type, uint count,
                                   uint offset, byte[]? inlineValue = null)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);

        if (inlineValue is { Length: <= 4 } && count <= 4)
        {
            writer.Write(inlineValue);
            for (int i = inlineValue.Length; i < 4; i++) writer.Write((byte)0);
        }
        else
        {
            writer.Write(offset);
        }
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text + '\0');
}
