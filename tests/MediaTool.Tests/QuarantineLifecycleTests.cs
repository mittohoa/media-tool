using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The destructive path, end to end, on real files.
///
/// These are the tests that matter most: everything else in the project can be wrong and be
/// corrected later, but a photo removed by mistake is gone. Each one states a promise the
/// tool makes to someone about to point it at their own library.
/// </summary>
public class QuarantineLifecycleTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ---- the move is reversible ------------------------------------------

    [Fact]
    public void ApplyMovesIntoQuarantineAndLeavesTheOriginalGone()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.VerificationFailed);
        Assert.False(File.Exists(victim), "the quarantined file should have left its original path");
        Assert.True(File.Exists(keeper), "the keeper must never be touched");

        var quarantined = Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories);
        Assert.Single(quarantined);
    }

    [Fact]
    public void DryRunTouchesNothing()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: true, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);   // reports what it would do
        Assert.True(File.Exists(victim), "a dry run must not move anything");
        Assert.True(File.Exists(keeper));
    }

    [Fact]
    public void UndoPutsEveryFileBackWhereItCameFrom()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        Assert.False(File.Exists(victim));

        var undone = _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.Equal(1, undone.Moved);
        Assert.True(File.Exists(victim), "undo must restore the original path exactly");
    }

    [Fact]
    public void UndoWorksFromTheManifestAloneWhenTheCatalogIsUnavailable()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        string manifest = Path.Combine(_workspace.QuarantineRoot, applied.BatchId, "manifest.csv");
        Assert.True(File.Exists(manifest), "a manifest must be written next to the quarantined files");

        var undone = _workspace.Executor.UndoFromManifest(manifest, null, CancellationToken.None);

        Assert.Equal(1, undone.Moved);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void UndoRefusesToOverwriteSomethingThatTookTheOriginalPath()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        // Something else now occupies the path the file came from.
        _workspace.WriteJpeg(victim, seed: 99);

        var undone = _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.Equal(0, undone.Moved);
        Assert.Equal(1, undone.Errors);
        Assert.Contains(undone.Problems, p => p.Contains("occupied", StringComparison.OrdinalIgnoreCase));
    }

    // ---- verification refuses what it cannot prove ------------------------

    [Fact]
    public void ApplyRefusesAPairThatIsNotActuallyIdentical()
    {
        string keeper = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 1);
        string victim = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 2);

        // A plan claiming two unrelated photos are duplicates — an edited plan, or a bug.
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.VerificationFailed);
        Assert.True(File.Exists(victim), "a file that failed verification must not be touched");
    }

    [Fact]
    public void ApplyRefusesWhenTheKeeperIsMissing()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        // The copy being kept is gone, so nothing justifies removing the other one.
        File.Delete(keeper);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.True(File.Exists(victim));
        Assert.Contains(result.Problems, p => p.Contains("keeper missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ApplyRefusesAReviewedFileThatChangedSinceItWasReviewed()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ReviewedByHuman,
            victimPixelHash: "00000000000000000000000000000000");

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.VerificationFailed);
        Assert.Contains(result.Problems, p => p.Contains("changed since it was reviewed", StringComparison.Ordinal));
    }

    [Fact]
    public void AHumanReviewedDecisionCanActuallyBeExecuted()
    {
        // Regression: the review app stamped its rows NearDuplicate, which the executor
        // refuses on principle, so its Apply button could not move a single file.
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ReviewedByHuman);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.VerificationFailed);
    }

    [Fact]
    public void ANearDuplicateNobodyLookedAtIsNeverActedOn()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.NearDuplicate);

        var result = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Moved);
        Assert.True(File.Exists(victim));
    }

    // ---- the purge ---------------------------------------------------------

    [Fact]
    public void PurgeRefusesABatchYoungerThanTheRetentionPeriod()
    {
        string batch = ApplyOneFile();

        var result = _workspace.Purger.Purge(batch, _workspace.QuarantineRoot,
            TimeSpan.FromDays(14), dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Contains(result.Problems, p => p.Contains("retention period", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void PurgeIsADryRunUnlessAskedOtherwise()
    {
        string batch = ApplyOneFile();

        var result = _workspace.Purger.Purge(batch, _workspace.QuarantineRoot,
            TimeSpan.Zero, dryRun: true, null, CancellationToken.None);

        Assert.Equal(1, result.Deleted);   // reports what it would do
        Assert.NotEmpty(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void PurgeDeletesOnlyOnceTheBatchIsRipe()
    {
        string batch = ApplyOneFile();

        var result = _workspace.Purger.Purge(batch, _workspace.QuarantineRoot,
            TimeSpan.Zero, dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Errors);
        Assert.Empty(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void PurgeRefusesAnyPathOutsideTheQuarantineFolder()
    {
        // The scenario this exists for: a corrupted or hand-edited record pointing the
        // purger back at the original library. It must refuse on the path alone, without
        // needing to recognise the file.
        string batch = ApplyOneFile();
        string original = _workspace.PathIn("bystander.jpg");
        _workspace.WriteJpeg(original, seed: 7);

        _workspace.RedirectFirstActionTo(batch, original);

        var result = _workspace.Purger.Purge(batch, _workspace.QuarantineRoot,
            TimeSpan.Zero, dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.Errors);
        Assert.Contains(result.Problems, p => p.Contains("not inside the quarantine folder", StringComparison.Ordinal));
        Assert.True(File.Exists(original), "a file outside quarantine must survive whatever the record says");
    }

    [Fact]
    public void PurgeRefusesAFileWhoseSizeChangedSinceItWasQuarantined()
    {
        string batch = ApplyOneFile();

        string quarantined = Directory
            .EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories)
            .Single();
        File.AppendAllText(quarantined, "something else is here now");

        var result = _workspace.Purger.Purge(batch, _workspace.QuarantineRoot,
            TimeSpan.Zero, dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Deleted);
        Assert.Contains(result.Problems, p => p.Contains("size changed", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(quarantined));
    }

    [Theory]
    [InlineData(@"C:\Quarantine", @"C:\Quarantine\batch\a.jpg", true)]
    [InlineData(@"C:\Quarantine", @"C:\Quarantine", false)]
    [InlineData(@"C:\Quarantine", @"C:\Quarantine-old\a.jpg", false)]
    [InlineData(@"C:\Quarantine", @"C:\Photos\a.jpg", false)]
    [InlineData(@"C:\Quarantine", @"C:\Quarantine\..\Photos\a.jpg", false)]
    public void ContainmentIsCheckedBySegmentNotByStringPrefix(string root, string candidate, bool expected)
        => Assert.Equal(expected, QuarantinePurger.IsInside(root, candidate));

    private string ApplyOneFile()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);
        return _workspace.Executor
            .Execute(rows, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None)
            .BatchId;
    }
}
