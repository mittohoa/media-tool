using MediaTool.Core.Actions;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The path that moves the most files.
///
/// Most of a library's redundancy is copies that decode to the same picture but differ in
/// their container bytes — the stripped-metadata case. There is no decision in such a group,
/// so the review app skips past them and offers them as one action; that one action is
/// responsible for more files than every human decision combined, and until now nothing
/// tested it.
///
/// What is being checked is that "same picture" is re-established from the files on disk at
/// the moment of the move, not taken on the plan's word.
/// </summary>
public class IdenticalPictureApplyTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void ACopyStrippedOfItsMetadataIsRecognisedAndMoved()
    {
        string keeper = _workspace.WriteJpegWithExif(_workspace.PathIn("photo.jpg"),
            seed: 21, camera: "SONY", model: "ILCE-7M4", taken: new DateTime(2025, 2, 5, 7, 53, 0));
        string victim = _workspace.StripMetadata(keeper, _workspace.PathIn("photo-stripped.jpg"));

        // The premise of the tier: different bytes, same picture.
        Assert.NotEqual(new FileInfo(keeper).Length, new FileInfo(victim).Length);

        var rows = _workspace.PlanFor(keeper, victim, GroupKind.IdenticalPicture);
        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.VerificationFailed);
        Assert.False(File.Exists(victim));
        Assert.True(File.Exists(keeper), "the keeper must never be touched");
    }

    [Fact]
    public void ADifferentPictureIsRefusedEvenWhenThePlanSaysOtherwise()
    {
        // A plan can be stale, hand-edited, or simply wrong. The check that stops a real
        // photograph being moved is the re-decode, so it has to hold against a plan that
        // asserts two unrelated pictures are copies of each other.
        string keeper = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 1);
        string other = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 2);

        var rows = _workspace.PlanFor(keeper, other, GroupKind.IdenticalPicture);
        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.VerificationFailed);
        Assert.True(File.Exists(other), "a picture that is not a duplicate must stay where it is");
    }

    [Fact]
    public void AFileReplacedAfterPlanningIsRefused()
    {
        string keeper = _workspace.WriteJpegWithExif(_workspace.PathIn("photo.jpg"),
            seed: 8, camera: "Canon", model: "EOS 50D", taken: new DateTime(2010, 9, 12, 17, 1, 0));
        string victim = _workspace.StripMetadata(keeper, _workspace.PathIn("photo-stripped.jpg"));

        var rows = _workspace.PlanFor(keeper, victim, GroupKind.IdenticalPicture);

        // Between planning and applying, something else overwrites the file. Time passes
        // between those two steps in real use - a plan can sit in a CSV for days.
        _workspace.WriteJpeg(victim, seed: 99);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.VerificationFailed);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void ADryRunOfTheBulkActionMovesNothing()
    {
        string keeper = _workspace.WriteJpegWithExif(_workspace.PathIn("photo.jpg"),
            seed: 3, camera: "NIKON", model: "COOLPIX L22", taken: new DateTime(2012, 7, 11, 22, 53, 0));
        string victim = _workspace.StripMetadata(keeper, _workspace.PathIn("photo-stripped.jpg"));

        var rows = _workspace.PlanFor(keeper, victim, GroupKind.IdenticalPicture);
        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: true, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);          // what it would do
        Assert.True(File.Exists(victim));       // what it actually did
    }

    [Fact]
    public void UndoRestoresWhatTheBulkActionMoved()
    {
        string keeper = _workspace.WriteJpegWithExif(_workspace.PathIn("photo.jpg"),
            seed: 12, camera: "SONY", model: "ILCE-7CM2", taken: new DateTime(2025, 4, 13, 8, 15, 0));
        string victim = _workspace.StripMetadata(keeper, _workspace.PathIn("photo-stripped.jpg"));
        long originalLength = new FileInfo(victim).Length;

        var rows = _workspace.PlanFor(keeper, victim, GroupKind.IdenticalPicture);
        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        Assert.Equal(1, applied.Moved);
        Assert.False(File.Exists(victim));

        _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.True(File.Exists(victim), "undo has to put the file back at its original path");
        Assert.Equal(originalLength, new FileInfo(victim).Length);
    }
}
