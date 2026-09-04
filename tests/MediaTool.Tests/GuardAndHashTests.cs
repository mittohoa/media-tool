using MediaTool.Core.Crawl;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Imaging;
using MediaTool.Core.Storage;
using MediaTool.Core.Util;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The rules that decide whether two images are the same photograph, and the plumbing that
/// decides which files are looked at in the first place.
/// </summary>
public class GuardAndHashTests
{
    // ---- telling a burst apart from a set of copies -----------------------

    [Theory]
    // Consecutive frames from one camera: same prefix, different number.
    [InlineData("_MG_8676.jpg", "_MG_8677.jpg", true)]
    [InlineData("NTN_5177.JPG", "NTN_5178.JPG", true)]
    [InlineData("20140622_144445.jpg", "20140622_144446.jpg", true)]
    // The same frame, wherever it lives or however it was copied.
    [InlineData("_MG_8676.jpg", "_MG_8676.jpg", false)]
    [InlineData("_MG_8676.jpg", "_MG_8676 (1).jpg", false)]
    [InlineData("_MG_8676.jpg", "_MG_8676 - Copy.jpg", false)]
    // A RAW and its JPEG are one exposure, not two.
    [InlineData("IMG_0452.CR2", "IMG_0452.jpg", false)]
    // Unrelated names carry no sequence to compare.
    [InlineData("holiday.jpg", "wedding.jpg", false)]
    // A bare number is not a camera series; rejecting on it would separate real copies.
    [InlineData("1.jpg", "2.jpg", false)]
    public void FrameNumbersDistinguishExposuresFromCopies(string a, string b, bool separate)
        => Assert.Equal(separate, SimilarityIndex.IsSameSeriesDifferentFrame(a, b));

    // ---- perceptual hashing ----------------------------------------------

    [Fact]
    public void TheSameImageAlwaysProducesTheSameFingerprint()
    {
        var image = SyntheticImage(seed: 5);
        var first = PerceptualHash.Compute(image);
        var second = PerceptualHash.Compute(image);

        Assert.Equal(first.DHash, second.DHash);
        Assert.Equal(first.PHash, second.PHash);
        Assert.Equal(first.PixelHash, second.PixelHash);
    }

    [Fact]
    public void RotatingAnImageByAQuarterTurnDoesNotChangeItsPerceptualHash()
    {
        // Rotation tolerance lives here rather than in the decoder, because the decoder
        // deliberately does not apply EXIF orientation — a stripped copy has lost that flag.
        var upright = SyntheticImage(seed: 11);
        var turned = Rotate90(upright);

        Assert.Equal(PerceptualHash.Compute(upright).PHash, PerceptualHash.Compute(turned).PHash);
    }

    [Fact]
    public void DifferentImagesProduceDifferentFingerprints()
    {
        var a = PerceptualHash.Compute(SyntheticImage(seed: 1));
        var b = PerceptualHash.Compute(SyntheticImage(seed: 2));

        Assert.NotEqual(a.PixelHash, b.PixelHash);
        Assert.True(PerceptualHash.HammingDistance(a.PHash, b.PHash) > 8,
            "unrelated images should be far apart, not marginally different");
    }

    [Fact]
    public void ThumbnailComparisonIgnoresAUniformBrightnessShift()
    {
        // The regression: pHash drops the DC coefficient precisely so brightness does not
        // matter, then the verifier compared absolute grey levels and threw out the pairs
        // the hash had just accepted.
        var image = SyntheticImage(seed: 3);
        var brighter = new DecodedImage
        {
            Gray = image.Gray.Select(v => (byte)Math.Min(255, v + 40)).ToArray(),
            Width = image.Width,
            Height = image.Height,
        };

        double distance = PerceptualHash.ThumbnailDistance(
            PerceptualHash.Compute(image).Thumb16,
            PerceptualHash.Compute(brighter).Thumb16);

        Assert.True(distance < 8, $"a brightness shift should not look like a different photo (was {distance:F1})");
    }

    [Fact]
    public void ThumbnailComparisonStillSeparatesGenuinelyDifferentImages()
    {
        double distance = PerceptualHash.ThumbnailDistance(
            PerceptualHash.Compute(SyntheticImage(seed: 1)).Thumb16,
            PerceptualHash.Compute(SyntheticImage(seed: 2)).Thumb16);

        Assert.True(distance > 8, $"unrelated images should not pass the verifier (was {distance:F1})");
    }

    [Fact]
    public void AFlatImageIsReportedAsLowContrast()
    {
        // Blank scans all hash alike; the contrast figure is what keeps them out of clustering.
        var blank = new DecodedImage
        {
            Gray = Enumerable.Repeat((byte)200, 64 * 64).ToArray(),
            Width = 1000,
            Height = 1000,
        };

        Assert.True(PerceptualHash.Compute(blank).Contrast < 1.0);
    }

    // ---- scoping ----------------------------------------------------------

    [Fact]
    public void AnEmptyScopeAddsNothingToTheQuery()
        => Assert.Equal("", new CatalogScope().ToSqlPredicate());

    [Fact]
    public void UnderMatchesASubtreeNotASubstring()
    {
        var scope = new CatalogScope();
        scope.Under.Add(@"E:\Photos");

        string sql = scope.ToSqlPredicate();

        Assert.Contains("LIKE 'Photos%'", sql);      // drive letter stripped, anchored at the start
        Assert.StartsWith(" AND ", sql);
    }

    [Fact]
    public void ScopeValuesAreEscapedSoTheyCannotAlterTheQuery()
    {
        var scope = new CatalogScope();
        scope.Exclude.Add("50% off's_folder");

        string sql = scope.ToSqlPredicate();

        Assert.Contains(@"\%", sql);      // wildcard neutralised
        Assert.Contains(@"\_", sql);      // single-character wildcard neutralised
        Assert.Contains("''", sql);       // quote doubled rather than closing the literal
    }

    // ---- crawl filters -----------------------------------------------------

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.JPG", true)]
    [InlineData("photo.nef", true)]
    [InlineData("notes.txt", false)]
    [InlineData("noextension", false)]
    [InlineData("trailingdot.", false)]
    public void ExtensionFilteringIsCaseInsensitive(string name, bool accepted)
        => Assert.Equal(accepted, new CrawlOptions().MatchesExtension(name));

    [Fact]
    public void ExcludedPathFragmentsMatchAnywhereInTheTree()
    {
        var options = new CrawlOptions();
        options.ExcludedPathFragments.Add("_mvc");

        Assert.True(options.IsExcludedPath(@"projects\_mvc\assets\img"));
        Assert.True(options.IsExcludedPath(@"_MVC\assets"));
        Assert.False(options.IsExcludedPath(@"projects\photos\2019"));
    }

    // ---- long paths --------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Photos\a.jpg", @"\\?\C:\Photos\a.jpg")]
    [InlineData(@"\\server\share\a.jpg", @"\\?\UNC\server\share\a.jpg")]
    [InlineData(@"\\?\C:\already.jpg", @"\\?\C:\already.jpg")]
    public void LongPathsGetTheDevicePrefixExactlyOnce(string input, string expected)
        => Assert.Equal(expected, LongPath.Prefix(input));

    [Fact]
    public void JoiningAvoidsThePathCombineDriveRelativeTrap()
    {
        // Path.Combine("E:", "Photos") yields the drive-relative "E:Photos", which resolves
        // against the process's current directory on that drive rather than its root.
        Assert.Equal(@"E:\Photos", LongPath.Join(@"E:\", "Photos"));
        Assert.Equal(@"E:\Photos\2019", LongPath.Join(@"E:\Photos", "2019"));
    }

    // ---- helpers -----------------------------------------------------------

    private static DecodedImage SyntheticImage(int seed)
    {
        var random = new Random(seed);
        var gray = new byte[64 * 64];

        // Blocks rather than per-pixel noise: noise averages out under downscaling and would
        // make every seed produce a similar fingerprint.
        for (int y = 0; y < 64; y += 8)
            for (int x = 0; x < 64; x += 8)
            {
                byte value = (byte)random.Next(256);
                for (int dy = 0; dy < 8; dy++)
                    for (int dx = 0; dx < 8; dx++)
                        gray[(y + dy) * 64 + x + dx] = value;
            }

        return new DecodedImage { Gray = gray, Width = 4000, Height = 3000 };
    }

    private static DecodedImage Rotate90(DecodedImage source)
    {
        var rotated = new byte[source.Gray.Length];
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                rotated[x * 64 + (63 - y)] = source.Gray[y * 64 + x];

        return new DecodedImage { Gray = rotated, Width = source.Height, Height = source.Width };
    }
}
