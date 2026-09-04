using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using MediaTool.Core.Metadata;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The EXIF reader.
///
/// Worth testing carefully out of proportion to its size, because everything downstream
/// treats "no metadata" as a fact about the photograph rather than as a possible failure of
/// this class — and once it did fail silently, the keeper policy started discarding the
/// copies that still had their capture dates.
/// </summary>
public class MetadataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediatool-meta", Guid.NewGuid().ToString("N")[..12]);

    public MetadataTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheReaderLoadsAtAllRatherThanFailingInItsStaticInitialiser()
    {
        // The regression this exists for: a static field was built from another static field
        // declared below it, so the type initialiser threw on first use — and every later
        // call threw too, which looked exactly like a library with no metadata anywhere.
        string path = WriteJpegWithExif("plain.jpg", camera: "TESTCO", model: "T1",
            taken: new DateTime(2020, 5, 1, 12, 0, 0), subSecond: null);

        var metadata = JpegMetadata.Read(path);

        Assert.True(metadata.HasExif, "a file with EXIF must not read back as having none");
    }

    [Fact]
    public void CaptureDateIsRead()
    {
        string path = WriteJpegWithExif("dated.jpg", "TESTCO", "T1",
            new DateTime(2019, 6, 14, 8, 30, 15), subSecond: null);

        Assert.Equal(new DateTime(2019, 6, 14, 8, 30, 15), JpegMetadata.Read(path).DateTaken);
    }

    [Fact]
    public void SubSecondCaptureTimeIsRead()
    {
        // Whole seconds cannot separate frames of a burst; a camera at 6 fps puts several
        // exposures inside one of them.
        string path = WriteJpegWithExif("burst.jpg", "TESTCO", "T1",
            new DateTime(2025, 4, 13, 8, 9, 17), subSecond: 81);

        Assert.Equal(81, JpegMetadata.Read(path).SubSecond);
    }

    [Fact]
    public void CameraMakeAndModelAreCombinedWithoutRepeatingThemselves()
    {
        string path = WriteJpegWithExif("camera.jpg", "NIKON CORPORATION", "NIKON D750",
            new DateTime(2025, 1, 1), subSecond: null);

        // The model already starts with the make; "NIKON CORPORATION NIKON D750" would be
        // the naive concatenation and is what a viewer should not be shown.
        Assert.Equal("NIKON D750", JpegMetadata.Read(path).Camera);
    }

    [Fact]
    public void StrippingMetadataIsReportedAsAbsenceNotAsFailure()
    {
        string original = WriteJpegWithExif("original.jpg", "TESTCO", "T1",
            new DateTime(2020, 1, 1), subSecond: null);
        string stripped = StripMetadata(original, Path.Combine(_dir, "stripped.jpg"));

        var before = JpegMetadata.Read(original);
        var after = JpegMetadata.Read(stripped);

        Assert.True(before.HasExif);
        Assert.False(after.HasExif);
        Assert.Null(after.DateTaken);
        Assert.True(before.MetadataBytes > after.MetadataBytes);
    }

    [Fact]
    public void JpegQualityIsEstimatedAndOrdersCorrectly()
    {
        // The absolute number is an estimate; what has to hold is the ordering, because that
        // is what decides which copy is the re-save.
        string high = WritePlainJpeg("q95.jpg", quality: 95);
        string low = WritePlainJpeg("q40.jpg", quality: 40);

        int? highQuality = JpegMetadata.Read(high).JpegQuality;
        int? lowQuality = JpegMetadata.Read(low).JpegQuality;

        Assert.NotNull(highQuality);
        Assert.NotNull(lowQuality);
        Assert.True(highQuality > lowQuality,
            $"a quality-95 file should estimate above a quality-40 one (got {highQuality} vs {lowQuality})");
    }

    [Fact]
    public void AnUnreadableFileYieldsNoMetadataRatherThanThrowing()
        => Assert.False(JpegMetadata.Read(Path.Combine(_dir, "does-not-exist.jpg")).HasExif);

    [Fact]
    public void ATruncatedFileDoesNotCrashTheReader()
    {
        string path = Path.Combine(_dir, "truncated.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF]);   // claims a huge segment

        var metadata = JpegMetadata.Read(path);
        Assert.False(metadata.HasExif);
    }

    // ---- building test files ----------------------------------------------

    private string WritePlainJpeg(string name, int quality)
    {
        string path = Path.Combine(_dir, name);

        using var bitmap = new Bitmap(160, 120);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var random = new Random(7);
            for (int y = 0; y < 120; y += 10)
                for (int x = 0; x < 160; x += 10)
                {
                    using var brush = new SolidBrush(
                        Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                    graphics.FillRectangle(brush, x, y, 10, 10);
                }
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)quality);
        bitmap.Save(path, codec, parameters);

        return path;
    }

    /// <summary>Writes a real JPEG with a hand-built EXIF block spliced in after the SOI.</summary>
    private string WriteJpegWithExif(string name, string camera, string model, DateTime taken, int? subSecond)
    {
        byte[] jpeg = File.ReadAllBytes(WritePlainJpeg("base-" + name, quality: 90));
        byte[] app1 = ExifBuilder.BuildApp1(camera, model, taken, subSecond);

        string path = Path.Combine(_dir, name);
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);                 // SOI
        output.Write(app1, 0, app1.Length);       // our APP1, found before any other
        output.Write(jpeg, 2, jpeg.Length - 2);
        File.WriteAllBytes(path, output.ToArray());

        return path;
    }

    /// <summary>Removes every APPn and COM segment, leaving the compressed image untouched.</summary>
    private static string StripMetadata(string source, string destination)
    {
        byte[] data = File.ReadAllBytes(source);
        using var output = new MemoryStream();
        output.Write(data, 0, 2);

        int i = 2;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF) break;
            byte marker = data[i + 1];

            if (marker == 0xDA) { output.Write(data, i, data.Length - i); break; }

            int length = (data[i + 2] << 8) | data[i + 3];
            bool isMetadata = (marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE;
            if (!isMetadata) output.Write(data, i, 2 + length);
            i += 2 + length;
        }

        File.WriteAllBytes(destination, output.ToArray());
        return destination;
    }

}
