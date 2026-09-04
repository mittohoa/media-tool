using System.Drawing;
using System.Drawing.Imaging;
using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.Tests;

/// <summary>
/// A throwaway folder, catalog and volume registration, so the destructive tests run against
/// real files on a real filesystem rather than against a mock.
///
/// Mocking the filesystem here would defeat the purpose: the properties being checked are
/// about what happens to bytes on a disk, and most of the bugs these tests exist to catch
/// were in exactly the places a mock would have smoothed over.
/// </summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly string _root;
    private readonly CatalogDatabase _db;
    private readonly long _volumeId;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private long _nextFileKey = 1;

    /// <summary>The workspace folder, so a test can clean up what its own fixtures locked.</summary>
    public string Root => _root;

    /// <summary>The catalog file itself, for tests about the catalog rather than its contents.</summary>
    public string CatalogPath => Path.Combine(_root, "catalog.db");

    public string QuarantineRoot { get; }
    public CatalogDatabase Db => _db;
    public PlanExecutor Executor { get; }
    public QuarantinePurger Purger { get; }

    public TestWorkspace()
    {
        _root = Path.Combine(Path.GetTempPath(), "mediatool-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(_root, "library"));

        QuarantineRoot = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(QuarantineRoot);

        _db = CatalogDatabase.Open(Path.Combine(_root, "catalog.db"));

        // Register the volume the temp folder actually lives on, so relative paths resolve
        // through the same code the real tool uses.
        var volume = VolumeScanner.ForPath(_root)
            ?? throw new InvalidOperationException("Could not resolve the volume for the temp folder.");
        _volumeGuid = volume.VolumeGuid;
        _mountPoint = VolumeScanner.GetMountPointForPath(_root)!;
        _volumeId = _db.UpsertVolume(volume);

        Executor = new PlanExecutor(_db);
        Purger = new QuarantinePurger(_db);
    }

    public string PathIn(string name) => Path.Combine(_root, "library", name);

    /// <summary>Writes a deterministic JPEG. Different seeds give visibly different images.</summary>
    public string WriteJpeg(string path, int seed, int width = 320, int height = 240)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var bitmap = new Bitmap(width, height);
        var random = new Random(seed);
        for (int y = 0; y < height; y += 8)
            for (int x = 0; x < width; x += 8)
            {
                var colour = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256));
                using var brush = new SolidBrush(colour);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.FillRectangle(brush, x, y, 8, 8);
            }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
        bitmap.Save(path, codec, parameters);

        return path;
    }

    /// <summary>Two byte-identical files, as a straightforward copy would produce.</summary>
    public (string Keeper, string Victim) CreateIdenticalPair(string keeperName, string victimName)
    {
        string keeper = WriteJpeg(PathIn(keeperName), seed: 42);
        string victim = PathIn(victimName);
        File.WriteAllBytes(victim, File.ReadAllBytes(keeper));
        return (keeper, victim);
    }

    public List<PlanRow> PlanFor(string keeperPath, string victimPath, GroupKind kind,
                                 string? victimPixelHash = null)
    {
        var keeper = Candidate(keeperPath);
        var victim = Candidate(victimPath, victimPixelHash);

        return
        [
            new PlanRow
            {
                Group = 1, Kind = kind, Action = PlannedAction.Keep,
                File = keeper, Score = 0, Reason = "test keeper",
            },
            new PlanRow
            {
                Group = 1, Kind = kind, Action = PlannedAction.Quarantine,
                File = victim, Score = 0, Reason = "test victim",
                KeptFileKey = keeper.FileKey,
            },
        ];
    }

    private readonly Dictionary<string, long> _keysByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A candidate backed by a real row in the catalog.
    ///
    /// The row matters: quarantining marks a file missing and undo marks it present again,
    /// so a workspace whose files table was empty could not tell a working undo from a
    /// broken one. Identity is stable per path, as it is in a real catalog, so the same file
    /// planned twice is the same file.
    /// </summary>
    private KeeperCandidate Candidate(string fullPath, string? pixelHash = null)
    {
        string relative = Path.GetRelativePath(_mountPoint, fullPath);
        long size = new FileInfo(fullPath).Length;

        if (!_keysByPath.TryGetValue(relative, out long fileKey))
        {
            fileKey = _nextFileKey++;
            _keysByPath[relative] = fileKey;
        }

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO files (file_key, volume_id, rel_path, name, size, mtime, ctime,
                                   attributes, last_scan_id, present)
                VALUES ($k, $v, $p, $n, $s, 0, 0, 0, 1, 1)
                ON CONFLICT(file_key) DO UPDATE SET size = excluded.size, present = 1
                """;
            cmd.Parameters.AddWithValue("$k", fileKey);
            cmd.Parameters.AddWithValue("$v", _volumeId);
            cmd.Parameters.AddWithValue("$p", relative);
            cmd.Parameters.AddWithValue("$n", Path.GetFileName(fullPath));
            cmd.Parameters.AddWithValue("$s", size);
            cmd.ExecuteNonQuery();
        }

        return new KeeperCandidate
        {
            FileKey = fileKey,
            VolumeGuid = _volumeGuid,
            VolumeName = _mountPoint,
            RelativePath = relative,
            Size = size,
            MTime = 0,
            PixelHash = pixelHash,
        };
    }

    /// <summary>Whether the catalog still believes this file is on disk.</summary>
    public bool IsPresent(long fileKey)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT present FROM files WHERE file_key = $k";
        cmd.Parameters.AddWithValue("$k", fileKey);

        object? value = cmd.ExecuteScalar();
        return value is not null && Convert.ToInt64(value) == 1;
    }

    /// <summary>
    /// Rewrites a recorded action to point somewhere else — the corrupted-record scenario
    /// the purger's containment check exists to survive.
    /// </summary>
    public void RedirectFirstActionTo(string batchId, string destination)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            UPDATE actions SET destination = @dest, size = @size
            WHERE action_id = (SELECT MIN(action_id) FROM actions WHERE batch_id = @batch)
            """;
        cmd.Parameters.AddWithValue("@dest", destination);
        cmd.Parameters.AddWithValue("@size", new FileInfo(destination).Length);
        cmd.Parameters.AddWithValue("@batch", batchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A JPEG carrying a hand-built EXIF block, for the merge tests.</summary>
    public string WriteJpegWithExif(string path, int seed, string camera, string model, DateTime taken)
    {
        WriteJpeg(path, seed);
        byte[] jpeg = File.ReadAllBytes(path);
        byte[] app1 = ExifBuilder.BuildApp1(camera, model, taken, subSecond: null);

        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2);
        output.Write(app1, 0, app1.Length);
        output.Write(jpeg, 2, jpeg.Length - 2);
        File.WriteAllBytes(path, output.ToArray());

        return path;
    }

    /// <summary>Removes every APPn and COM segment, leaving the compressed image untouched.</summary>
    public string StripMetadata(string source, string destination)
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

    /// <summary>Saves the same picture through the encoder again at a lower quality.</summary>
    public string ReencodeJpeg(string source, string destination, int quality)
    {
        using var image = Image.FromFile(source);
        using var copy = new Bitmap(image);

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        copy.Save(destination, codec, parameters);

        return destination;
    }

    /// <summary>Repaints one rectangle, standing in for a subject that moved between frames.</summary>
    public string WithPatch(string source, string destination, int x, int y, int width, int height)
    {
        using var image = Image.FromFile(source);
        using var copy = new Bitmap(image);

        using (var graphics = Graphics.FromImage(copy))
        {
            var random = new Random(1234);
            for (int py = y; py < y + height; py += 6)
                for (int px = x; px < x + width; px += 6)
                {
                    using var brush = new SolidBrush(
                        Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                    graphics.FillRectangle(brush, px, py, 6, 6);
                }
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
        copy.Save(destination, codec, parameters);

        return destination;
    }

    /// <summary>Lifts every channel by a constant, as an exposure adjustment would.</summary>
    public string WithBrightness(string source, string destination, int delta)
    {
        using var image = Image.FromFile(source);
        using var copy = new Bitmap(image.Width, image.Height);

        using (var graphics = Graphics.FromImage(copy))
        {
            float shift = delta / 255f;
            var matrix = new System.Drawing.Imaging.ColorMatrix
            {
                Matrix40 = shift, Matrix41 = shift, Matrix42 = shift,
            };
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(matrix);
            graphics.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height),
                0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
        copy.Save(destination, codec, parameters);

        return destination;
    }

    public string PixelHashOf(string path) => Convert.ToHexString(
        MediaTool.Core.Imaging.PerceptualHash
            .Compute(MediaTool.Core.Imaging.ImageDecoder.Decode(path)).PixelHash);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
