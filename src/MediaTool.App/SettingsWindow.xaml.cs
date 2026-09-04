using System.IO;
using System.Windows;
using System.Windows.Media;
using MediaTool.Core.Storage;

namespace MediaTool.App;

/// <summary>
/// Choosing where the catalog and the quarantine folder live.
///
/// The screen's job is less to collect two paths than to make the consequence of each one
/// visible before it is chosen: how much room is left on that drive, and whether a move into
/// it will be instant or a full copy. Those are the two facts that turn a reasonable-looking
/// choice into an overnight wait or a full disk.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly WinnowSettings _settings;
    private string? _catalog;
    private string? _quarantine;

    /// <summary>True when the catalog path changed, which only takes effect on restart.</summary>
    public bool CatalogMoved { get; private set; }

    public SettingsWindow(WinnowSettings settings)
    {
        InitializeComponent();

        _settings = settings;
        _catalog = settings.CatalogPath;
        _quarantine = settings.QuarantineFolder;

        RetentionBox.Text = settings.RetentionDays.ToString();
        Refresh();
    }

    private void Refresh()
    {
        CatalogBox.Text = _catalog ?? CatalogLocation.Resolve();
        CatalogNote.Text = _catalog is null
            ? "Default location. Change it to keep the catalog off a drive that is filling up."
            : "Chosen location. The app has to be reopened for a change here to take effect.";

        QuarantineBox.Text = _quarantine ?? "(asked for each time)";
        DescribeQuarantine();
        UpdateFooter();
    }

    private void DescribeQuarantine()
    {
        if (_quarantine is null)
        {
            QuarantineNote.Text = "No folder set, so every apply asks. Setting one makes the choice once.";
            QuarantineNote.Foreground = (Brush)FindResource("TextDim");
            return;
        }

        long? free = WinnowSettings.FreeSpaceFor(_quarantine);
        string room = free is null ? "free space unknown" : $"{ReviewFile.FormatBytes(free.Value)} free";

        QuarantineNote.Text = $"{room} on this drive. A file moved within one drive is instant and needs no " +
                              "extra room; moved to another drive it is copied in full, which needs room for " +
                              "every file at once.";
        QuarantineNote.Foreground = free is not null && free < 5L * 1024 * 1024 * 1024
            ? (Brush)FindResource("Warn")
            : (Brush)FindResource("TextDim");
    }

    private void UpdateFooter() => FooterNote.Text = $"Saved in {WinnowSettings.DefaultFile}";

    private void OnPickCatalog(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Where should the catalog be kept?",
            FileName = Path.GetFileName(CatalogBox.Text),
            Filter = "Winnow catalog|*.db",
            InitialDirectory = SafeDirectoryOf(CatalogBox.Text),
            OverwritePrompt = false,   // choosing an existing catalog is a normal thing to do
        };

        if (dialog.ShowDialog() != true) return;

        _catalog = dialog.FileName;
        CatalogMoved = true;
        Refresh();
    }

    private void OnPickQuarantine(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should files go instead of being deleted?",
            InitialDirectory = _quarantine is not null ? SafeDirectoryOf(_quarantine) : "",
        };

        if (dialog.ShowDialog() != true) return;

        _quarantine = dialog.FolderName;
        Refresh();
    }

    private static string SafeDirectoryOf(string path)
    {
        try
        {
            string? directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return Directory.Exists(directory) ? directory! : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void OnRetentionChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (SaveButton is null) return;   // fires once while the window is still being built

        bool ok = int.TryParse(RetentionBox.Text, out int days) && days >= 1 && days <= 365;
        RetentionNote.Text = ok ? "" : "1 to 365";
        SaveButton.IsEnabled = ok;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_quarantine is not null && !Directory.Exists(_quarantine))
        {
            MessageBox.Show($"{_quarantine}\n\nThat folder is not there. Choose one that exists.",
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.CatalogPath = _catalog;
        _settings.QuarantineFolder = _quarantine;
        _settings.RetentionDays = int.Parse(RetentionBox.Text);

        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be written.\n\n{ex.GetType().Name}: {ex.Message}",
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
