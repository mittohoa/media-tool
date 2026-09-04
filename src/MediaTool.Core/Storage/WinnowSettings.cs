using System.IO;
using System.Text.Json;

namespace MediaTool.Core.Storage;

/// <summary>
/// The two places a person needs to be able to choose, remembered between sessions.
///
/// Both defaults are wrong for somebody eventually. The catalog grows with the library and
/// lands on the system drive, which is the drive most likely to be full. The quarantine
/// folder holds every file on its way out — tens of gigabytes — and picking it from a dialog
/// on every apply is both tedious and an invitation to pick a different one by mistake.
///
/// Settings live beside the catalog rather than inside the install folder, because the
/// installer deletes its own folder on uninstall and a reinstall should not forget where the
/// library was.
/// </summary>
public sealed class WinnowSettings
{
    /// <summary>Where the catalog is, when the person has chosen somewhere other than the default.</summary>
    public string? CatalogPath { get; set; }

    /// <summary>Where quarantined files go, offered as the default on every apply.</summary>
    public string? QuarantineFolder { get; set; }

    /// <summary>Days a quarantined batch must sit before it may be purged.</summary>
    public int RetentionDays { get; set; } = 14;

    public static string FileFor(string baseFolder) =>
        Path.Combine(baseFolder, CatalogLocation.PreferredFolder, "settings.json");

    public static string DefaultFile => FileFor(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static WinnowSettings Load() => Load(DefaultFile);

    /// <summary>
    /// Reads the settings, falling back to defaults for anything missing or unreadable.
    ///
    /// A corrupt settings file must never stop the app opening: the worst it should cost is
    /// being asked where the quarantine folder is again.
    /// </summary>
    public static WinnowSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new WinnowSettings();

            var loaded = JsonSerializer.Deserialize<WinnowSettings>(File.ReadAllText(path));
            if (loaded is null) return new WinnowSettings();

            // A folder that has since been deleted or unplugged is not a setting worth
            // keeping: offering it would send files somewhere that no longer exists.
            if (loaded.QuarantineFolder is { Length: > 0 } q && !Directory.Exists(Path.GetPathRoot(q) ?? q))
                loaded.QuarantineFolder = null;

            if (loaded.RetentionDays < 1) loaded.RetentionDays = 14;

            return loaded;
        }
        catch (Exception)
        {
            return new WinnowSettings();
        }
    }

    public void Save() => Save(DefaultFile);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Format));
    }

    /// <summary>
    /// The catalog this configuration points at: the chosen one, or wherever the usual
    /// search finds one.
    /// </summary>
    public string ResolveCatalogPath() =>
        CatalogPath is { Length: > 0 } chosen ? chosen : CatalogLocation.Resolve();

    /// <summary>
    /// Free space on the drive holding a folder, or null when that cannot be determined —
    /// an unplugged drive, or a path that names no drive at all.
    /// </summary>
    public static long? FreeSpaceFor(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
