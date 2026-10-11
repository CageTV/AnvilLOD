using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AnvilLOD.Plugins;
using Microsoft.Win32;

namespace AnvilLOD.App;

// The window layout: category buttons on the left, results above and the options of the chosen category below,
// printing the results to a file, expanding the results over the window, and the size estimate.
public partial class MainWindow
{
    private Dictionary<string, (FrameworkElement Panel, string Title)>? _categories;
    private bool _expanded;

    private void InitLayout()
    {
        _categories = new()
        {
            ["Output"] = (CatOutput, "Output and worlds"),
            ["Rules"] = (CatRules, "LOD rules"),
            ["Objects"] = (CatObjects, "Objects and terrain"),
            ["Trees"] = (CatTrees, "Trees"),
            ["Grass"] = (CatGrass, "Grass and size"),
            ["Dynamic"] = (CatDynamic, "Seasons, water and dynamic LOD"),
            ["Finder"] = (CatFinder, "Missing LOD finder (mod author tool)"),
            ["Maker"] = (CatMaker, "LOD Mesh Maker (mod author tool)"),
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _expanded)
            {
                SetExpanded(false);
                e.Handled = true;
            }
        };
        WireEstimate();
        UpdateEstimate();
    }

    // ---------- categories ----------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_categories is null || sender is not RadioButton { Tag: string tag }) return;
        ShowCategory(tag);
    }

    private void ShowCategory(string tag)
    {
        if (_categories is null || !_categories.TryGetValue(tag, out var target)) return;
        foreach (var (key, (panel, _)) in _categories)
            panel.Visibility = key == tag ? Visibility.Visible : Visibility.Collapsed;
        OptionsTitle.Text = target.Title;
        SettingsScroll.ScrollToTop();
        // a short fade-in so the switch isn't abrupt
        target.Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        if (tag == "Finder") AuthorTab_GotFocus(this, new RoutedEventArgs());
        else if (tag == "Maker") MakerTab_GotFocus(this, new RoutedEventArgs());
    }

    // ---------- results box: expand over the window ----------

    private void ExpandResults_Click(object sender, RoutedEventArgs e) => SetExpanded(!_expanded);

    /// <summary>
    /// Expands the results box until it fills the window (the options box and the category column slide away), or brings
    /// them back. Both directions are animated.
    /// </summary>
    private void SetExpanded(bool expand)
    {
        if (expand == _expanded) return;
        _expanded = expand;
        ExpandButton.Content = expand ? "⤡  Restore" : "⤢  Expand";
        var span = TimeSpan.FromMilliseconds(340);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        if (!expand)
        {
            NavBorder.Visibility = Visibility.Visible;
            OptionsBox.Visibility = Visibility.Visible;
        }
        double navFrom = expand ? NavColumn.ActualWidth : 0, navTo = expand ? 0 : 224;
        double optFrom = expand ? 1.35 : 0, optTo = expand ? 0 : 1.35;

        var nav = new GridLengthAnimation { From = new GridLength(navFrom), To = new GridLength(navTo), EasingFunction = ease, Duration = span };
        var opt = new GridLengthAnimation { From = new GridLength(optFrom, GridUnitType.Star), To = new GridLength(optTo, GridUnitType.Star), EasingFunction = ease, Duration = span };
        opt.Completed += (_, _) =>
        {
            NavColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            OptionsRow.BeginAnimation(RowDefinition.HeightProperty, null);
            NavColumn.Width = new GridLength(navTo);
            OptionsRow.Height = new GridLength(optTo, GridUnitType.Star);
            if (expand)
            {
                NavBorder.Visibility = Visibility.Collapsed;
                OptionsBox.Visibility = Visibility.Collapsed;
            }
        };
        OptionsBox.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 1 : 0, expand ? 0 : 1, span) { EasingFunction = ease });
        NavBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(expand ? 1 : 0, expand ? 0 : 1, span) { EasingFunction = ease });
        NavColumn.BeginAnimation(ColumnDefinition.WidthProperty, nav);
        OptionsRow.BeginAnimation(RowDefinition.HeightProperty, opt);
    }

    // ---------- results box: print to file ----------

    private void PrintResults_Click(object sender, RoutedEventArgs e)
    {
        var tab = (ResultsTabs.SelectedItem as TabItem)?.Header as string ?? "Results";
        string body;
        string ext;
        switch (tab)
        {
            case "Blocks":
                ext = "csv";
                body = Csv(["File", "Level", "X", "Y", "Refs", "Status"],
                    (BlockGrid.ItemsSource as IEnumerable<BlockRow> ?? []).Select(r => new[] { r.File, r.Level.ToString(CultureInfo.InvariantCulture), r.X.ToString(CultureInfo.InvariantCulture), r.Y.ToString(CultureInfo.InvariantCulture), r.Refs.ToString(CultureInfo.InvariantCulture), r.Status }));
                break;
            case "Missing meshes":
                ext = "csv";
                body = Csv(["Mesh path", "Problem"], (MissingGrid.ItemsSource as IEnumerable<MissingRow> ?? []).Select(r => new[] { r.Path, r.Problem }));
                break;
            case "Log":
                ext = "txt";
                body = LogBox.Text;
                break;
            default:
                ext = "txt";
                body = OverviewAsText();
                break;
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            StatusText.Text = $"Nothing to print in {tab} yet";
            return;
        }
        var d = new SaveFileDialog
        {
            Title = $"Save {tab}",
            Filter = ext == "csv" ? "CSV (*.csv)|*.csv" : "Text (*.txt)|*.txt",
            FileName = $"AnvilLOD {tab.ToLowerInvariant()} {DateTime.Now:yyyy-MM-dd HHmm}.{ext}",
        };
        if (_settings.OutputFolder is { } o && Directory.Exists(o)) d.InitialDirectory = o;
        if (d.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(d.FileName, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            StatusText.Text = $"{tab} saved: {d.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Could not save", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string[] header, IEnumerable<string[]> rows)
    {
        static string Q(string s) => s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', header.Select(Q)));
        int n = 0;
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(',', row.Select(Q)));
            n++;
        }
        return n == 0 ? "" : sb.ToString();
    }

    private string OverviewAsText()
    {
        if (_lastScan is null) return "";
        var sb = new StringBuilder();
        sb.AppendLine($"AnvilLOD {VersionText.Text}  -  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var w in WarningList.Items) sb.AppendLine("WARNING: " + w);
        if (WarningList.Items.Count > 0) sb.AppendLine();
        sb.AppendLine($"{TilePluginsLabel.Text}: {TilePlugins.Text}");
        sb.AppendLine($"References scanned: {TileVisited.Text}");
        sb.AppendLine($"LOD references: {TileLodRefs.Text}");
        sb.AppendLine($"LOD blocks: {TileBlocks.Text}");
        sb.AppendLine($"{TileRebuildLabel.Text}: {TileRebuild.Text}");
        sb.AppendLine($"Missing LOD meshes: {TileMissing.Text}");
        sb.AppendLine($"Total time: {TileTime.Text}");
        sb.AppendLine();
        sb.AppendLine("Blocks per level");
        sb.AppendLine("Worldspace\tLOD 4\tLOD 8\tLOD 16\tLOD 32");
        foreach (var r in LevelGrid.ItemsSource as IEnumerable<LevelRow> ?? [])
            sb.AppendLine($"{r.Worldspace}\t{r.Lod4}\t{r.Lod8}\t{r.Lod16}\t{r.Lod32}");
        sb.AppendLine();
        sb.AppendLine("Timing");
        sb.AppendLine(TimingText.Text);
        return sb.ToString();
    }

    // ---------- size estimate ----------

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var inner in FindAll<T>(child)) yield return inner;
        }
    }

    private void WireEstimate()
    {
        foreach (var box in FindAll<CheckBox>(SettingsPanel))
        {
            box.Checked += (_, _) => UpdateEstimate();
            box.Unchecked += (_, _) => UpdateEstimate();
        }
        foreach (var combo in FindAll<ComboBox>(SettingsPanel))
            combo.SelectionChanged += (_, _) => UpdateEstimate();
    }

    private SizeCalibration? LoadCalibration()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastRunSizes)) return null;
        try { return JsonSerializer.Deserialize<SizeCalibration>(_settings.LastRunSizes); }
        catch (JsonException) { return null; }
    }

    private (int Percent, bool GrassOn, bool Tree3D, bool Lod8, bool Seasons) CurrentChoices() =>
        (GrassDensityChoices[Math.Clamp(GrassDensityBox.SelectedIndex, 0, GrassDensityChoices.Length - 1)],
         GrassLodCheck.IsChecked == true, Tree3DCheck.IsChecked == true, Tree3DLod8Check.IsChecked == true, SeasonsCheck.IsChecked == true);

    /// <summary>Shows the expected size of the next generation, scaled from the last one on this list (preset).</summary>
    private void UpdateEstimate()
    {
        if (EstimateText is null || EstimateDetail is null) return;
        var calibration = LoadCalibration();
        if (calibration is null)
        {
            EstimateText.Text = "Size estimate: generate once and it is worked out for your list.";
            EstimateDetail.Text = "Generate once and AnvilLOD can estimate the size of your next generation from your own list. "
                + "As a guide for one big worldspace without seasons: grass at 4% is about 0.7 GB, 15% about 1.7 GB, 40% about 3.9 GB and 100% about 11 GB. "
                + "Seasons add a full copy of the changed blocks for each season (summer is the biggest, its grass cache is much denser).";
            return;
        }
        var c = CurrentChoices();
        var est = SizeEstimator.Estimate(calibration, c.Percent, c.GrassOn, c.Tree3D, c.Lod8, c.Seasons);
        if (est is null) return;
        string seasons = est.Seasons is null ? " + seasonal copies (not measured yet: generate once with Seasons on)" : est.Seasons > 0 ? $" + seasons {SizeEstimator.Format(est.Seasons.Value)}" : "";
        long total = est.Base + (est.Seasons ?? 0);
        EstimateText.Text = $"Estimated size: about {SizeEstimator.Format(total)}{(est.Seasons is null ? " (without seasons)" : "")}";
        EstimateDetail.Text = $"About {SizeEstimator.Format(est.Base)} of base object LOD{seasons}, scaled from your last generation "
            + $"({SizeEstimator.Format(calibration.BaseTotal + calibration.SeasonTotal)}, grass {(calibration.GrassOn ? calibration.GrassPercent + "%" : "off")}). "
            + "An estimate: the real size depends on your list. " + est.Note;
    }

    /// <summary>After a generation: remember what it was made of, so the next one can be estimated.</summary>
    private void RecordSizeCalibration(ScanSummary summary, string? outputFolder)
    {
        if (summary.Generation is null || string.IsNullOrWhiteSpace(outputFolder)) return;
        var c = CurrentChoices();
        var calibration = SizeEstimator.Calibrate(summary.Generation, outputFolder, c.Percent, c.GrassOn, c.Tree3D, c.Lod8);
        if (calibration is null) return;
        _settings.LastRunSizes = JsonSerializer.Serialize(calibration);
        _settings.StoreActivePreset();
        _settings.Save();
        UpdateEstimate();
    }
}
