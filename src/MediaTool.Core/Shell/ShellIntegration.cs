using Microsoft.Win32;

namespace MediaTool.Core.Shell;

public sealed record ShellStatus(bool ContextMenu, bool DesktopShortcut, bool StartMenuShortcut, string? RegisteredExe);

/// <summary>
/// Puts the app where the work already happens: the right-click menu of a folder in
/// Explorer, and a shortcut where the user looks for programs.
///
/// Everything is written under HKEY_CURRENT_USER, never HKEY_LOCAL_MACHINE. That means no
/// administrator prompt, no change to other accounts on the machine, and an uninstall that
/// removes exactly what was added. A photo tool has no business asking for machine-wide
/// privileges.
/// </summary>
public static class ShellIntegration
{
    /// <summary>
    /// Finds the app to register, starting from the folder the calling tool runs in.
    ///
    /// Next to the tool is the normal answer. In this repository the two projects build into
    /// separate trees, so the fallback crosses over to the app's — carrying the *current*
    /// build configuration with it. Naming one configuration here was a real bug: installing
    /// from a Release build silently registered the Debug executable, which then broke the
    /// right-click menu the next time that folder was rebuilt.
    /// </summary>
    public static string? FindAppExecutable(string baseDirectory, Func<string, bool>? exists = null)
    {
        exists ??= System.IO.File.Exists;

        string beside = System.IO.Path.Combine(baseDirectory, AppExeName);
        if (exists(beside)) return beside;

        // src/MediaTool.Cli/bin/<config>/<tfm>/  ->  src/MediaTool.App/bin/<config>/<tfm>/
        var tfm = new System.IO.DirectoryInfo(baseDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        var config = tfm.Parent;
        var src = config?.Parent?.Parent?.Parent;
        if (config is null || src is null) return null;

        string sibling = System.IO.Path.Combine(
            src.FullName, "MediaTool.App", "bin", config.Name, tfm.Name, AppExeName);

        return exists(sibling) ? sibling : null;
    }

    /// <summary>The GUI, which is what a right-click should open — not the console tool.</summary>
    public const string AppExeName = "Winnow.exe";

    private const string KeyName = "Winnow";
    private const string MenuText = "Scan for duplicates with Winnow";

    /// <summary>
    /// The three places a folder can be right-clicked. Explorer treats them as separate
    /// verbs, so an entry registered for one does not appear on the others.
    /// </summary>
    private static readonly (string Path, string Argument)[] Targets =
    [
        // On a folder's icon. %1 is the folder that was clicked.
        (@"Software\Classes\Directory\shell", "%1"),
        // On empty space inside a folder. %V is the folder being viewed — %1 is empty here.
        (@"Software\Classes\Directory\Background\shell", "%V"),
        // On a drive in "This PC".
        (@"Software\Classes\Drive\shell", "%1"),
    ];

    public static ShellStatus Status(string exePath)
    {
        using var probe = Registry.CurrentUser.OpenSubKey($@"{Targets[0].Path}\{KeyName}\command");
        string? command = probe?.GetValue(null) as string;

        return new ShellStatus(
            ContextMenu: command is not null,
            DesktopShortcut: File.Exists(ShortcutPath(Environment.SpecialFolder.DesktopDirectory)),
            StartMenuShortcut: File.Exists(ShortcutPath(Environment.SpecialFolder.Programs)),
            RegisteredExe: ExtractExe(command));
    }

    public static void InstallContextMenu(string exePath, string iconPath)
    {
        foreach (var (path, argument) in Targets)
        {
            using var shell = Registry.CurrentUser.CreateSubKey($@"{path}\{KeyName}")
                ?? throw new InvalidOperationException($"Could not create {path}\\{KeyName}");

            shell.SetValue(null, MenuText);
            shell.SetValue("Icon", File.Exists(iconPath) ? iconPath : $"{exePath},0");

            using var command = shell.CreateSubKey("command")
                ?? throw new InvalidOperationException("Could not create the command key");

            command.SetValue(null, $"\"{exePath}\" --scan \"{argument}\"");
        }
    }

    public static void RemoveContextMenu()
    {
        foreach (var (path, _) in Targets)
        {
            using var parent = Registry.CurrentUser.OpenSubKey(path, writable: true);
            // DeleteSubKeyTree throws when the key is absent, which is the normal state
            // for a second uninstall rather than a failure.
            if (parent?.OpenSubKey(KeyName) is not null) parent.DeleteSubKeyTree(KeyName);
        }
    }

    public static string CreateShortcut(string exePath, string iconPath, Environment.SpecialFolder where)
    {
        string path = ShortcutPath(where);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // WScript.Shell is the least-effort way to write a .lnk without hand-rolling
        // IShellLink; it has shipped with Windows since long before this app.
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is not available on this machine.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);

        link.TargetPath = exePath;
        link.WorkingDirectory = Path.GetDirectoryName(exePath);
        link.Description = "Winnow — find duplicate photos without deleting anything";
        if (File.Exists(iconPath)) link.IconLocation = iconPath;
        link.Save();

        return path;
    }

    public static void RemoveShortcuts()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            string path = ShortcutPath(folder);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string ShortcutPath(Environment.SpecialFolder where) =>
        Path.Combine(Environment.GetFolderPath(where), "Winnow.lnk");

    private static string? ExtractExe(string? command)
    {
        if (string.IsNullOrEmpty(command)) return null;

        int open = command.IndexOf('"');
        int close = open < 0 ? -1 : command.IndexOf('"', open + 1);
        return close > open ? command[(open + 1)..close] : command;
    }
}
