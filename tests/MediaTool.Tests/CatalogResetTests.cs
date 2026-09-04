using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Storage;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Starting the catalog over.
///
/// Two things have to hold. The catalog must not simply vanish — it is hours of walking,
/// hashing and decoding, and someone who reorganises a library and changes their mind should
/// not have paid for that twice. And it must refuse while files are still in quarantine,
/// because the catalog is the only thing that knows where each of them came from.
/// </summary>
public class CatalogResetTests : IDisposable
{
    private readonly TestWorkspace _workspace = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"winnow-reset-{Guid.NewGuid():N}");

    public CatalogResetTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _workspace.Dispose();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string NewCatalog(string name = "catalog.db")
    {
        string path = Path.Combine(_folder, name);
        using var db = CatalogDatabase.Open(path);
        return path;
    }

    [Fact]
    public void TheOldCatalogIsKeptRatherThanDeleted()
    {
        string path = NewCatalog();
        long size = new FileInfo(path).Length;

        var result = CatalogReset.Reset(path);

        Assert.True(result.Done);
        Assert.False(File.Exists(path), "the next open has to create an empty one");
        Assert.NotNull(result.ArchivedTo);
        Assert.True(File.Exists(result.ArchivedTo!), "the previous catalog must still be there");
        Assert.Equal(size, new FileInfo(result.ArchivedTo!).Length);
    }

    [Fact]
    public void TheWriteAheadLogGoesWithIt()
    {
        // Left behind, SQLite would try to replay the old library's log into the new
        // catalog. Driven through the injection points rather than real files, because a
        // real log only exists while a real connection is open.
        string path = Path.Combine(_folder, "catalog.db");
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            path, path + "-wal", path + "-shm",
        };
        var moves = new List<(string From, string To)>();

        var result = CatalogReset.Reset(path, onDisk.Contains, (from, to) => moves.Add((from, to)));

        Assert.True(result.Done);
        Assert.Equal(3, moves.Count);
        Assert.Contains(moves, m => m.From == path && m.To == result.ArchivedTo);
        Assert.Contains(moves, m => m.From == path + "-wal" && m.To == result.ArchivedTo + "-wal");
        Assert.Contains(moves, m => m.From == path + "-shm" && m.To == result.ArchivedTo + "-shm");
    }

    [Fact]
    public void AResetIsRefusedWhileFilesSitInQuarantine()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);
        _workspace.Executor.Execute(rows, _workspace.QuarantineRoot, dryRun: false, null, CancellationToken.None);

        string path = _workspace.CatalogPath;
        var result = CatalogReset.Reset(path);

        Assert.False(result.Done);
        Assert.Single(result.Blockers);
        Assert.Equal(1, result.Blockers[0].Files);
        Assert.True(File.Exists(path), "nothing may be moved while a batch is applied");
    }

    [Fact]
    public void OnceThatBatchIsUndoneTheResetIsAllowed()
    {
        var (keeper, victim) = _workspace.CreateIdenticalPair("photo.jpg", "photo-copy.jpg");
        var rows = _workspace.PlanFor(keeper, victim, GroupKind.ExactBytes);
        var applied = _workspace.Executor.Execute(rows, _workspace.QuarantineRoot,
            dryRun: false, null, CancellationToken.None);
        _workspace.Executor.Undo(applied.BatchId, null, CancellationToken.None);

        Assert.Empty(CatalogReset.PendingBatches(_workspace.CatalogPath));
    }

    [Fact]
    public void ResettingWhenThereIsNoCatalogIsNotAnError()
    {
        string path = Path.Combine(_folder, "never-existed.db");

        var result = CatalogReset.Reset(path);

        Assert.True(result.Done);
        Assert.Null(result.ArchivedTo);
    }

    [Fact]
    public void TwoResetsInTheSameSecondDoNotOverwriteEachOther()
    {
        // The stamp has one-second resolution, and losing the first archive to the second
        // would defeat the point of keeping it.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(_folder, "catalog.db");

        string first = CatalogReset.ArchiveNameFor(path, taken.Contains);
        taken.Add(first);
        string second = CatalogReset.ArchiveNameFor(path, taken.Contains);

        Assert.NotEqual(first, second);
    }
}
