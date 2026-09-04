using MediaTool.Core.Storage;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Decisions surviving the window they were made in.
///
/// Reviewing near-duplicates is the only part of this tool a machine cannot do, which makes
/// each decision the most expensive thing in the catalog. They used to live in memory alone:
/// closing the app discarded hours of judgement, and because Apply acted only on confirmed
/// clusters, it discarded them silently.
/// </summary>
public class ReviewDecisionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"winnow-rd-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }

    [Fact]
    public void ADecisionOutlivesTheSessionThatMadeIt()
    {
        string key = ReviewDecisions.KeyFor([10L, 20L, 30L]);

        using (var db = CatalogDatabase.Open(_path))
            new ReviewDecisions(db).Save(key, keeperFileKey: 20, ReviewDecisionState.Confirmed);

        using (var db = CatalogDatabase.Open(_path))
        {
            var loaded = new ReviewDecisions(db).LoadAll();

            Assert.True(loaded.ContainsKey(key));
            Assert.Equal(20, loaded[key].KeeperFileKey);
            Assert.Equal(ReviewDecisionState.Confirmed, loaded[key].State);
        }
    }

    [Fact]
    public void AClusterIsNamedByItsMembersNotTheirOrder()
    {
        // The review list re-orders whenever the scope changes. If identity moved with it,
        // yesterday's decision would land on a different cluster today.
        Assert.Equal(ReviewDecisions.KeyFor([3L, 1L, 2L]), ReviewDecisions.KeyFor([1L, 2L, 3L]));
    }

    [Fact]
    public void ADifferentSetOfFilesIsADifferentCluster()
    {
        Assert.NotEqual(ReviewDecisions.KeyFor([1L, 2L, 3L]), ReviewDecisions.KeyFor([1L, 2L]));
        Assert.NotEqual(ReviewDecisions.KeyFor([1L, 2L, 3L]), ReviewDecisions.KeyFor([1L, 2L, 4L]));
    }

    [Fact]
    public void ChangingYourMindOverwritesRatherThanAccumulates()
    {
        string key = ReviewDecisions.KeyFor([7L, 8L]);

        using var db = CatalogDatabase.Open(_path);
        var decisions = new ReviewDecisions(db);

        decisions.Save(key, keeperFileKey: 7, ReviewDecisionState.Confirmed);
        decisions.Save(key, keeperFileKey: 8, ReviewDecisionState.Skipped);

        Assert.Equal(1, decisions.Count());
        var loaded = decisions.LoadAll()[key];
        Assert.Equal(8, loaded.KeeperFileKey);
        Assert.Equal(ReviewDecisionState.Skipped, loaded.State);
    }

    [Fact]
    public void ADecisionCanBeForgotten()
    {
        string key = ReviewDecisions.KeyFor([5L, 6L]);

        using var db = CatalogDatabase.Open(_path);
        var decisions = new ReviewDecisions(db);

        decisions.Save(key, keeperFileKey: 5, ReviewDecisionState.Confirmed);
        decisions.Forget(key);

        Assert.Equal(0, decisions.Count());
    }

    [Fact]
    public void AnExistingCatalogGainsTheTableWithoutLosingAnything()
    {
        // The migration runs against catalogs holding hours of scanning. Reaching the new
        // version must never be a reason to rebuild one.
        using (var db = CatalogDatabase.Open(_path))
        {
            using var cmd = db.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM volumes";
            cmd.ExecuteScalar();
        }

        using (var db = CatalogDatabase.Open(_path))
        {
            Assert.Equal(CatalogDatabase.SchemaVersion, db.GetSchemaVersion());
            Assert.Equal(0, new ReviewDecisions(db).Count());
        }
    }
}
