using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The rules that decide which copy survives.
///
/// Every case here is one that went wrong during development. The first version scored the
/// criteria additively, which let a folder preference and a tidy filename outvote the fact
/// that one copy still had its capture date — so these tests are written as "weak evidence
/// must not beat strong evidence", not merely as "the expected file wins".
/// </summary>
public class KeeperPolicyTests
{
    private static KeeperCandidate File(
        string path,
        int width = 4000, int height = 3000,
        DateTime? taken = null,
        int exifTags = 0,
        int? quality = null,
        long size = 5_000_000)
        => new()
        {
            FileKey = path.GetHashCode(),
            VolumeGuid = "{test}",
            VolumeName = @"E:\",
            RelativePath = path,
            Size = size,
            MTime = 0,
            Width = width,
            Height = height,
            DateTaken = taken,
            HasExif = exifTags > 0,
            ExifTags = exifTags,
            JpegQuality = quality,
        };

    private static string KeeperOf(params KeeperCandidate[] candidates) =>
        KeeperPolicy.Rank(candidates, new KeeperOptions())[0].Candidate.RelativePath;

    private static string KeeperPreferring(string fragment, params KeeperCandidate[] candidates)
    {
        var options = new KeeperOptions();
        options.PreferredPathFragments.Add(fragment);
        return KeeperPolicy.Rank(candidates, options)[0].Candidate.RelativePath;
    }

    [Fact]
    public void HigherResolutionWins()
    {
        string keeper = KeeperOf(
            File(@"small.jpg", width: 1000, height: 750),
            File(@"large.jpg", width: 4000, height: 3000));

        Assert.Equal("large.jpg", keeper);
    }

    [Fact]
    public void TheCopyWithACaptureDateWins()
    {
        string keeper = KeeperOf(
            File(@"stripped.jpg"),
            File(@"original.jpg", taken: new DateTime(2019, 6, 14), exifTags: 40));

        Assert.Equal("original.jpg", keeper);
    }

    [Fact]
    public void APreferredFolderCannotOutweighACaptureDate()
    {
        // The regression: metadata is evidence about the photograph, a folder preference is
        // taste. Taste must not be able to discard the only record of when it was taken.
        string keeper = KeeperPreferring("Photos",
            File(@"Photos\nicely-named-2019-hoi-an.jpg"),
            File(@"OldBackup\IMG_9999.jpg", taken: new DateTime(2019, 6, 14), exifTags: 48));

        Assert.Equal(@"OldBackup\IMG_9999.jpg", keeper);
    }

    [Fact]
    public void ACameraSequenceNumberIsNotACopyMarker()
    {
        // IMG_9999 was being penalised as if "_9999" meant "copy", which demoted exactly the
        // camera originals the policy is supposed to protect.
        string keeper = KeeperOf(
            File(@"IMG_9999.jpg", taken: new DateTime(2020, 1, 1), exifTags: 40),
            File(@"holiday.jpg", taken: new DateTime(2020, 1, 1), exifTags: 40));

        // Neither should be demoted for its name; resolution and metadata are equal, so the
        // tie falls to the deterministic path ordering rather than to a bogus penalty.
        Assert.Contains(keeper, new[] { "IMG_9999.jpg", "holiday.jpg" });

        var ranked = KeeperPolicy.Rank(
            [File(@"IMG_9999.jpg", taken: new DateTime(2020, 1, 1), exifTags: 40)],
            new KeeperOptions());
        Assert.DoesNotContain(ranked[0].Reasons, r => r.Contains("copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AWindowsCopySuffixIsACopyMarker()
    {
        var ranked = KeeperPolicy.Rank([File(@"holiday (2).jpg")], new KeeperOptions());
        Assert.Contains(ranked[0].Reasons, r => r.Contains("copy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ARawOriginalOutranksAJpegRenderedFromIt()
    {
        // Losing the negative to keep the print is the one mistake here that cannot be undone.
        string keeper = KeeperOf(
            File(@"NTN_5200.JPG", taken: new DateTime(2025, 4, 13), exifTags: 48),
            File(@"NTN_5200.NEF", taken: new DateTime(2025, 4, 13), exifTags: 48));

        Assert.Equal("NTN_5200.NEF", keeper);
    }

    [Fact]
    public void ResolutionStillBeatsFormatWhenTheGapIsLarge()
    {
        // A thumbnail-sized RAW is not worth more than a full-resolution JPEG; discarded
        // pixels are the more expensive loss.
        string keeper = KeeperOf(
            File(@"tiny.nef", width: 400, height: 300),
            File(@"full.jpg", width: 6000, height: 4000));

        Assert.Equal("full.jpg", keeper);
    }

    [Fact]
    public void LessCompressionWinsWhenEverythingElseMatches()
    {
        string keeper = KeeperOf(
            File(@"resaved.jpg", quality: 60, taken: new DateTime(2020, 1, 1), exifTags: 30),
            File(@"original.jpg", quality: 98, taken: new DateTime(2020, 1, 1), exifTags: 30));

        Assert.Equal("original.jpg", keeper);
    }

    [Fact]
    public void RankingIsStableAcrossRuns()
    {
        // A plan that changes between runs cannot be reviewed before it is applied.
        var candidates = new[]
        {
            File(@"b\photo.jpg"), File(@"a\photo.jpg"), File(@"c\photo.jpg"),
        };

        string first = KeeperOf(candidates);
        for (int i = 0; i < 5; i++) Assert.Equal(first, KeeperOf(candidates));
    }

    [Theory]
    [InlineData("photo.nef", FormatTier.Raw)]
    [InlineData("photo.CR2", FormatTier.Raw)]
    [InlineData("photo.dng", FormatTier.Raw)]
    [InlineData("photo.png", FormatTier.Lossless)]
    [InlineData("photo.tiff", FormatTier.Lossless)]
    [InlineData("photo.jpg", FormatTier.Lossy)]
    [InlineData("photo.heic", FormatTier.Lossy)]
    [InlineData("photo.xyz", FormatTier.Unknown)]
    public void FormatTiersAreRecognised(string name, FormatTier expected)
        => Assert.Equal(expected, FormatTiers.Of(name));
}
