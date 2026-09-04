using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaTool.Core.Actions;
using MediaTool.Core.Dedupe;
using MediaTool.Core.Imaging;
using MediaTool.Core.Storage;

namespace MediaTool.App;

public partial class MainWindow : Window
{
    private const int PreviewMaxSide = 480;

    private readonly ReviewQueue _queue = new();
    private CatalogDatabase? _db;

    /// <summary>Decoded previews, keyed by file. Cleared wholesale when it grows too large.</summary>
    private readonly Dictionary<long, BitmapSource> _previewCache = [];
    private const int PreviewCacheLimit = 400;

    public MainWindow()
    {
        InitializeComponent();
        ClusterList.ItemsSource = _queue.Clusters;
        Loaded += OnLoaded;
    }

    // ---- loading ---------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // With no catalog there is nothing to review, and until now no way to get one from
        // inside the app — so the first thing offered is the place to choose what to scan.
        if (!File.Exists(App.CatalogPath))
        {
            HeaderTitle.Text = "Nothing scanned yet";
            HeaderDetail.Text = "";

            if (!OpenScanSetup())
            {
                ShowEmpty($"No catalog at\n{App.CatalogPath}\n\nChoose Scan… to pick what to look at.");
                return;
            }
        }

        HeaderTitle.Text = "Building review queue…";
        HeaderDetail.Text = "clustering near-duplicates; this takes a moment on a large catalog";
        CardScroller.Visibility = Visibility.Collapsed;

        try
        {
            var built = await Task.Run(BuildQueue);
            foreach (var cluster in built.Clusters) _queue.Clusters.Add(cluster);
            _queue.AutoAcceptedClusters = built.AutoAcceptedClusters;
            _queue.AutoAcceptedBytes = built.AutoAcceptedBytes;
            _identicalRows = built.AutoAcceptedRows;
        }
        catch (Exception ex)
        {
            ShowEmpty($"Could not build the review queue.\n\n{ex.GetType().Name}: {ex.Message}");
            HeaderTitle.Text = "Failed to load";
            return;
        }

        if (_queue.Clusters.Count == 0)
        {
            // Arriving here from Explorer usually means the folder was never scanned, which
            // looks identical to "no duplicates" unless it is spelled out.
            ShowEmpty(App.ScanTarget is { Length: > 0 } opened
                ? $"Nothing to review in\n{opened}\n\n" +
                  "Either this folder has no duplicates, or it has not been scanned yet.\n\n" +
                  $"    winnow-cli scan \"{opened}\"\n    winnow-cli hash\n    winnow-cli images"
                : "Nothing needs a human decision.\n\n" +
                  "Every duplicate found is provably the same picture, so 'winnow-cli plan' " +
                  "can handle them without review.");
            HeaderTitle.Text = "Nothing to review";
            UpdateStatus();
            return;
        }

        HeaderTitle.Text = _queue.Clusters.Count == 1
            ? "1 cluster needs a decision"
            : $"{_queue.Clusters.Count:N0} clusters need a decision";
        string scopeNote = App.Scope.IsEmpty ? "" : $"   ·   {App.Scope}";

        // Say so when the catalog came from the folder an older build used. Silence here is
        // what made the app look empty once; a library that took hours to scan deserves a
        // line saying which file was opened.
        string catalogNote = MediaTool.Core.Storage.CatalogLocation.IsLegacy(App.CatalogPath)
            ? $"   ·   catalog: {App.CatalogPath}"
            : "";

        HeaderDetail.Text = (_queue.AutoAcceptedClusters > 0
            ? $"{_queue.AutoAcceptedClusters:N0} more are identical pictures and need no review"
            : "") + scopeNote + catalogNote;

        ExportButton.IsEnabled = true;
        ApplyButton.IsEnabled = true;
        WholeCatalogButton.Visibility = App.Scope.IsEmpty ? Visibility.Collapsed : Visibility.Visible;

        RestoreDecisions();

        int identical = _identicalRows.Count(r => r.Action == PlannedAction.Quarantine);
        if (identical > 0)
        {
            long bytes = _identicalRows.Where(r => r.Action == PlannedAction.Quarantine).Sum(r => r.File.Size);
            IdenticalButton.Content = $"Apply {identical:N0} identical ({ReviewFile.FormatBytes(bytes)})…";
            IdenticalButton.Visibility = Visibility.Visible;
        }
        CardScroller.Visibility = Visibility.Visible;
        EmptyMessage.Text = "";

        ClusterList.SelectedIndex = 0;
        ClusterList.Focus();
        UpdateStatus();
    }

    /// <summary>
    /// Opens the picker. Returns true when a scan actually ran, which is the caller's cue
    /// that the catalog underneath has changed.
    /// </summary>
    private bool OpenScanSetup()
    {
        var setup = new ScanSetupWindow();
        if (IsLoaded) setup.Owner = this;
        setup.ShowDialog();

        // Look at what was just chosen. Someone who picks a folder to scan is asking about
        // that folder; showing them the whole library instead buries it. The header states
        // the narrowing, and "Whole catalog" undoes it, so nothing looks lost.
        if (setup.Completed && setup.ScannedRoots.Count > 0)
        {
            App.Scope.Under.Clear();
            App.Scope.Under.AddRange(setup.ScannedRoots);

            App.Scope.Exclude.Clear();
            App.Scope.Exclude.AddRange(setup.ScannedExclusions);
        }
        return setup.Completed;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(App.Settings) { Owner = this };
        if (settings.ShowDialog() != true) return;

        // The catalog is opened once, when the queue is built, and held open for previews.
        // Pointing at a different file therefore means reopening rather than reloading, and
        // saying so beats appearing to ignore the change.
        if (settings.CatalogMoved)
            MessageBox.Show(
                """
                The catalog location is saved.

                Close and reopen Winnow for it to be used. Nothing was moved: if the new
                location has no catalog yet, the next scan creates one there.
                """,
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Widens the review back to everything the catalog knows.</summary>
    private async void OnShowWholeCatalog(object sender, RoutedEventArgs e)
    {
        App.Scope.Under.Clear();
        App.Scope.Exclude.Clear();
        await RebuildQueue();
    }

    private async void OnOpenScanSetup(object sender, RoutedEventArgs e)
    {
        if (!OpenScanSetup()) return;

        await RebuildQueue();
    }

    /// <summary>
    /// Rebuilt from scratch rather than patched: clusters, previews and the open connection
    /// all belong to the contents or the scope that has just changed.
    /// </summary>
    private async Task RebuildQueue()
    {
        _queue.Clusters.Clear();
        _previewCache.Clear();
        FileCards.ItemsSource = null;
        ClusterList.ItemsSource = _queue.Clusters;
        ComparisonPanel.Visibility = Visibility.Collapsed;
        _db?.Dispose();
        _db = null;

        await Task.Yield();
        OnLoaded(this, new RoutedEventArgs());
    }

    private sealed record BuiltQueue(
        List<ReviewCluster> Clusters,
        int AutoAcceptedClusters,
        long AutoAcceptedBytes,
        List<PlanRow> AutoAcceptedRows);

    /// <summary>
    /// Builds the queue on a background thread.
    ///
    /// Only clusters that mix different pictures — different resolutions, different
    /// compression — reach the reviewer. A cluster whose members all decode to the identical
    /// picture has no decision in it worth a person's attention, and there are tens of
    /// thousands of those.
    /// </summary>
    private BuiltQueue BuildQueue()
    {
        _db = CatalogDatabase.Open(App.CatalogPath);

        // A folder handed over by Explorer becomes the scope for the session: someone who
        // right-clicked one folder is asking about that folder, not the whole library.
        if (App.ScanTarget is { Length: > 0 } target && !App.Scope.Under.Contains(target))
            App.Scope.Under.Add(target);

        var builder = new PlanBuilder(_db, new PlanOptions
        {
            Kind = GroupKind.NearDuplicate,
            Scope = App.Scope,
        });
        var (rows, _) = builder.Build();

        var clusters = new List<ReviewCluster>();
        var autoAcceptedRows = new List<PlanRow>();
        int autoAccepted = 0;
        long autoAcceptedBytes = 0;
        int index = 0;

        foreach (var group in rows.GroupBy(r => r.Group))
        {
            var ordered = group.OrderBy(r => r.Action == PlannedAction.Keep ? 0 : 1)
                               .ThenByDescending(r => r.File.Pixels)
                               .ToList();

            bool allSamePicture = ordered.Select(r => r.File.PixelHash).Distinct().Count() == 1;
            if (allSamePicture)
            {
                autoAccepted++;
                autoAcceptedBytes += ordered.Skip(1).Sum(r => r.File.Size);

                // Keep the rows. These used to be counted and dropped, which left the app
                // announcing thousands of groups it then gave no way to act on - most of the
                // reclaimable space in the library, reachable only from the command line.
                // They are IdenticalPicture rather than NearDuplicate: every member decodes
                // to the same pixels, so apply can re-decode and prove it before moving one.
                foreach (var row in ordered)
                    autoAcceptedRows.Add(new PlanRow
                    {
                        Group = row.Group,
                        Kind = GroupKind.IdenticalPicture,
                        Action = row.Action,
                        File = row.File,
                        Score = row.Score,
                        Reason = row.Reason,
                        KeptFileKey = row.KeptFileKey,
                    });

                continue;
            }

            index++;
            var files = ordered.Select((r, i) => new ReviewFile
            {
                Source = r.File,
                Ordinal = i + 1,
                IsKeeper = r.Action == PlannedAction.Keep,
            }).ToList();

            clusters.Add(new ReviewCluster { Index = index, Kind = GroupKind.NearDuplicate, Files = files });
        }

        // Metadata conflicts first, then by how much space is at stake: the decisions that
        // can destroy information come before the ones that only save disk.
        clusters = clusters
            .OrderByDescending(c => c.MetadataConflict)
            .ThenByDescending(c => c.ReclaimableBytes)
            .ToList();

        return new BuiltQueue(clusters, autoAccepted, autoAcceptedBytes, autoAcceptedRows);
    }

    // ---- selection and previews ------------------------------------------

    /// <summary>
    /// Groups whose members all decode to the same picture. No decision in them, so they
    /// never reach the review list - but they hold most of the reclaimable space, so they
    /// get their own button rather than being counted and forgotten.
    /// </summary>
    private List<PlanRow> _identicalRows = [];

    /// <summary>Decisions already made, in this session or an earlier one.</summary>
    private ReviewDecisions? _decisions;

    private ReviewCluster? Current => ClusterList.SelectedItem as ReviewCluster;

    private void OnClusterSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ShowCurrentCluster(resetScroll: true);
    }

    private void ShowCurrentCluster(bool resetScroll)
    {
        var cluster = Current;
        FileCards.ItemsSource = cluster?.VisibleFiles;

        TruncationBanner.Visibility = cluster is { IsTruncated: true } ? Visibility.Visible : Visibility.Collapsed;
        TruncationText.Text = cluster?.TruncationText ?? "";

        if (cluster is null) return;
        if (resetScroll) CardScroller.ScrollToTop();

        BuildComparison(cluster);

        // Only what is on screen gets decoded. Previewing all 370 copies of a cluster would
        // cost minutes of disk for images nobody asked to see.
        foreach (var file in cluster.VisibleFiles) _ = LoadPreviewAsync(file);

        _ = LoadDifferencesAsync(cluster);
    }

    /// <summary>
    /// Computes each candidate's difference from the copy being kept.
    ///
    /// Runs after the previews so the images appear immediately and the analysis fills in
    /// behind them; on a spinning disk the two extra decodes per card would otherwise hold
    /// up the whole pane.
    /// </summary>
    private async Task LoadDifferencesAsync(ReviewCluster cluster)
    {
        var keeper = cluster.Files.FirstOrDefault(f => f.IsKeeper);
        if (keeper is null) return;

        string keeperPath = keeper.FullPath;
        var others = cluster.VisibleFiles.Where(f => !f.IsKeeper).ToList();
        if (others.Count == 0) return;

        try
        {
            var computed = await Task.Run(() =>
            {
                // The reference is decoded once and reused for every comparison in the
                // cluster, which halves the work on a group of six.
                var reference = PreviewDecoder.Decode(keeperPath, DifferenceMap.WorkingSize);
                var results = new List<(ReviewFile File, DifferenceResult? Result)>();

                foreach (var other in others)
                {
                    try
                    {
                        var candidate = PreviewDecoder.Decode(other.FullPath, DifferenceMap.WorkingSize);
                        results.Add((other, DifferenceMap.Compare(reference, candidate)));
                    }
                    catch (Exception)
                    {
                        results.Add((other, null));   // unreadable: the strip stays hidden
                    }
                }

                return results;
            });

            foreach (var (file, result) in computed)
            {
                if (result is null) continue;

                var image = BitmapSource.Create(result.Width, result.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, result.Bgra, result.Width * 4);
                image.Freeze();

                file.Difference = image;
                file.DifferenceVerdict = result.Verdict;
                file.DifferenceMeasurements = result.Measurements;
                file.DifferenceIsWarning = result.SuggestsSeparateExposure;
            }
        }
        catch (Exception)
        {
            // The heatmap is an aid, not a requirement; the cluster stays reviewable without it.
        }
    }

    /// <summary>
    /// Lays the candidates out as a table, one row per attribute, the best cell in each row
    /// picked out. Reading down a column describes one file; reading across a row answers
    /// which file wins on that count, which is the question actually being asked.
    /// </summary>
    private void BuildComparison(ReviewCluster? cluster)
    {
        ComparisonGrid.Children.Clear();
        ComparisonGrid.ColumnDefinitions.Clear();
        ComparisonGrid.RowDefinitions.Clear();

        if (cluster is null || cluster.VisibleFiles.Count < 2)
        {
            ComparisonPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ComparisonPanel.Visibility = Visibility.Visible;
        var files = cluster.VisibleFiles;

        ComparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        foreach (var _ in files)
            ComparisonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = new (string Label, Func<ReviewFile, string> Value, Func<ReviewFile, double> Rank)[]
        {
            ("resolution", f => f.Dimensions, f => f.Source.Pixels),
            ("capture date", f => f.MetadataText, f => f.Source.DateTaken is null ? 0 : 1),
            ("camera", f => f.CameraText.Length == 0 ? "-" : f.CameraText, f => f.CameraText.Length == 0 ? 0 : 1),
            ("quality", f => f.QualityText.Length == 0 ? "-" : f.QualityText, f => f.Source.JpegQuality ?? 0),
            ("format", f => Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant(), f => (double)f.Source.Tier),
            ("size", f => f.SizeText, _ => 0),
        };

        AddHeaderRow(files);

        for (int r = 0; r < rows.Length; r++)
        {
            var (label, value, rank) = rows[r];
            ComparisonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = r + 1;

            AddCell(label, 0, row, dim: true);

            // A row is only worth marking when the files actually disagree on it.
            double best = files.Max(rank);
            bool meaningful = best > 0 && files.Any(f => rank(f) < best);

            for (int c = 0; c < files.Count; c++)
            {
                bool wins = meaningful && Math.Abs(rank(files[c]) - best) < 0.0001;
                AddCell(value(files[c]), c + 1, row, dim: false, highlight: wins);
            }
        }
    }

    private void AddHeaderRow(IReadOnlyList<ReviewFile> files)
    {
        ComparisonGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell("", 0, 0, dim: true);

        for (int c = 0; c < files.Count; c++)
        {
            var block = new TextBlock
            {
                Text = files[c].IsKeeper ? $"{files[c].Ordinal}  KEEP" : $"{files[c].Ordinal}",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(6, 2, 6, 6),
                Foreground = files[c].IsKeeper
                    ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            };

            Grid.SetColumn(block, c + 1);
            Grid.SetRow(block, 0);
            ComparisonGrid.Children.Add(block);
        }
    }

    private void AddCell(string text, int column, int row, bool dim, bool highlight = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(6, 2, 6, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = highlight ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = highlight
                ? new SolidColorBrush(Color.FromRgb(0xF7, 0xB7, 0x31))
                : dim
                    ? new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A))
                    : new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
        };

        Grid.SetColumn(block, column);
        Grid.SetRow(block, row);
        ComparisonGrid.Children.Add(block);
    }

    private async Task LoadPreviewAsync(ReviewFile file)
    {
        if (_previewCache.TryGetValue(file.Source.FileKey, out var cached))
        {
            file.Preview = cached;
            return;
        }

        string path = file.FullPath;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var preview = PreviewDecoder.Decode(path, PreviewMaxSide);
                var source = BitmapSource.Create(preview.Width, preview.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, preview.Bgra, preview.Stride);
                // Frozen so it can cross back to the UI thread without marshalling.
                source.Freeze();
                return source;
            });

            if (_previewCache.Count > PreviewCacheLimit) _previewCache.Clear();
            _previewCache[file.Source.FileKey] = bitmap;
            file.Preview = bitmap;
            file.PreviewError = null;
        }
        catch (Exception ex)
        {
            // A missing codec or a file moved since the scan. Say so on the card rather than
            // showing an empty box the reviewer cannot interpret.
            file.PreviewError = ex is WicException ? "cannot decode\n(codec missing?)" : ex.GetType().Name;
        }
    }

    // ---- interaction -----------------------------------------------------

    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReviewFile file }) ChooseKeeper(file);
    }

    private void ChooseKeeper(ReviewFile file)
    {
        Current?.ChooseKeeper(file);
        TruncationText.Text = Current?.TruncationText ?? "";
        UpdateStatus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var cluster = Current;
        if (cluster is null) { base.OnPreviewKeyDown(e); return; }

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            int ordinal = e.Key - Key.D1 + 1;
            var pick = cluster.Files.FirstOrDefault(f => f.Ordinal == ordinal);
            if (pick is not null) ChooseKeeper(pick);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                cluster.State = ReviewState.Confirmed;
                Remember(cluster);
                MoveNext();
                e.Handled = true;
                break;

            case Key.S:
                cluster.State = ReviewState.Skipped;
                Remember(cluster);
                MoveNext();
                e.Handled = true;
                break;

            case Key.A when cluster.IsTruncated:
                cluster.ShowAll = !cluster.ShowAll;
                ShowCurrentCluster(resetScroll: false);
                e.Handled = true;
                break;

            case Key.O:
                OpenContainingFolder(cluster.Files.First(f => f.IsKeeper));
                e.Handled = true;
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    private void MoveNext()
    {
        _queue.RaiseTotals();
        UpdateStatus();
        if (ClusterList.SelectedIndex < _queue.Clusters.Count - 1) ClusterList.SelectedIndex++;
        ClusterList.ScrollIntoView(ClusterList.SelectedItem);
    }

    private static void OpenContainingFolder(ReviewFile file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer refusing to open is not worth interrupting a review over.
        }
    }

    /// <summary>
    /// Writes a decision down as it is made, not at the end.
    ///
    /// A review is hours of work and the app can be closed at any moment, so each keystroke
    /// is the commit point. A catalog that cannot be written to must not stop the review:
    /// the decision still stands for this session, and the reviewer is told once.
    /// </summary>
    private void Remember(ReviewCluster cluster)
    {
        if (_decisions is null) return;

        try
        {
            var keeper = cluster.Files.FirstOrDefault(f => f.IsKeeper);
            if (keeper is null) return;

            _decisions.Save(cluster.ClusterKey, keeper.Source.FileKey,
                cluster.State == ReviewState.Confirmed
                    ? ReviewDecisionState.Confirmed
                    : ReviewDecisionState.Skipped);
        }
        catch (Exception ex)
        {
            _decisions = null;
            MessageBox.Show(
                $"""
                Decisions cannot be saved to the catalog, so they will be lost when this
                window closes.

                {ex.GetType().Name}: {ex.Message}

                Reviewing still works. Apply what you decide before closing.
                """,
                "Winnow", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Puts back what was decided before, including which copy was chosen to keep.
    ///
    /// A decision is only restored onto a cluster holding exactly the same files, and only
    /// if the copy that was chosen is still one of them. Anything else has changed since the
    /// judgement was made, so the judgement no longer applies to it.
    /// </summary>
    private void RestoreDecisions()
    {
        if (_db is null) return;

        Dictionary<string, ReviewDecision> saved;
        try
        {
            _decisions = new ReviewDecisions(_db);
            saved = _decisions.LoadAll();
        }
        catch (Exception)
        {
            _decisions = null;
            return;
        }

        if (saved.Count == 0) return;

        int restored = 0;
        foreach (var cluster in _queue.Clusters)
        {
            if (!saved.TryGetValue(cluster.ClusterKey, out var decision)) continue;

            var keeper = cluster.Files.FirstOrDefault(f => f.Source.FileKey == decision.KeeperFileKey);
            if (keeper is null) continue;

            foreach (var file in cluster.Files) file.IsKeeper = false;
            keeper.IsKeeper = true;

            cluster.State = decision.State == ReviewDecisionState.Confirmed
                ? ReviewState.Confirmed
                : ReviewState.Skipped;
            restored++;
        }

        if (restored > 0)
        {
            _queue.RaiseTotals();
            StatusText.Text = $"restored {restored:N0} earlier decisions";
        }
    }

    private void UpdateStatus()
    {
        int confirmed = _queue.Clusters.Count(c => c.State == ReviewState.Confirmed);
        long bytes = _queue.Clusters.Where(c => c.State == ReviewState.Confirmed)
                                    .Sum(c => c.ReclaimableBytes);
        StatusText.Text = $"{confirmed:N0} / {_queue.Clusters.Count:N0} confirmed   ·   " +
                          $"{ReviewFile.FormatBytes(bytes)} marked";
    }

    private void ShowEmpty(string message)
    {
        EmptyMessage.Text = message;
        CardScroller.Visibility = Visibility.Collapsed;
    }

    // ---- output ----------------------------------------------------------

    private List<PlanRow> ConfirmedRows() =>
        _queue.Clusters.Where(c => c.State == ReviewState.Confirmed)
                       .SelectMany(c => c.ToPlanRows())
                       .ToList();

    private void OnExportPlan(object sender, RoutedEventArgs e)
    {
        var rows = ConfirmedRows();
        if (rows.Count == 0)
        {
            MessageBox.Show("Nothing has been confirmed yet.", "Winnow");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "review-plan.csv",
            Filter = "CSV|*.csv",
        };
        if (dialog.ShowDialog() != true) return;

        PlanCsv.Write(dialog.FileName, rows);
        MessageBox.Show($"Wrote {rows.Count:N0} rows.\n\nApply it with:\n" +
                        $"mediatool apply --plan \"{dialog.FileName}\" --quarantine <folder>",
                        "Winnow");
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        var rows = ConfirmedRows();
        if (rows.Count(r => r.Action == PlannedAction.Quarantine) == 0)
        {
            // Saying only "nothing confirmed" left a button that looked broken. The queue
            // does nothing until a decision is recorded, so say how one is recorded.
            MessageBox.Show(
                """
                No cluster has been confirmed yet, so there is nothing to move.

                In the panel on the right: press 1-9 to pick which copy to keep if the
                suggested one is wrong, then press Enter to confirm that cluster and move
                to the next. The counter at the bottom right tracks how many are ready.

                The groups that need no decision have their own button.
                """,
                "Nothing confirmed yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Apply(rows, "reviewed by you");
    }

    /// <summary>
    /// The groups where every copy decodes to the same picture.
    ///
    /// Deliberately a button of its own. They hold most of the reclaimable space and carry
    /// no judgement call, but folding them into the reviewed ones would mean a click meant
    /// for two checked decisions quietly moved sixteen thousand files.
    /// </summary>
    private void OnApplyIdentical(object sender, RoutedEventArgs e)
    {
        if (_identicalRows.Count(r => r.Action == PlannedAction.Quarantine) == 0)
        {
            MessageBox.Show("There are no identical-picture groups in this scope.", "Winnow");
            return;
        }

        Apply(_identicalRows, "identical pictures, each re-decoded and compared before it moves");
    }

    private void Apply(List<PlanRow> rows, string what)
    {
        // Offered rather than assumed: the folder is pre-filled from settings, but every
        // apply still shows where the files are about to go.
        var folder = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a quarantine folder",
            InitialDirectory = App.Settings.QuarantineFolder is { Length: > 0 } saved
                               && System.IO.Directory.Exists(saved) ? saved : "",
        };
        if (folder.ShowDialog() != true) return;

        // Dry run first, always. It re-verifies every pair on disk and reports what would
        // happen, so the confirmation prompt is based on checked facts rather than the plan's
        // own claims about itself.
        var executor = new PlanExecutor(_db!);
        var dryRun = executor.Execute(rows, folder.FolderName, dryRun: true, null, CancellationToken.None);

        string warning = dryRun.VerificationFailed > 0
            ? $"\n\n{dryRun.VerificationFailed:N0} files FAILED verification and will not be touched."
            : "";

        var answer = MessageBox.Show(
            $"Move {dryRun.Moved:N0} files -- {what} -- into\n{folder.FolderName}\n\n" +
            $"Frees {ReviewFile.FormatBytes(dryRun.BytesFreed)}.{warning}\n\n" +
            "Files are MOVED, not deleted, and can be restored with 'mediatool undo'.",
            "Apply", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        var result = executor.Execute(rows, folder.FolderName, dryRun: false, null, CancellationToken.None);

        MessageBox.Show(
            $"Batch {result.BatchId}\n\n" +
            $"Moved {result.Moved:N0} files, freed {ReviewFile.FormatBytes(result.BytesFreed)}.\n" +
            (result.VerificationFailed > 0 ? $"{result.VerificationFailed:N0} refused verification.\n" : "") +
            (result.Errors > 0 ? $"{result.Errors:N0} errors.\n" : "") +
            $"\nTo reverse:\nmediatool undo --batch {result.BatchId}",
            "Winnow");
    }

    protected override void OnClosed(EventArgs e)
    {
        _db?.Dispose();
        base.OnClosed(e);
    }
}
