using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;
using AnvilLOD.Plugins.Mo2;
using Microsoft.Win32;

namespace AnvilLOD.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _elapsed = new();

    private CancellationTokenSource? _cts;
    private ScanSummary? _lastScan;
    private TimeSpan _lastTotal;
    private List<BlockRow> _allBlocks = [];

    public MainWindow()
    {
        StartupLog.Write("MainWindow: InitializeComponent");
        InitializeComponent();
        Loaded += (_, _) => StartupLog.Write("MainWindow loaded (UI is up)");
        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        _clock.Tick += (_, _) => StatusText.Text = $"Running… {_elapsed.Elapsed:mm\\:ss}";
        ApplySettings();
        AuthorWarningText.Text = AnvilLOD.Plugins.Authoring.ModAuthorTool.Warning;
        if (StartupLog.IsUnderMo2()) Title = "AnvilLOD  (running under MO2)";
    }

    // ---------- settings <-> controls ----------

    private void ApplySettings()
    {
        DynDolodBox.Text = _settings.DynDolodFolder ?? "";
        PresetBox.SelectedIndex = _settings.Preset switch { "Low" => 0, "Medium" => 1, _ => 2 };
        Mo2Box.Text = _settings.Mo2Instance ?? "";
        LoadProfiles(_settings.Mo2Profile);
        if (_settings.UseMo2) Mo2Radio.IsChecked = true; else DataRadio.IsChecked = true;
        DataBox.Text = _settings.DataFolder ?? "";
        PluginsBox.Text = _settings.PluginsTxt ?? "";
        OutputBox.Text = _settings.OutputFolder ?? "";
        WorldspaceBox.Text = _settings.Worldspaces;
        AllWorldspacesCheck.IsChecked = _settings.AllWorldspaces;
        WorldspaceBox.IsEnabled = !_settings.AllWorldspaces;
        Lod4Check.IsChecked = _settings.Lod4;
        Lod8Check.IsChecked = _settings.Lod8;
        Lod16Check.IsChecked = _settings.Lod16;
        Lod32Check.IsChecked = _settings.Lod32;
        RemoveBuriedCheck.IsChecked = _settings.RemoveBuried;
        TreeLodCheck.IsChecked = _settings.TreeLod;
        GrassLodCheck.IsChecked = _settings.GrassLod;
        TreeBrightnessBox.SelectedIndex = _settings.TreeBrightness switch { <= 82 => 0, <= 95 => 1, >= 105 => 3, _ => 2 };
        DynamicLodCheck.IsChecked = _settings.DynamicLod;
        GrassDensityBox.SelectedIndex = _settings.GrassDensity switch { <= 5 => 0, >= 12 => 2, _ => 1 };
        IncludeDisabledCheck.IsChecked = _settings.IncludeDisabled;
        IncludeEnableParentCheck.IsChecked = _settings.IncludeEnableParented;
    }

    private void CaptureSettings()
    {
        _settings.UseMo2 = Mo2Radio.IsChecked == true;
        _settings.Mo2Instance = Blank(Mo2Box.Text);
        _settings.Mo2Profile = ProfileBox.SelectedItem as string;
        _settings.DataFolder = Blank(DataBox.Text);
        _settings.PluginsTxt = Blank(PluginsBox.Text);
        _settings.OutputFolder = Blank(OutputBox.Text);
        _settings.Worldspaces = WorldspaceBox.Text.Trim();
        _settings.AllWorldspaces = AllWorldspacesCheck.IsChecked == true;
        _settings.Lod4 = Lod4Check.IsChecked == true;
        _settings.Lod8 = Lod8Check.IsChecked == true;
        _settings.Lod16 = Lod16Check.IsChecked == true;
        _settings.Lod32 = Lod32Check.IsChecked == true;
        _settings.DynDolodFolder = Blank(DynDolodBox.Text);
        _settings.Preset = PresetBox.SelectedIndex switch { 0 => "Low", 1 => "Medium", _ => "High" };
        _settings.RemoveBuried = RemoveBuriedCheck.IsChecked == true;
        _settings.TreeLod = TreeLodCheck.IsChecked == true;
        _settings.GrassLod = GrassLodCheck.IsChecked == true;
        _settings.TreeBrightness = TreeBrightnessBox.SelectedIndex switch { 0 => 80, 1 => 90, 3 => 110, _ => 100 };
        _settings.DynamicLod = DynamicLodCheck.IsChecked == true;
        _settings.GrassDensity = GrassDensityBox.SelectedIndex switch { 0 => 4, 2 => 15, _ => 8 };
        _settings.IncludeDisabled = IncludeDisabledCheck.IsChecked == true;
        _settings.IncludeEnableParented = IncludeEnableParentCheck.IsChecked == true;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _cts?.Cancel();
        CaptureSettings();
        _settings.Save();
    }

    // ---------- game source ----------

    private void SourceMode_Changed(object sender, RoutedEventArgs e)
    {
        if (Mo2Panel is null || DataPanel is null) return;
        var mo2 = Mo2Radio.IsChecked == true;
        Mo2Panel.Visibility = mo2 ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = mo2 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseMo2_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select the MO2 instance folder (contains ModOrganizer.ini)" };
        if (Directory.Exists(Mo2Box.Text)) d.InitialDirectory = Mo2Box.Text;
        if (d.ShowDialog(this) != true) return;
        Mo2Box.Text = d.FolderName;
        LoadProfiles(null);
    }

    private void Mo2Box_LostFocus(object sender, RoutedEventArgs e) => LoadProfiles(ProfileBox.SelectedItem as string);

    /// <summary>Reads the instance's profiles into the dropdown and selects the wanted (or MO2's selected) one.</summary>
    private void LoadProfiles(string? wanted)
    {
        ProfileBox.ItemsSource = null;
        var folder = Blank(Mo2Box.Text);
        if (folder is null)
        {
            Mo2InfoText.Text = "Reads your mods directly. No need to launch AnvilLOD from MO2.";
            return;
        }
        try
        {
            var inst = Mo2Instance.Open(folder);
            var profiles = inst.ListProfiles();
            ProfileBox.ItemsSource = profiles;
            ProfileBox.SelectedItem =
                profiles.FirstOrDefault(p => p.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault(p => p.Equals(inst.SelectedProfile, StringComparison.OrdinalIgnoreCase))
                ?? profiles.FirstOrDefault();
            Mo2InfoText.Text = $"{inst.GameName ?? "Game"} at {inst.GamePath}";
            if (string.IsNullOrWhiteSpace(DynDolodBox.Text))
            {
                var guess = Path.Combine(inst.InstanceFolder, "tools", "DynDOLOD");
                if (File.Exists(Path.Combine(guess, "DynDOLODx64.exe"))) DynDolodBox.Text = guess;
            }
        }
        catch (Exception ex)
        {
            Mo2InfoText.Text = ex.Message;
        }
    }

    // ---------- browse buttons ----------

    private void BrowseData_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select the Skyrim Data folder" };
        if (Directory.Exists(DataBox.Text)) d.InitialDirectory = DataBox.Text;
        if (d.ShowDialog(this) == true) DataBox.Text = d.FolderName;
    }

    private void BrowsePlugins_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "Select plugins.txt",
            Filter = "plugins.txt|plugins.txt|Text files (*.txt)|*.txt|All files|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition"),
        };
        if (d.ShowDialog(this) == true) PluginsBox.Text = d.FileName;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select the output folder for generated LOD" };
        if (Directory.Exists(OutputBox.Text)) d.InitialDirectory = OutputBox.Text;
        if (d.ShowDialog(this) == true) OutputBox.Text = d.FolderName;
    }

    private void BrowseDynDolod_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select your DynDOLOD folder (the one with DynDOLODx64.exe)" };
        if (Directory.Exists(DynDolodBox.Text)) d.InitialDirectory = DynDolodBox.Text;
        if (d.ShowDialog(this) == true) DynDolodBox.Text = d.FolderName;
    }

    private void AllWorldspaces_Changed(object sender, RoutedEventArgs e)
    {
        if (WorldspaceBox is not null) WorldspaceBox.IsEnabled = AllWorldspacesCheck.IsChecked != true;
    }

    // ---------- scan ----------

    private void Scan_Click(object sender, RoutedEventArgs e) => _ = RunAsync(generate: false);

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        CaptureSettings();
        if (_settings.OutputFolder is null)
        {
            MessageBox.Show(this, "Choose an output folder first. For MO2, use an empty mod folder (e.g. mods\\AnvilLOD Output) and enable it in MO2 after generating.",
                "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _ = RunAsync(generate: true);
    }

    private async Task RunAsync(bool generate)
    {
        CaptureSettings();
        _settings.Save();

        if (_settings.UseMo2 && (_settings.Mo2Instance is null || _settings.Mo2Profile is null))
        {
            MessageBox.Show(this, "Choose your MO2 instance folder and a profile.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_settings.UseMo2 && _settings.PluginsTxt is not null && _settings.DataFolder is null)
        {
            MessageBox.Show(this, "A plugins.txt needs a Data folder too.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var levels = new List<LodLevel>();
        if (_settings.Lod4) levels.Add(LodLevel.Lod4);
        if (_settings.Lod8) levels.Add(LodLevel.Lod8);
        if (_settings.Lod16) levels.Add(LodLevel.Lod16);
        if (_settings.Lod32) levels.Add(LodLevel.Lod32);
        if (levels.Count == 0)
        {
            MessageBox.Show(this, "Pick at least one LOD level.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string[]? worldspaces = _settings.AllWorldspaces
            ? null
            : _settings.Worldspaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var req = new ScanRequest(
            _settings.UseMo2
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _settings.Mo2Instance, Mo2Profile: _settings.Mo2Profile)
                : new GameContextOptions(_settings.DataFolder, _settings.PluginsTxt),
            new ScanOptions(
                Worldspaces: worldspaces is { Length: > 0 } ? worldspaces : null,
                IncludeInitiallyDisabled: _settings.IncludeDisabled,
                IncludeEnableParented: _settings.IncludeEnableParented,
                TreeLod: _settings.TreeLod,
                DynamicLod: _settings.DynamicLod),
            OutputFolder: _settings.OutputFolder,
            Levels: levels,
            Generate: generate,
            DynDolodFolder: _settings.DynDolodFolder,
            RemoveBuried: _settings.RemoveBuried,
            GrassLod: _settings.GrassLod,
            TreeBrightness: _settings.TreeBrightness / 100f,
            GrassDensity: _settings.GrassDensity / 100f,
            Preset: _settings.Preset switch { "Low" => AnvilLOD.Core.Lod.LodPreset.Low, "Medium" => AnvilLOD.Core.Lod.LodPreset.Medium, _ => AnvilLOD.Core.Lod.LodPreset.High });

        SetBusy(true);
        LogBox.Clear();
        WarningBox.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(m =>
        {
            ProgressText.Text = m;
            LogBox.AppendText($"[{_elapsed.Elapsed:mm\\:ss\\.f}] {m}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });

        try
        {
            var summary = await Task.Run(() => ScanPipeline.Run(req, progress, ct), ct);
            _lastTotal = _elapsed.Elapsed;
            _lastScan = summary;
            ShowResults(summary, _lastTotal);
            StatusText.Text = generate
                ? $"LOD generated in {_lastTotal.TotalSeconds:F1}s"
                : $"Scan finished in {_lastTotal.TotalSeconds:F1}s";
            ProgressText.Text = "Done";
            Progress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed — see Log";
            ProgressText.Text = ex.Message;
            LogBox.AppendText(Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
            MessageBox.Show(this, ex.Message, "Scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ProgressText.Text = "Cancelling…";
    }

    // ---------- mod author tools ----------

    private string? _authorOutputShown;

    private void AuthorAgree_Changed(object sender, RoutedEventArgs e)
    {
        AuthorPanel.IsEnabled = AuthorAgreeCheck.IsChecked == true;
        AuthorTab_GotFocus(sender, e);
    }

    private void AuthorTab_GotFocus(object sender, RoutedEventArgs e)
    {
        if (AuthorWarningText.Text.Length == 0) AuthorWarningText.Text = AnvilLOD.Plugins.Authoring.ModAuthorTool.Warning;
        if (AuthorPluginBox.Items.Count > 0) return;
        try
        {
            CaptureSettings();
            IEnumerable<string> plugins = [];
            if (_settings.UseMo2 && _settings.Mo2Instance is not null)
                plugins = Mo2Instance.Open(_settings.Mo2Instance).OpenProfile(_settings.Mo2Profile).EnabledPlugins;
            else if (_settings.PluginsTxt is not null && File.Exists(_settings.PluginsTxt))
                plugins = File.ReadAllLines(_settings.PluginsTxt).Where(l => l.StartsWith('*')).Select(l => l[1..].Trim());
            foreach (var p in plugins.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) AuthorPluginBox.Items.Add(p);
        }
        catch (Exception ex)
        {
            AuthorSummaryText.Text = "Couldn't list plugins (type the name instead): " + ex.Message;
        }
    }

    private void AuthorPlugin_Changed(object sender, SelectionChangedEventArgs e) => SuggestAuthorOutput(AuthorPluginBox.SelectedItem as string);

    private void AuthorPlugin_LostFocus(object sender, RoutedEventArgs e) => SuggestAuthorOutput(Blank(AuthorPluginBox.Text));

    private void SuggestAuthorOutput(string? plugin)
    {
        if (plugin is null || !_settings.UseMo2 || _settings.Mo2Instance is null) return;
        if (Blank(AuthorOutBox.Text) is { } current && current != _authorOutputShown) return; // the user typed their own
        try
        {
            var mods = Mo2Instance.Open(_settings.Mo2Instance).ModsFolder;
            AuthorOutBox.Text = _authorOutputShown = Path.Combine(mods, "AnvilLOD Author - " + Path.GetFileNameWithoutExtension(plugin));
        }
        catch { /* leave it empty */ }
    }

    private void BrowseAuthorOut_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select an empty folder for the generated files (a new MO2 mod)" };
        if (Directory.Exists(AuthorOutBox.Text)) d.InitialDirectory = AuthorOutBox.Text;
        if (d.ShowDialog(this) == true) AuthorOutBox.Text = d.FolderName;
    }

    private void AuthorOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Blank(AuthorOutBox.Text) is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void AuthorCheck_Click(object sender, RoutedEventArgs e) => _ = RunAuthorAsync(generate: false);

    private void AuthorGenerate_Click(object sender, RoutedEventArgs e) => _ = RunAuthorAsync(generate: true);

    private async Task RunAuthorAsync(bool generate)
    {
        CaptureSettings();
        _settings.Save();
        var plugin = Blank(AuthorPluginBox.Text);
        var output = Blank(AuthorOutBox.Text);
        if (plugin is null || output is null)
        {
            MessageBox.Show(this, "Choose a plugin and an output folder.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_settings.OutputFolder is { } lodOut && Path.GetFullPath(output).TrimEnd('\\').Equals(Path.GetFullPath(lodOut).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Use a separate folder, not your LOD output folder.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        float Num(TextBox b, float def) => float.TryParse(b.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : def;

        var req = new AnvilLOD.Plugins.Authoring.AuthorRequest(
            _settings.UseMo2
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _settings.Mo2Instance, Mo2Profile: _settings.Mo2Profile)
                : new GameContextOptions(_settings.DataFolder, _settings.PluginsTxt),
            plugin, output,
            LodMeshes: generate && AuthorMeshesCheck.IsChecked == true,
            Billboards: generate && AuthorBillboardsCheck.IsChecked == true,
            RuleFile: generate && AuthorRulesCheck.IsChecked == true,
            MinObjectSize: Num(AuthorMinSizeBox, 400f),
            MinTreeHeight: Num(AuthorMinTreeBox, 256f),
            Overwrite: AuthorOverwriteCheck.IsChecked == true,
            DynDolodFolder: _settings.DynDolodFolder,
            Preset: _settings.Preset switch { "Low" => AnvilLOD.Core.Lod.LodPreset.Low, "Medium" => AnvilLOD.Core.Lod.LodPreset.Medium, _ => AnvilLOD.Core.Lod.LodPreset.High });

        SetBusy(true);
        AuthorPanel.IsEnabled = false;
        LogBox.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(m =>
        {
            ProgressText.Text = m;
            AuthorSummaryText.Text = m;
            LogBox.AppendText($"[{_elapsed.Elapsed:mm\\:ss\\.f}] {m}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
        try
        {
            var r = await Task.Run(() => AnvilLOD.Plugins.Authoring.ModAuthorTool.Run(req, progress, ct), ct);
            AuthorGrid.ItemsSource = r.Items
                .OrderBy(i => i.Status.StartsWith("Has", StringComparison.Ordinal) || i.Status.StartsWith("Small", StringComparison.Ordinal) || i.Status.StartsWith("No LOD on purpose", StringComparison.Ordinal))
                .ThenByDescending(i => i.Size).ToList();
            int missing = r.Items.Count(i => i.Status.StartsWith("Missing", StringComparison.Ordinal));
            int broken = r.Items.Count(i => i.Status.Contains("missing", StringComparison.Ordinal) && !i.Status.StartsWith("Missing", StringComparison.Ordinal) || i.Status.StartsWith("Model", StringComparison.Ordinal));
            AuthorSummaryText.Text = $"{r.Items.Count} placed objects/trees checked: {missing} missing LOD or billboards, {broken} with broken files. "
                + (generate ? $"Generated {r.MeshesWritten} LOD meshes and {r.BillboardsWritten} billboards{(r.RuleFile is null ? "" : " plus a rule file")}. " : "")
                + $"Report: {r.ReportFile}";
            AuthorOpenButton.IsEnabled = true;
            StatusText.Text = $"Mod author check finished in {r.Elapsed.TotalSeconds:F0}s";
            ProgressText.Text = "Done";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed — see Log";
            AuthorSummaryText.Text = ex.Message;
            LogBox.AppendText(Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
            MessageBox.Show(this, ex.Message, "Mod author tools", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            AuthorPanel.IsEnabled = AuthorAgreeCheck.IsChecked == true;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        GenerateButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        SettingsPanel.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy && _lastScan is not null;
        Progress.IsIndeterminate = busy;
        if (busy)
        {
            Progress.Value = 0;
            _elapsed.Restart();
            _clock.Start();
        }
        else
        {
            _elapsed.Stop();
            _clock.Stop();
        }
    }

    // ---------- results ----------

    private void ShowResults(ScanSummary s, TimeSpan total)
    {
        OverviewHint.Text = s.Grids.Count == 0
            ? "No worldspace with a .lod settings file matched. Check the worldspace names and that LODSettings files are present."
            : $"Scanned {string.Join(", ", s.Grids.Keys)}.";

        TilePlugins.Text = s.PluginsLoaded.ToString("N0");
        TilePluginsLabel.Text = s.MissingPlugins.Count == 0
            ? "Plugins"
            : $"Plugins found (of {s.Stats.PluginsInLoadOrder:N0} listed)";

        WarningList.ItemsSource = s.Warnings;
        WarningBox.Visibility = s.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TileVisited.Text = s.Stats.PlacedObjectsVisited.ToString("N0");
        TileLodRefs.Text = s.Stats.LodReferencesFound.ToString("N0");
        TileBlocks.Text = s.Quads.Count.ToString("N0");
        if (s.Generation is { } g)
        {
            TileRebuild.Text = g.BlocksWritten.ToString("N0");
            TileRebuildLabel.Text = g.BlocksFailed > 0 ? $"Blocks written ({g.BlocksFailed} failed)" : $"Blocks written ({s.Plan.Unchanged.Count:N0} unchanged)";
        }
        else
        {
            TileRebuild.Text = s.Plan.Rebuild.Count.ToString("N0");
            TileRebuildLabel.Text = "Blocks to rebuild";
        }
        TileTime.Text = $"{total.TotalSeconds:F1}s";

        LevelGrid.ItemsSource = s.Quads.Keys
            .GroupBy(q => q.Worldspace, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g => new LevelRow(
                g.Key,
                g.Count(q => q.Level == LodLevel.Lod4),
                g.Count(q => q.Level == LodLevel.Lod8),
                g.Count(q => q.Level == LodLevel.Lod16),
                g.Count(q => q.Level == LodLevel.Lod32)))
            .ToList();

        TimingText.Text =
            $"BSA index     {s.IndexTime.TotalSeconds,8:F2}s   ({s.ArchivedFiles:N0} files in {s.ArchivesIndexed} archives)\n" +
            $"Plugin scan   {s.Stats.Elapsed.TotalSeconds,8:F2}s\n" +
            $"Bucket+hash   {s.BucketAndHashTime.TotalSeconds,8:F2}s\n" +
            $"Total         {total.TotalSeconds,8:F2}s\n\n" +
            $"Skipped: {s.Stats.SkippedDisabled:N0} disabled, {s.Stats.SkippedMissingMesh:N0} without usable mesh, {s.SkippedOutsideGrid:N0} outside LOD grid\n\n" +
            $"Dynamic LOD: {s.DynamicRefs:N0} switchable references (shown by the SKSE plugin)\n" +
            $"Grass LOD: {s.GrassCells:N0} cached cells" + (s.MissingGrassBillboards is { Count: > 0 } mg ? $", {mg.Count} grass types without billboard" : "") + "\n" +
            $"Trees: {s.TreeReferences:N0} with billboards, {s.MissingBillboards?.Values.Sum() ?? 0:N0} without ({s.MissingBillboards?.Count ?? 0:N0} tree types, "
                + $"{s.MissingBillboards?.Keys.Count(k => s.SkippedByTexGen?.Contains(k) == true) ?? 0:N0} of them small plants TexGen skips)" +
            (s.Trees is { } treeStats
                ? $"\nTree LOD: {treeStats.Instances:N0} trees, {treeStats.TreeTypes} types, {treeStats.Blocks:N0} blocks ({treeStats.EmptyBlocks:N0} vanilla blocks emptied), {treeStats.Elapsed.TotalSeconds:F1}s"
                : "");

        var rebuild = s.Plan.Rebuild.ToHashSet();
        _allBlocks = s.Quads
            .OrderBy(kv => kv.Key.Worldspace).ThenBy(kv => kv.Key.Level).ThenBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y)
            .Select(kv => new BlockRow(kv.Key.FileName, (int)kv.Key.Level, kv.Key.X, kv.Key.Y, kv.Value.Count,
                s.Generation is { } gen
                    ? gen.BlockErrors.ContainsKey(kv.Key) ? "Failed" : rebuild.Contains(kv.Key) ? "Written" : "Unchanged"
                    : rebuild.Contains(kv.Key) ? "Rebuild" : "Unchanged"))
            .ToList();
        ApplyBlockFilter();

        var missing = s.Stats.MissingMeshes
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => new MissingRow(kv.Key, $"not installed ({kv.Value} base objects)"));
        var broken = (s.Generation?.MeshErrors ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key)
            .Select(kv => new MissingRow(kv.Key, kv.Value));
        var noBillboard = (s.MissingBillboards ?? new Dictionary<string, long>())
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Select(kv => new MissingRow(kv.Key, s.SkippedByTexGen?.Contains(kv.Key) == true
                ? $"small plant, TexGen skips it, no LOD (same as DynDOLOD) ({kv.Value} placed)"
                : $"no tree billboard ({kv.Value} placed trees) - run TexGen"));
        var noGrassBillboard = (s.MissingGrassBillboards ?? new Dictionary<string, long>())
            .OrderBy(kv => kv.Key)
            .Select(kv => new MissingRow(kv.Key, s.SkippedByTexGen?.Contains(kv.Key) == true
                ? "small grass, TexGen skips it, no grass LOD"
                : "no grass billboard - run TexGen with grass billboards"));
        MissingGrid.ItemsSource = missing.Concat(broken).Concat(noBillboard).Concat(noGrassBillboard).ToList();
        int realBillboards = (s.MissingBillboards?.Keys ?? []).Concat(s.MissingGrassBillboards?.Keys ?? [])
            .Count(k => s.SkippedByTexGen?.Contains(k) != true);
        TileMissing.Text = (s.Stats.MissingMeshes.Count + (s.Generation?.MeshErrors.Count ?? 0) + realBillboards).ToString("N0");
    }

    private void BlockFilter_Changed(object sender, TextChangedEventArgs e) => ApplyBlockFilter();

    private void ApplyBlockFilter()
    {
        if (BlockGrid is null) return;
        var f = BlockFilterBox.Text.Trim();
        var rows = f.Length == 0
            ? _allBlocks
            : _allBlocks.Where(r => r.File.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        BlockGrid.ItemsSource = rows;
        BlockCountText.Text = $"{rows.Count:N0} of {_allBlocks.Count:N0}";
    }

    // ---------- export ----------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScan is null) return;
        var d = new SaveFileDialog
        {
            Title = "Save scan report",
            Filter = "JSON (*.json)|*.json",
            FileName = $"AnvilLOD.scan.{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (_settings.OutputFolder is { } o && Directory.Exists(o)) d.InitialDirectory = o;
        if (d.ShowDialog(this) != true) return;

        ScanReport.Write(d.FileName, _lastScan, _lastTotal);
        StatusText.Text = "Report saved";
    }
}
