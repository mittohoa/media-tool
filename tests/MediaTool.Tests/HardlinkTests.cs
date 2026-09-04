using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Collapsing byte-identical duplicates into one file that two paths share.
///
/// What makes this attractive over quarantining is that nothing disappears — both paths keep
/// working — so the tests are mostly about the ways that could stop being true: a pair that
/// is not actually identical, a second run doing it twice, and getting back out again.
/// </summary>
public class HardlinkTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly HardlinkExecutor _executor;

    public HardlinkTests() => _executor = new HardlinkExecutor(_workspace.Db);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void BothPathsSurviveTheOperation()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        var result = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, result.Linked);
        Assert.Equal(0, result.Errors);

        // The entire point: the library looks exactly as it did.
        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(duplicate));
        Assert.Equal(new FileInfo(keeper).Length, new FileInfo(duplicate).Length);
    }

    [Fact]
    public void ADryRunLinksNothing()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        var result = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: true, null, CancellationToken.None);

        Assert.Equal(1, result.Linked);   // reports what it would do
        Assert.Empty(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void FilesThatAreNotIdenticalAreRefused()
    {
        // Linking two different photos would destroy one outright, so the bytes are compared
        // again here rather than taken on the plan's word.
        string keeper = _workspace.WriteJpeg(_workspace.PathIn("a.jpg"), seed: 1);
        string other = _workspace.WriteJpeg(_workspace.PathIn("b.jpg"), seed: 2);
        var rows = _workspace.PlanFor(keeper, other, GroupKind.ExactBytes);

        var result = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.VerificationFailed);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void NearDuplicatesAreNeverLinked()
    {
        // A near duplicate is a different picture. There is no version of this operation
        // that makes sense for one, so it is refused on the group kind alone.
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.NearDuplicate);

        var result = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, result.Linked);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void RunningTwiceIsANoOp()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        _executor.Execute(rows, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None);

        var again = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, again.Linked);
        Assert.Equal(1, again.Skipped);
    }

    [Fact]
    public void TheOriginalIsKeptInQuarantineNotDiscarded()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        _executor.Execute(rows, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None);

        Assert.Single(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void UndoRestoresAnIndependentCopy()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        var linked = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        Assert.Equal(1, linked.Linked);

        var undone = _executor.Undo(linked.BatchId, null, CancellationToken.None);

        Assert.Equal(1, undone.Linked);
        Assert.Equal(0, undone.Errors);
        Assert.True(File.Exists(keeper));
        Assert.True(File.Exists(duplicate));
        Assert.Empty(Directory.EnumerateFiles(_workspace.QuarantineRoot, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void UndoLeavesAnUnrelatedFileOccupyingThePathAlone()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.jpg", "b.jpg");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        var linked = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        // Someone replaced the link with a real, different file. Removing that would be
        // destroying work, so undo has to refuse rather than assume.
        File.Delete(duplicate);
        _workspace.WriteJpeg(duplicate, seed: 77);

        var undone = _executor.Undo(linked.BatchId, null, CancellationToken.None);

        Assert.Equal(0, undone.Linked);
        Assert.Equal(1, undone.Errors);
        Assert.True(File.Exists(duplicate));
    }
}
