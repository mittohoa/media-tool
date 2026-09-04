using System.Windows;

namespace MediaTool.App;

public partial class App : Application
{
    /// <summary>What the person has chosen about where things are kept.</summary>
    public static MediaTool.Core.Storage.WinnowSettings Settings { get; } =
        MediaTool.Core.Storage.WinnowSettings.Load();

    private static string? _catalogPath;

    /// <summary>
    /// Catalog path, overridable with a --db argument for testing against a copy.
    ///
    /// Resolved on first use rather than in a field initialiser. Static fields initialise in
    /// declaration order, so an eager default here would read <see cref="Settings"/> before
    /// it exists and take the whole app down at startup with a null reference — which is
    /// exactly what it did. Resolving lazily removes the ordering question rather than
    /// answering it, so re-ordering the members later cannot bring the crash back.
    /// </summary>
    public static string CatalogPath
    {
        get => _catalogPath ??= Settings.ResolveCatalogPath();
        private set => _catalogPath = value;
    }

    /// <summary>
    /// Which slice of the catalog to review. Scanning is done once over everything; this is
    /// how a session narrows to the photos and leaves a web project's assets alone.
    /// </summary>
    public static MediaTool.Core.Storage.CatalogScope Scope { get; } = new();

    /// <summary>
    /// A folder handed over by Explorer's right-click menu. The session opens scoped to it
    /// rather than to the whole catalog, because someone who right-clicked one folder is
    /// asking about that folder.
    /// </summary>
    public static string? ScanTarget { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--db") CatalogPath = e.Args[i + 1];
            else if (e.Args[i] == "--under") Scope.Under.Add(e.Args[i + 1]);
            else if (e.Args[i] == "--exclude") Scope.Exclude.Add(e.Args[i + 1]);
            else if (e.Args[i] == "--scan") ScanTarget = e.Args[i + 1];
        }

        // A crash mid-review must not look like the app simply vanished; nothing here has
        // touched a file, and the user needs to know that.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                "No files were moved. Close and reopen to continue reviewing.",
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
