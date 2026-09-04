using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// The catalog agreeing with the disk after an undo.
///
/// Quarantining marks a file missing so later commands stop offering it. Undo used to move
/// the file back without saying so, leaving the two out of step: the photo was on disk, but
/// every command behaved as though it were gone. Nothing was lost and nothing warned either,
/// which is the worst combination — a library that has quietly shrunk.
/// </summary>
public class UndoCatalogStateTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void UndoPutsTheFileBackIntoTheCatalogAsWellAsOntoTheDisk()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);
        long victimKey = rows.First(r => r.Action == PlannedAction.Quarantine).File.FileKey;

        Assert.True(_workspace.IsPresent(victimKey));

        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        Assert.Equal(1, applied.Moved);
        Assert.False(_workspace.IsPresent(victimKey), "quarantining marks the file missing");

        _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.True(File.Exists(victim), "the file has to be back on disk");
        Assert.True(_workspace.IsPresent(victimKey), "and back in the catalog's view of the library");
    }

    [Fact]
    public void AnUndoneLibraryStillFindsItsDuplicates()
    {
        // The consequence that matters: a file the catalog thinks is gone cannot be found as
        // a duplicate again, so an undone batch would silently stop being reclaimable.
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);

        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.True(_workspace.IsPresent(rows.First(r => r.Action == PlannedAction.Quarantine).File.FileKey));
    }
}
