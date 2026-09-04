using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Imaging;

namespace MediaTool.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ReviewFile : Observable
{
    public required KeeperCandidate Source { get; init; }

    /// <summary>Position in the cluster, so the 1-9 shortcuts have something to name.</summary>
    public required int Ordinal { get; init; }

    public string FullPath => Source.FullPath;
    public string Name => Source.Name;
    /// <summary>
    /// The last few folders, not the whole path.
    ///
    /// Text trimming cuts from the right, which for a path throws away the only part that
    /// distinguishes two copies — "…\Backup_USB\2019" tells you which one this is, while
    /// "Users\Someone\AppData\Local\…" tells you nothing and is what a right-trimmed full
    /// path shows.
    /// </summary>
    public string Folder
    {
        get
        {
            string? dir = System.IO.Path.GetDirectoryName(Source.RelativePath);
            if (string.IsNullOrEmpty(dir)) return Source.VolumeName;

            var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 3
                ? Source.VolumeName.TrimEnd('\\') + "\\" + dir
                : "…\\" + string.Join('\\', parts[^3..]);
        }
    }
    public string Dimensions => Source.Width > 0 ? $"{Source.Width} x {Source.Height}" : "?";
    public string SizeText => FormatBytes(Source.Size);

    public string MetadataText => Source.DateTaken is { } d
        ? $"{d:yyyy-MM-dd HH:mm}"
        : Source.HasExif ? "EXIF, no date" : "NO metadata";

    public bool HasMetadata => Source.DateTaken is not null;
    public string CameraText => Source.Camera ?? "";
    public string QualityText => Source.JpegQuality is { } q ? $"q~{q}" : "";

    private bool _isKeeper;
    public bool IsKeeper
    {
        get => _isKeeper;
        set { Set(ref _isKeeper, value); Raise(nameof(BorderBrush)); Raise(nameof(ActionText)); }
    }

    public Brush BorderBrush => _isKeeper
        ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
        : new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));

    public string ActionText => _isKeeper ? "KEEP" : "quarantine";

    private BitmapSource? _preview;
    public BitmapSource? Preview
    {
        get => _preview;
        set => Set(ref _preview, value);
    }

    private BitmapSource? _difference;
    /// <summary>Heatmap of where this file differs from the one being kept.</summary>
    public BitmapSource? Difference
    {
        get => _difference;
        set { Set(ref _difference, value); Raise(nameof(HasDifference)); }
    }

    public bool HasDifference => _difference is not null;

    private string _differenceVerdict = "";
    public string DifferenceVerdict
    {
        get => _differenceVerdict;
        set => Set(ref _differenceVerdict, value);
    }

    private string _differenceMeasurements = "";
    /// <summary>The numbers behind the verdict, for a reviewer who wants to check the wording.</summary>
    public string DifferenceMeasurements
    {
        get => _differenceMeasurements;
        set => Set(ref _differenceMeasurements, value);
    }

    private bool _differenceIsWarning;
    /// <summary>
    /// True when the shape of the difference argues this is a separate exposure rather than
    /// a copy — the case where confirming would discard a photograph.
    /// </summary>
    public bool DifferenceIsWarning
    {
        get => _differenceIsWarning;
        set { Set(ref _differenceIsWarning, value); Raise(nameof(DifferenceBrush)); }
    }

    public Brush DifferenceBrush => _differenceIsWarning
        ? new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x2E))
        : new SolidColorBrush(Color.FromRgb(0x7A, 0x9A, 0x7A));

    private string? _previewError;
    public string? PreviewError
    {
        get => _previewError;
        set => Set(ref _previewError, value);
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}

public enum ReviewState { Pending, Confirmed, Skipped }

public sealed class ReviewCluster : Observable
{
    public required int Index { get; init; }

    private string? _clusterKey;
    /// <summary>
    /// Identity that survives between sessions: derived from who is in the cluster, not from
    /// where it happens to sit in today's list.
    /// </summary>
    public string ClusterKey =>
        _clusterKey ??= MediaTool.Core.Storage.ReviewDecisions.KeyFor(Files.Select(f => f.Source.FileKey));
    public required GroupKind Kind { get; init; }
    public required List<ReviewFile> Files { get; init; }

    /// <summary>
    /// True when one of the copies carries metadata the currently chosen keeper lacks.
    /// These are surfaced first: they are the cases where accepting the default silently
    /// throws away the only record of when the photo was taken.
    /// </summary>
    public bool MetadataConflict
    {
        get
        {
            var keeper = Files.FirstOrDefault(f => f.IsKeeper);
            if (keeper is null) return false;
            return Files.Any(f => !f.IsKeeper &&
                (f.Source.DateTaken is not null && keeper.Source.DateTaken is null));
        }
    }

    /// <summary>
    /// How many copies are shown before the list is cut off.
    ///
    /// Clusters are not all small: a single image reused across copies of a website turned up
    /// here 370 times. Rendering a card per copy makes those unreviewable, and there is no
    /// decision in them anyway — the reviewer needs to see enough copies to judge the picture,
    /// not every path it occupies.
    /// </summary>
    public const int DisplayLimit = 8;

    private bool _showAll;
    public bool ShowAll
    {
        get => _showAll;
        set { Set(ref _showAll, value); Raise(nameof(VisibleFiles)); Raise(nameof(TruncationText)); }
    }

    public bool IsTruncated => Files.Count > DisplayLimit;

    public IReadOnlyList<ReviewFile> VisibleFiles =>
        _showAll || !IsTruncated ? Files : Files.Take(DisplayLimit).ToList();

    public string TruncationText => !IsTruncated
        ? ""
        : _showAll
            ? $"Showing all {Files.Count} copies  ·  press A to collapse"
            : $"Showing {DisplayLimit} of {Files.Count} copies  ·  press A to see the rest. " +
              $"Confirming keeps the one marked KEEP and quarantines the other {Files.Count - 1}.";

    public long ReclaimableBytes => Files.Where(f => !f.IsKeeper).Sum(f => f.Source.Size);
    public string ReclaimableText => ReviewFile.FormatBytes(ReclaimableBytes);
    public string Title => Files.FirstOrDefault(f => f.IsKeeper)?.Name ?? Files[0].Name;
    public string Subtitle => $"{Files.Count} copies";

    private ReviewState _state = ReviewState.Pending;
    public ReviewState State
    {
        get => _state;
        set { Set(ref _state, value); Raise(nameof(StateText)); Raise(nameof(StateBrush)); }
    }

    public string StateText => _state switch
    {
        ReviewState.Confirmed => "confirmed",
        ReviewState.Skipped => "skipped",
        _ => MetadataConflict ? "metadata conflict" : "",
    };

    public Brush StateBrush => _state switch
    {
        ReviewState.Confirmed => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
        ReviewState.Skipped => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        _ => MetadataConflict
            ? new SolidColorBrush(Color.FromRgb(0xE5, 0x8A, 0x2E))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };

    public void ChooseKeeper(ReviewFile file)
    {
        foreach (var f in Files) f.IsKeeper = ReferenceEquals(f, file);
        Raise(nameof(TruncationText));
        Raise(nameof(ReclaimableBytes));
        Raise(nameof(ReclaimableText));
        Raise(nameof(Title));
        Raise(nameof(StateText));
        Raise(nameof(StateBrush));
    }

    /// <summary>
    /// Turns the current choice into plan rows.
    ///
    /// Stamped ReviewedByHuman rather than NearDuplicate: the executor refuses to act on a
    /// near-duplicate on its own authority, and rightly so, but these have been looked at.
    /// The kind records who made the call, which is what decides how it gets verified.
    /// </summary>
    public IEnumerable<PlanRow> ToPlanRows()
    {
        var keeper = Files.First(f => f.IsKeeper);
        yield return new PlanRow
        {
            Group = Index,
            Kind = GroupKind.ReviewedByHuman,
            Action = PlannedAction.Keep,
            File = keeper.Source,
            Score = 0,
            Reason = "chosen in review",
        };

        foreach (var file in Files.Where(f => !f.IsKeeper))
            yield return new PlanRow
            {
                Group = Index,
                Kind = GroupKind.ReviewedByHuman,
                Action = PlannedAction.Quarantine,
                File = file.Source,
                Score = 0,
                Reason = "not chosen in review",
                KeptFileKey = keeper.Source.FileKey,
            };
    }
}

public sealed class ReviewQueue : Observable
{
    public ObservableCollection<ReviewCluster> Clusters { get; } = [];

    /// <summary>Groups every copy of which is the identical picture — no judgement to make.</summary>
    public int AutoAcceptedClusters { get; set; }
    public long AutoAcceptedBytes { get; set; }

    public int Confirmed => Clusters.Count(c => c.State == ReviewState.Confirmed);
    public long ConfirmedBytes => Clusters.Where(c => c.State == ReviewState.Confirmed)
                                          .Sum(c => c.ReclaimableBytes);

    public void RaiseTotals()
    {
        Raise(nameof(Confirmed));
        Raise(nameof(ConfirmedBytes));
    }
}
