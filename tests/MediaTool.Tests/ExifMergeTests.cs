using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Metadata;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Recovering a capture date from the copy about to be discarded.
///
/// This is the only code that writes into the user's library, so the tests are written the
/// same way as the deletion ones: not "does it produce the right output" but "can it damage
/// anything if something goes wrong".
/// </summary>
public class ExifMergeTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    /// <summary>A pair sharing one picture: the keeper stripped, the donor still dated.</summary>
    private (string Keeper, string Donor) StrippedKeeperAndDatedDonor(DateTime taken)
    {
        string donor = _workspace.WriteJpegWithExif(_workspace.PathIn("donor.jpg"), seed: 21,
            camera: "TESTCO", model: "T1", taken: taken);
        string keeper = _workspace.StripMetadata(donor, _workspace.PathIn("keeper.jpg"));
        return (keeper, donor);
    }

    [Fact]
    public void ACandidateIsFoundWhenTheKeeperLostItsDateAndTheDonorKeptIt()
    {
        var taken = new DateTime(2019, 6, 14, 8, 30, 15);
        var (keeper, donor) = StrippedKeeperAndDatedDonor(taken);

        var candidates = new MetadataMerger(_workspace.Db)
            .FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));

        var candidate = Assert.Single(candidates);
        Assert.Equal(taken, candidate.DonorDate);
    }

    [Fact]
    public void NothingIsProposedWhenTheKeeperAlreadyHasADate()
    {
        string keeper = _workspace.WriteJpegWithExif(_workspace.PathIn("keeper.jpg"), seed: 21,
            camera: "TESTCO", model: "T1", taken: new DateTime(2019, 1, 1));
        string donor = _workspace.WriteJpegWithExif(_workspace.PathIn("donor.jpg"), seed: 21,
            camera: "TESTCO", model: "T1", taken: new DateTime(2019, 1, 1));

        Assert.Empty(new MetadataMerger(_workspace.Db)
            .FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes)));
    }

    [Fact]
    public void MergingRecoversTheDateWithoutChangingASinglePixel()
    {
        var taken = new DateTime(2019, 6, 14, 8, 30, 15);
        var (keeper, donor) = StrippedKeeperAndDatedDonor(taken);

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));

        string pixelsBefore = _workspace.PixelHashOf(keeper);

        var result = merger.Merge(candidates, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Merged);
        Assert.Equal(0, result.Errors);

        var after = JpegMetadata.Read(keeper);
        Assert.Equal(taken, after.DateTaken);

        // The whole justification for splicing rather than re-saving: the picture is not
        // merely similar afterwards, it is the same bytes of image data.
        Assert.Equal(pixelsBefore, _workspace.PixelHashOf(keeper));
    }

    [Fact]
    public void ADryRunWritesNothing()
    {
        var (keeper, donor) = StrippedKeeperAndDatedDonor(new DateTime(2019, 6, 14));

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));

        long sizeBefore = new FileInfo(keeper).Length;
        var result = merger.Merge(candidates, _workspace.QuarantineRoot,
            dryRun: true, null, CancellationToken.None);

        Assert.Equal(1, result.Merged);   // reports what it would do
        Assert.Equal(sizeBefore, new FileInfo(keeper).Length);
        Assert.Null(JpegMetadata.Read(keeper).DateTaken);
    }

    [Fact]
    public void TheOriginalIsQuarantinedRatherThanOverwritten()
    {
        var (keeper, donor) = StrippedKeeperAndDatedDonor(new DateTime(2019, 6, 14));

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));
        merger.Merge(candidates, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None);

        var backups = Directory.EnumerateFiles(_workspace.QuarantineRoot, "keeper.jpg", SearchOption.AllDirectories);
        Assert.Single(backups);
        Assert.Null(JpegMetadata.Read(backups.Single()).DateTaken);   // the pre-merge original
    }

    [Fact]
    public void AMergeCanBeUndoneLikeAnyOtherChange()
    {
        var (keeper, donor) = StrippedKeeperAndDatedDonor(new DateTime(2019, 6, 14));

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));
        var result = merger.Merge(candidates, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.NotNull(JpegMetadata.Read(keeper).DateTaken);

        // Undo has to remove the merged file first: its original path is occupied by the
        // very thing the merge put there.
        File.Delete(keeper);
        var undone = _workspace.Executor.Undo(result.BatchId, null, CancellationToken.None);

        Assert.Equal(1, undone.Moved);
        Assert.Null(JpegMetadata.Read(keeper).DateTaken);
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehindOnSuccess()
    {
        var (keeper, donor) = StrippedKeeperAndDatedDonor(new DateTime(2019, 6, 14));

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));
        merger.Merge(candidates, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(keeper)!, "*.mediatool-merge"));
    }

    [Fact]
    public void AnExistingTemporaryFileIsNeverOverwritten()
    {
        var (keeper, donor) = StrippedKeeperAndDatedDonor(new DateTime(2019, 6, 14));
        File.WriteAllText(keeper + ".mediatool-merge", "someone else's work in progress");

        var merger = new MetadataMerger(_workspace.Db);
        var candidates = merger.FindCandidates(_workspace.PlanFor(keeper, donor, GroupKind.ExactBytes));
        var result = merger.Merge(candidates, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Merged);
        Assert.Equal(1, result.Errors);
        Assert.Equal("someone else's work in progress", File.ReadAllText(keeper + ".mediatool-merge"));
    }

    [Fact]
    public void OnlyJpegsAreEligible()
    {
        Assert.True(ExifTransplant.IsSupported("photo.jpg"));
        Assert.True(ExifTransplant.IsSupported("photo.JPEG"));
        Assert.False(ExifTransplant.IsSupported("photo.nef"));
        Assert.False(ExifTransplant.IsSupported("photo.png"));
        Assert.False(ExifTransplant.IsSupported("photo.heic"));
    }

    [Fact]
    public void SplicingReplacesTheExistingExifRatherThanAppendingASecondOne()
    {
        string donor = _workspace.WriteJpegWithExif(_workspace.PathIn("donor.jpg"), seed: 3,
            camera: "TESTCO", model: "T1", taken: new DateTime(2021, 3, 3));
        string recipient = _workspace.WriteJpegWithExif(_workspace.PathIn("recipient.jpg"), seed: 3,
            camera: "OTHERCO", model: "X9", taken: new DateTime(1999, 1, 1));

        byte[] exif = ExifTransplant.ExtractExifSegment(donor)!;
        byte[] merged = ExifTransplant.Splice(recipient, exif);

        string output = _workspace.PathIn("merged.jpg");
        File.WriteAllBytes(output, merged);

        var metadata = JpegMetadata.Read(output);
        Assert.Equal(new DateTime(2021, 3, 3), metadata.DateTaken);
        Assert.Equal("TESTCO T1", metadata.Camera);
    }
}
