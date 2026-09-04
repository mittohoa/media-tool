using System.IO;

namespace MediaTool.Core.Storage;

/// <summary>
/// Where the catalog lives, resolved the same way by the app and the command line.
///
/// The two used to disagree. The tool was built as "media-tool" and put its catalog under
/// that name; the app was named Winnow later and looked under the new one. Nothing warned
/// about it, so launching the app from its shortcut opened an empty catalog and a library
/// that had taken hours to scan looked like it had vanished.
///
/// The fix is to look for the old home as well as the new one rather than to move anything.
/// A catalog is expensive to rebuild and cheap to find, so finding it is the better trade.
/// </summary>
public static class CatalogLocation
{
    /// <summary>Where a catalog is created when there is not one already.</summary>
    ///
    /// <remarks>
    /// Deliberately not the installer's own folder. Velopack installs under
    /// %LOCALAPPDATA%\<packId>, and removes that folder entirely on uninstall — a catalog
    /// living there would be destroyed by an uninstall, taking hours of scanning with it and
    /// the record of every file still sitting in quarantine. The installer therefore uses a
    /// different id (see <see cref="InstallFolder"/>) and the data stays here.
    /// </remarks>
    public const string PreferredFolder = "Winnow";

    /// <summary>
    /// The installer's own folder, named here only so the two are visibly kept apart. Never
    /// write catalog data under this name.
    /// </summary>
    public const string InstallFolder = "WinnowApp";

    /// <summary>Folders that held the catalog under earlier names, newest first.</summary>
    private static readonly string[] LegacyFolders = ["media-tool"];

    public const string FileName = "catalog.db";

    /// <summary>
    /// The catalog to open when the user has not named one: whichever already exists,
    /// preferring the current name, falling back to a previous one, and otherwise the
    /// path a new catalog would be created at.
    /// </summary>
    public static string Resolve() => Resolve(LocalAppData);

    /// <summary>The same resolution against a given base folder, so it can be tested.</summary>
    public static string Resolve(string baseFolder)
    {
        string preferred = Path.Combine(baseFolder, PreferredFolder, FileName);
        if (File.Exists(preferred)) return preferred;

        foreach (string folder in LegacyFolders)
        {
            string legacy = Path.Combine(baseFolder, folder, FileName);
            if (File.Exists(legacy)) return legacy;
        }

        return preferred;
    }

    /// <summary>
    /// True when <see cref="Resolve"/> settled on a catalog left by an earlier version, so
    /// the caller can say which file it opened instead of leaving the user to guess.
    /// </summary>
    public static bool IsLegacy(string path) => IsLegacy(path, LocalAppData);

    public static bool IsLegacy(string path, string baseFolder) => LegacyFolders.Any(
        folder => string.Equals(path, Path.Combine(baseFolder, folder, FileName), StringComparison.OrdinalIgnoreCase));

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}
