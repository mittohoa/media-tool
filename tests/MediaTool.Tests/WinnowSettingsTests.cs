using MediaTool.Core.Storage;
using Xunit;

namespace MediaTool.Tests;

/// <summary>
/// Remembering where things are kept.
///
/// Both choices are consequential and neither is guessable: the catalog defaults onto the
/// system drive, which is the drive most likely to fill up, and the quarantine folder holds
/// every file on its way out. What these tests protect is that a bad settings file costs at
/// most a re-answered question, never a refusal to start.
/// </summary>
public class WinnowSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"winnow-cfg-{Guid.NewGuid():N}");

    public WinnowSettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string File1 => Path.Combine(_folder, "settings.json");

    [Fact]
    public void ChoicesSurviveBetweenSessions()
    {
        new WinnowSettings
        {
            CatalogPath = @"E:\photos\catalog.db",
            QuarantineFolder = _folder,
            RetentionDays = 30,
        }.Save(File1);

        var loaded = WinnowSettings.Load(File1);

        Assert.Equal(@"E:\photos\catalog.db", loaded.CatalogPath);
        Assert.Equal(_folder, loaded.QuarantineFolder);
        Assert.Equal(30, loaded.RetentionDays);
    }

    [Fact]
    public void NoSettingsFileMeansDefaults()
    {
        var loaded = WinnowSettings.Load(Path.Combine(_folder, "absent.json"));

        Assert.Null(loaded.CatalogPath);
        Assert.Null(loaded.QuarantineFolder);
        Assert.Equal(14, loaded.RetentionDays);
    }

    [Fact]
    public void ACorruptFileCostsTheSettingsButNotTheApp()
    {
        // Refusing to start because a preferences file is malformed would be the worst
        // possible response to the least important file in the product.
        File.WriteAllText(File1, "{ this is not json");

        var loaded = WinnowSettings.Load(File1);

        Assert.Null(loaded.QuarantineFolder);
        Assert.Equal(14, loaded.RetentionDays);
    }

    [Fact]
    public void AQuarantineFolderOnAVanishedDriveIsForgotten()
    {
        // An unplugged drive should not be offered as a destination on the next apply.
        new WinnowSettings { QuarantineFolder = @"Q:\winnow-quarantine" }.Save(File1);

        Assert.Null(WinnowSettings.Load(File1).QuarantineFolder);
    }

    [Fact]
    public void ANonsenseRetentionFallsBackRatherThanDisablingTheWait()
    {
        // Zero or negative would mean "purge is allowed immediately", turning the safety
        // margin off through a typo.
        new WinnowSettings { RetentionDays = 0 }.Save(File1);

        Assert.Equal(14, WinnowSettings.Load(File1).RetentionDays);
    }

    [Fact]
    public void WithoutAChoiceTheCatalogIsFoundTheUsualWay()
    {
        Assert.Equal(CatalogLocation.Resolve(), new WinnowSettings().ResolveCatalogPath());
    }

    [Fact]
    public void SettingsLiveOutsideTheInstallFolder()
    {
        // The installer deletes its folder on uninstall; a reinstall should still know where
        // the library was.
        string path = WinnowSettings.FileFor(@"C:\base");

        Assert.DoesNotContain(CatalogLocation.InstallFolder, path);
        Assert.Contains(CatalogLocation.PreferredFolder, path);
    }

    [Fact]
    public void FreeSpaceIsReportedForARealDriveAndNotInventedForAFakeOne()
    {
        Assert.NotNull(WinnowSettings.FreeSpaceFor(_folder));
        Assert.Null(WinnowSettings.FreeSpaceFor(@"Q:\nowhere"));
    }
}
