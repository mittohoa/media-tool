using System.IO;
using MediaTool.Core.Storage;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Finding the catalog the user already has.
///
/// This exists because of a real failure: the app and the command line looked in different
/// folders, so opening the app from its shortcut showed an empty library and hours of
/// scanning appeared to be gone. Nothing was lost, but nothing said so either.
/// </summary>
public class CatalogLocationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "winnow-loc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }

    private string Place(string folder)
    {
        string path = Path.Combine(_base, folder, CatalogLocation.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void ACatalogLeftByTheOlderNameIsStillFound()
    {
        string legacy = Place("media-tool");

        string resolved = CatalogLocation.Resolve(_base);

        Assert.Equal(legacy, resolved);
        Assert.True(CatalogLocation.IsLegacy(resolved, _base));
    }

    [Fact]
    public void TheCurrentNameWinsWhenBothExist()
    {
        Place("media-tool");
        string preferred = Place(CatalogLocation.PreferredFolder);

        string resolved = CatalogLocation.Resolve(_base);

        Assert.Equal(preferred, resolved);
        Assert.False(CatalogLocation.IsLegacy(resolved, _base));
    }

    [Fact]
    public void WithNothingOnDiskItNamesWhereANewCatalogGoes()
    {
        string resolved = CatalogLocation.Resolve(_base);

        Assert.Equal(Path.Combine(_base, CatalogLocation.PreferredFolder, CatalogLocation.FileName), resolved);
        Assert.False(File.Exists(resolved));
        Assert.False(CatalogLocation.IsLegacy(resolved, _base));
    }

    [Fact]
    public void TheCatalogNeverLivesInsideTheInstallerFolder()
    {
        // Velopack removes its install folder wholesale on uninstall. A catalog kept there
        // would be destroyed by an uninstall, along with the record of every file still
        // sitting in quarantine — files that could then only be recovered by hand.
        Assert.NotEqual(CatalogLocation.InstallFolder, CatalogLocation.PreferredFolder);

        string resolved = CatalogLocation.Resolve(_base);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + CatalogLocation.InstallFolder + Path.DirectorySeparatorChar,
            resolved);
    }

    [Fact]
    public void ResolvingTwiceGivesTheSameAnswer()
    {
        // The app and the command line each resolve independently; if they could disagree
        // the bug this guards against would simply come back in a new form.
        Place("media-tool");

        Assert.Equal(CatalogLocation.Resolve(_base), CatalogLocation.Resolve(_base));
    }
}
