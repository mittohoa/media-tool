using MediaTool.Core.Storage;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Narrowing which files a command may touch.
///
/// Scope is the main control a person has over a tool that moves files: point it at one
/// folder, leave the rest alone. Its failures matter in one direction especially — a scope
/// that is too narrow returns less than asked and is obvious, while one that is too wide
/// quietly includes files the person believed they had excluded.
///
/// The predicate is exercised against a real catalog rather than compared as a string,
/// because what matters is which rows come back, not how the SQL is spelled.
/// </summary>
public class CatalogScopeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"winnow-scope-{Guid.NewGuid():N}.db");
    private readonly CatalogDatabase _db;

    public CatalogScopeTests()
    {
        _db = CatalogDatabase.Open(_path);

        AddVolume(1, "{vol-e}", "E:\\");
        AddVolume(2, "{vol-f}", "F:\\");

        AddFile(10, volume: 1, "Photos\\wedding\\a.jpg");
        AddFile(11, volume: 1, "Photos\\trip\\b.jpg");
        AddFile(12, volume: 1, "_mvc\\assets\\logo.png");
        AddFile(20, volume: 2, "Photos\\wedding\\c.jpg");
        AddFile(21, volume: 2, "Video\\clip.mp4");
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }

    private void AddVolume(long id, string guid, string mountPoint)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volumes (volume_id, volume_guid, last_mount_point, last_seen_utc)
            VALUES ($id, $guid, $mount, '2026-09-01T00:00:00Z')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$guid", guid);
        cmd.Parameters.AddWithValue("$mount", mountPoint);
        cmd.ExecuteNonQuery();
    }

    private void AddFile(long key, long volume, string relPath)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (file_key, volume_id, rel_path, name, size, mtime, ctime,
                               attributes, last_scan_id, present)
            VALUES ($k, $v, $p, $n, 1, 0, 0, 0, 1, 1)
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", volume);
        cmd.Parameters.AddWithValue("$p", relPath);
        cmd.Parameters.AddWithValue("$n", Path.GetFileName(relPath));
        cmd.ExecuteNonQuery();
    }

    private List<long> Matching(CatalogScope scope)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = $"SELECT file_key FROM files f WHERE f.present = 1{scope.ToSqlPredicate("f")} ORDER BY file_key";

        var keys = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetInt64(0));
        return keys;
    }

    [Fact]
    public void AnEmptyScopeIsTheWholeCatalog()
    {
        Assert.Equal([10L, 11L, 12L, 20L, 21L], Matching(new CatalogScope()));
    }

    [Fact]
    public void ADriveRootNarrowsToThatDrive()
    {
        // This is the failure that prompted the test: the drive letter was stripped and the
        // remainder trimmed to nothing, so the condition became "path starts with anything"
        // and the scope silently covered every drive.
        var scope = new CatalogScope();
        scope.Under.Add("F:\\");

        Assert.Equal([20L, 21L], Matching(scope));
    }

    [Fact]
    public void TheSameFolderNameOnAnotherDriveIsNotIncluded()
    {
        var scope = new CatalogScope();
        scope.Under.Add("F:\\Photos");

        Assert.Equal([20L], Matching(scope));
    }

    [Fact]
    public void APathWithoutADriveStillMatchesEveryVolume()
    {
        // Someone who types a bare folder name means it wherever it lives; only a drive
        // letter expresses "this one".
        var scope = new CatalogScope();
        scope.Under.Add("Photos");

        Assert.Equal([10L, 11L, 20L], Matching(scope));
    }

    [Fact]
    public void SeveralSubtreesAreUnioned()
    {
        var scope = new CatalogScope();
        scope.Under.Add("E:\\Photos\\trip");
        scope.Under.Add("F:\\Video");

        Assert.Equal([11L, 21L], Matching(scope));
    }

    [Fact]
    public void ExclusionStillWinsOverInclusion()
    {
        var scope = new CatalogScope();
        scope.Under.Add("E:\\");
        scope.Exclude.Add("_mvc");

        Assert.Equal([10L, 11L], Matching(scope));
    }

    [Fact]
    public void AScopeThatMatchesNothingReturnsNothingRatherThanEverything()
    {
        var scope = new CatalogScope();
        scope.Under.Add("Z:\\");

        Assert.Empty(Matching(scope));
    }
}
