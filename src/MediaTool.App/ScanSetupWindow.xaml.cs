using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaTool.Core.Storage;
using MediaTool.Core.Volumes;

namespace MediaTool.App;

public sealed class DriveChoice : Observable
{
    public required string Guid { get; init; }
    public required string Mount { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required string SizeText { get; init; }
    public required bool CanSelect { get; init; }
    public required string Note { get; init; }
    public required bool IsWarning { get; init; }

    private bool _selected;
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public Brush NoteBrush => IsWarning
        ? new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x2E))
        : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
}

public sealed class FolderChoice : Observable
{
    public required string Path { get; init; }

    private bool _selected = true;
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

/// <summary>
/// The first thing someone sees when there is nothing to review yet: which drives and
/// folders to look at.
///
/// Everything here was previously reachable only from the command line, which meant a person
/// who opened the app had no way to scan anything at all — the window could only show what
/// some earlier terminal session had catalogued.
/// </summary>
public partial class ScanSetupWindow : Window
{
    private readonly ObservableCollection<DriveChoice> _drives = [];
    private readonly ObservableCollection<FolderChoice> _folders = [];
    private readonly ObservableCollection<string> _exclusions = [];

    private CancellationTokenSource? _cancellation;
    private bool _running;

    /// <summary>True when a scan completed, so the caller knows to open the review.</summary>
    public bool Completed { get; private set; }

    /// <summary>
    /// The places this run actually scanned, and what it was told to ignore.
    ///
    /// The window used to keep this to itself, so choosing a folder to scan added it to the
    /// catalog and then showed the whole library anyway — the folder you picked buried among
    /// everything picked before it. Choosing a place to scan is also a statement about what
    /// you want to look at.
    /// </summary>
    public IReadOnlyList<string> ScannedRoots { get; private set; } = [];

    public IReadOnlyList<string> ScannedExclusions { get; private set; } = [];

    public ScanSetupWindow()
    {
        InitializeComponent();

        DriveList.ItemsSource = _drives;
        FolderList.ItemsSource = _folders;
        ExclusionList.ItemsSource = _exclusions;

        // Two defaults that reflect what this library actually turned out to contain: a web
        // project tree and a source tree, both full of images that are not photographs.
        _exclusions.Add("_mvc");
        _exclusions.Add("node_modules");

        LoadDrives();
        UpdateSummary();
    }

    private void LoadDrives()
    {
        var runner = new PipelineRunner(App.CatalogPath);
        var catalogued = runner.CataloguedByVolume();

        foreach (var target in ScanTargetSelector.Choose(VolumeScanner.EnumerateVolumes()))
        {
            if (target.Path.Length == 0) continue;   // no mount point: nothing to offer

            catalogued.TryGetValue(target.Volume.VolumeGuid, out var known);
            string note = target.Note.Length > 0
                ? target.Note
                : known.Files > 0
                    ? $"{known.Files:N0} images already catalogued — a rescan only picks up what changed"
                    : "not scanned yet";

            _drives.Add(new DriveChoice
            {
                Guid = target.Volume.VolumeGuid,
                Mount = target.Path,
                Label = target.Volume.Label ?? "",
                Kind = target.Volume.StorageKind.ToString(),
                SizeText = FormatBytes((long)target.Volume.TotalBytes),
                CanSelect = true,
                // A cloud drive can be chosen, but never by default and never quietly.
                Selected = target.Recommended && known.Files > 0,
                Note = note,
                IsWarning = !target.Recommended,
            });
        }

        foreach (var drive in _drives) drive.PropertyChanged += (_, _) => UpdateSummary();
    }

    // ---- picking ----------------------------------------------------------

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder to scan",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true) return;

        foreach (string path in dialog.FolderNames)
        {
            if (_folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            var choice = new FolderChoice { Path = path };
            choice.PropertyChanged += (_, _) => UpdateSummary();
            _folders.Add(choice);
        }

        UpdateSummary();
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;

        var match = _folders.FirstOrDefault(f => f.Path == path);
        if (match is not null) _folders.Remove(match);

        UpdateSummary();
    }

    private void OnAddExclusion(object sender, RoutedEventArgs e) => AddExclusion();

    private void OnExclusionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddExclusion();
        e.Handled = true;
    }

    private void AddExclusion()
    {
        string text = ExclusionInput.Text.Trim();
        if (text.Length == 0 || _exclusions.Contains(text, StringComparer.OrdinalIgnoreCase)) return;

        _exclusions.Add(text);
        ExclusionInput.Clear();
    }

    private void OnRemoveExclusion(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string fragment }) _exclusions.Remove(fragment);
    }

    private List<string> ChosenRoots()
    {
        return Chosen().Roots;
    }

    /// <summary>
    /// The places to walk, and any chosen folder a ticked drive already covers.
    ///
    /// Those folders used to be dropped without a word, so ticking a drive and then adding a
    /// folder inside it scanned the entire drive — the opposite of what picking a folder is
    /// asking for, and invisible until the scan was over.
    /// </summary>
    private (List<string> Roots, List<string> Absorbed) Chosen()
    {
        var roots = _drives.Where(d => d.Selected).Select(d => d.Mount).ToList();
        var absorbed = new List<string>();

        foreach (var folder in _folders.Where(f => f.Selected))
        {
            // A folder inside an already-chosen drive would be walked twice.
            if (roots.Any(r => folder.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            {
                absorbed.Add(folder.Path);
                continue;
            }

            roots.Add(folder.Path);
        }

        return (roots, absorbed);
    }

    private void UpdateSummary()
    {
        NoFoldersHint.Visibility = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var (roots, absorbed) = Chosen();
        StartButton.IsEnabled = roots.Count > 0;

        if (roots.Count == 0)
        {
            SummaryText.Text = "Choose at least one drive or folder.";
            SummaryText.Foreground = (Brush)FindResource("TextDim");
            return;
        }

        // Naming the places beats counting them. "2 places" reads as agreement even when the
        // places are not the ones that were picked.
        string named = string.Join(", ", roots.Take(3));
        if (roots.Count > 3) named += $" and {roots.Count - 3} more";

        if (absorbed.Count > 0)
        {
            SummaryText.Text = absorbed.Count == 1
                ? $"Will scan {named} — which already covers {absorbed[0]}, so the whole drive is walked"
                : $"Will scan {named} — which already covers {absorbed.Count} of the folders chosen below";
            SummaryText.Foreground = (Brush)FindResource("Warn");
            return;
        }

        SummaryText.Text = $"Will scan {named}";
        SummaryText.Foreground = (Brush)FindResource("TextDim");
    }

    /// <summary>Where the previous catalog went, so the person can be told rather than guess.</summary>
    private string? _archivedCatalog;

    private bool ConfirmFreshStart()
    {
        var answer = MessageBox.Show(
            """
            Scan into an empty catalog?

            Everything scanned before is forgotten: file lists, hashes and decoded
            fingerprints. Only these places will be known afterwards.

            No photograph is touched. The old catalog is renamed, not deleted, so it can be
            put back by hand if this was a mistake.

            Re-reading a large library takes hours.
            """,
            "Start fresh", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        return answer == MessageBoxResult.OK;
    }

    // ---- running ----------------------------------------------------------

    /// <summary>
    /// Warns as soon as the box is ticked rather than at the moment of no return, and
    /// refuses outright while files are sitting in quarantine — the catalog is what knows
    /// where each of them came from.
    /// </summary>
    private void OnFreshStartChanged(object sender, RoutedEventArgs e)
    {
        if (FreshStart.IsChecked != true) { SummaryText.Text = ""; return; }

        var blockers = CatalogReset.PendingBatches(App.CatalogPath);
        if (blockers.Count > 0)
        {
            FreshStart.IsChecked = false;
            MessageBox.Show(
                $"""
                {blockers.Count} batch(es) are still applied, holding {blockers.Sum(b => b.Files):N0} file(s)
                in quarantine. The catalog is what knows where each one came from, so it
                cannot be set aside yet.

                Put them back first, or purge them once you are sure:
                  winnow-cli undo --batch <id>
                  winnow-cli purge --batch <id> --quarantine <folder> --execute
                """,
                "Cannot start fresh yet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SummaryText.Text = "the current catalog will be kept under a dated name, not deleted";
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var roots = ChosenRoots();
        if (roots.Count == 0) return;

        if (FreshStart.IsChecked == true && !ConfirmFreshStart()) return;

        _running = true;
        _cancellation = new CancellationTokenSource();

        PickerPane.Visibility = Visibility.Collapsed;
        ProgressPane.Visibility = Visibility.Visible;
        StartButton.IsEnabled = false;
        StartButton.Content = "Scanning…";
        CancelButton.Content = "Stop";
        SummaryText.Text = "";

        var progress = new Progress<PipelineProgress>(Render);
        var runner = new PipelineRunner(App.CatalogPath);

        ScannedRoots = roots;
        ScannedExclusions = [.. _exclusions];

        try
        {
            if (FreshStart.IsChecked == true)
            {
                var reset = CatalogReset.Reset(App.CatalogPath);
                if (!reset.Done)
                    throw new InvalidOperationException(
                        "A batch is still applied, so the catalog cannot be set aside.");

                _archivedCatalog = reset.ArchivedTo;
                if (_archivedCatalog is not null)
                    SummaryText.Text = $"previous catalog kept at {_archivedCatalog}";
            }

            await runner.RunAsync(roots, [.. _exclusions], progress, _cancellation.Token);
            Completed = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Stopping is a normal outcome. Whatever finished is already in the catalog, so
            // the review is still worth opening.
            Completed = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\nNo files were changed.",
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Error);
            _running = false;
            PickerPane.Visibility = Visibility.Visible;
            ProgressPane.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = true;
            StartButton.Content = "Start scan";
            CancelButton.Content = "Cancel";
        }
    }

    private void Render(PipelineProgress p)
    {
        StageText.Text = p.Stage;
        StageDetail.Text = p.Detail;
        StageCounter.Text = p.Stage == "Done" ? "" : $"step {p.StageIndex} of {p.StageCount}";

        // The bar spans all four stages so it only ever moves forward, rather than resetting
        // to zero each time a stage begins.
        double overall = ((p.StageIndex - 1) + p.Fraction) / p.StageCount;
        double available = Math.Max(0, ActualWidth - 80);
        ProgressFill.Width = Math.Max(0, available * Math.Clamp(overall, 0, 1));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            _cancellation?.Cancel();
            CancelButton.IsEnabled = false;
            CancelButton.Content = "Stopping…";
            return;
        }

        DialogResult = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _cancellation?.Cancel();
        base.OnClosing(e);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:F0} {units[unit]}";
    }
}
