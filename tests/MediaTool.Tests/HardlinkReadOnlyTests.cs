using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Getting read-only files back out again.
///
/// Hardlinking is offered as the safe option precisely because it is reversible, so anything
/// that can be linked but not unlinked turns the safe option into a one-way door. Old
/// archives — the kind that accumulate duplicates in the first place — are full of read-only
/// files: the case that found this was a folder of bitmaps from a 1990s game, where undo
/// linked 14,806 files and then refused to restore 17 of them.
/// </summary>
public class HardlinkReadOnlyTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly HardlinkExecutor _executor;

    public HardlinkReadOnlyTests() => _executor = new HardlinkExecutor(_workspace.Db);

    public void Dispose()
    {
        // Read-only files would otherwise defeat the workspace's own cleanup.
        foreach (string file in Directory.EnumerateFiles(_workspace.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        _workspace.Dispose();
    }

    [Fact]
    public void AReadOnlyDuplicateCanBeLinkedAndPutBack()
    {
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.bmp", "b.bmp");
        long originalLength = new FileInfo(duplicate).Length;

        File.SetAttributes(duplicate, FileAttributes.ReadOnly);
        File.SetAttributes(keeper, FileAttributes.ReadOnly);

        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);
        var linked = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);

        Assert.Equal(1, linked.Linked);
        Assert.Equal(0, linked.Errors);

        var undone = _executor.Undo(linked.BatchId, null, CancellationToken.None);

        Assert.Equal(0, undone.Errors);
        Assert.True(File.Exists(duplicate), "the duplicate has to come back to its own path");
        Assert.True(File.Exists(keeper), "the keeper is never touched");
        Assert.Equal(originalLength, new FileInfo(duplicate).Length);
    }

    [Fact]
    public void TheReadOnlyFlagSurvivesTheRoundTrip()
    {
        // The flag is somebody's intention about the file, not an obstacle to be discarded.
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.bmp", "b.bmp");
        File.SetAttributes(duplicate, FileAttributes.ReadOnly);

        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);
        var linked = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        _executor.Undo(linked.BatchId, null, CancellationToken.None);

        Assert.True(File.GetAttributes(duplicate).HasFlag(FileAttributes.ReadOnly),
            "a file that was read-only before must still be read-only afterwards");
    }

    [Fact]
    public void AReadOnlyFileThatIsNotOursIsStillLeftAlone()
    {
        // Clearing the flag must not become a licence to overwrite. If the path is occupied
        // by different content, it stays — read-only or not.
        var (keeper, duplicate) = _workspace.CreateIdenticalPair("a.bmp", "b.bmp");
        var rows = _workspace.PlanFor(keeper, duplicate, GroupKind.ExactBytes);

        var linked = _executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        Assert.Equal(1, linked.Linked);

        // Somebody replaces the link with their own work, and protects it.
        File.Delete(duplicate);
        _workspace.WriteJpeg(duplicate, seed: 77);
        File.SetAttributes(duplicate, FileAttributes.ReadOnly);
        long theirLength = new FileInfo(duplicate).Length;

        var undone = _executor.Undo(linked.BatchId, null, CancellationToken.None);

        Assert.Equal(0, undone.Linked);
        Assert.Equal(1, undone.Errors);
        Assert.Equal(theirLength, new FileInfo(duplicate).Length);
        Assert.True(File.GetAttributes(duplicate).HasFlag(FileAttributes.ReadOnly));
    }
}
