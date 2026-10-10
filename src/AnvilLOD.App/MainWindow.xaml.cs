using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        VersionText.Text = "v" + version;
        Title = "AnvilLOD " + version;
        _clock.Tick += (_, _) => StatusText.Text = $"Running… {_elapsed.Elapsed:mm\\:ss}";
        ApplySettings();
        InitPresets();
        AuthorWarningText.Text = AnvilLOD.Plugins.Authoring.ModAuthorTool.Warning;
        if (StartupLog.IsUnderMo2()) Title = "AnvilLOD  (running under MO2)";
    }

    // ---------- settings <-> controls ----------

    private void ApplySettings()
    {
        DynDolodBox.Text = _settings.DynDolodFolder ?? "";
        PresetBox.SelectedIndex = _settings.Preset switch { "Low" => 0, "Medium" => 1, _ => 2 };
        CandlesCheck.IsChecked = _settings.Candles;
        FxGlowCheck.IsChecked = _settings.FxGlow;
        CustomRulesBox.Text = _settings.CustomRulesFile ?? "";
        CustomRulesCheck.IsChecked = _settings.CustomRulesEnabled;
        Mo2Box.Text = _settings.Mo2Instance ?? "";
        Mo2GameBox.Text = _settings.Mo2GamePath ?? "";
        Mo2ModsBox.Text = _settings.Mo2ModsFolder ?? "";
        Mo2ProfilesBox.Text = _settings.Mo2ProfilesFolder ?? "";
        Mo2OverwriteBox.Text = _settings.Mo2OverwriteFolder ?? "";
        UpdateLocationsButton();
        MakerInputBox.Text = _settings.MakerInputs ?? "";
        MakerModNameBox.Text = _settings.MakerModName ?? "AnvilLOD LOD Meshes";
        MakerOutBox.Text = _settings.MakerOutput ?? "";
        if (_settings.MakerFolderMode) MakerFolderRadio.IsChecked = true; else MakerNewModRadio.IsChecked = true;
        MakerOutMode_Changed(this, new RoutedEventArgs());
        MakerBudget_Changed(this, new RoutedEventArgs());
        LoadProfiles(_settings.Mo2Profile);
        if (_settings.UseMo2) Mo2Radio.IsChecked = true; else if (_settings.UseVortex) VortexRadio.IsChecked = true; else DataRadio.IsChecked = true;
        DataBox.Text = _settings.DataFolder ?? "";
        PluginsBox.Text = _settings.PluginsTxt ?? "";
        OutputBox.Text = _settings.OutputFolder ?? "";
        CleanOutputCheck.IsChecked = _settings.CleanOutput;
        WorldspaceBox.Text = _settings.Worldspaces;
        AllWorldspacesCheck.IsChecked = _settings.AllWorldspaces;
        WorldspaceBox.IsEnabled = !_settings.AllWorldspaces;
        Lod4Check.IsChecked = _settings.Lod4;
        Lod8Check.IsChecked = _settings.Lod8;
        Lod16Check.IsChecked = _settings.Lod16;
        Lod32Check.IsChecked = _settings.Lod32;
        RemoveBuriedCheck.IsChecked = _settings.RemoveBuried;
        TreeLodCheck.IsChecked = _settings.TreeLod;
        LargeRefsCheck.IsChecked = _settings.LargeReferences;
        LargeRefsEslCheck.IsChecked = _settings.LargeRefsEsl;
        PbrLodCheck.IsChecked = _settings.PbrLod;
        PbrLodBrightnessBox.SelectedIndex = BrightnessIndex(_settings.PbrLodBrightness);
        UndersideCheck.IsChecked = _settings.Underside;
        Tree3DCheck.IsChecked = _settings.Tree3D;
        Tree3DLod8Check.IsChecked = _settings.Tree3DLod8;
        Tree3DNameCheck.IsChecked = _settings.Tree3DByName;
        GrassLodCheck.IsChecked = _settings.GrassLod;
        TreeBrightnessBox.SelectedIndex = BrightnessIndex(_settings.TreeBrightness);
        ObjectBrightnessBox.SelectedIndex = BrightnessIndex(_settings.ObjectBrightness);
        DynamicLodCheck.IsChecked = _settings.DynamicLod;
        GridObjectsCheck.IsChecked = _settings.GridObjects;
        SeasonsCheck.IsChecked = _settings.Seasons;
        WaterStandInsCheck.IsChecked = _settings.WaterStandIns;
        SkseDllBox.SelectedIndex = Math.Clamp(_settings.SkseDll, 0, 4);
        GrassDensityBox.SelectedIndex = GrassDensityIndex(_settings.GrassDensity);
        FillGrassBrightness(GrassTopBox, _settings.GrassTop);
        FillGrassBrightness(GrassBottomBox, _settings.GrassBottom);
        IncludeDisabledCheck.IsChecked = _settings.IncludeDisabled;
        IncludeEnableParentCheck.IsChecked = _settings.IncludeEnableParented;
    }

    private void CaptureSettings()
    {
        _settings.UseMo2 = Mo2Radio.IsChecked == true;
        _settings.UseVortex = VortexRadio.IsChecked == true;
        _settings.Mo2Instance = Blank(Mo2Box.Text);
        _settings.Mo2Profile = ProfileBox.SelectedItem as string;
        _settings.Mo2GamePath = Blank(Mo2GameBox.Text);
        _settings.Mo2ModsFolder = Blank(Mo2ModsBox.Text);
        _settings.Mo2ProfilesFolder = Blank(Mo2ProfilesBox.Text);
        _settings.Mo2OverwriteFolder = Blank(Mo2OverwriteBox.Text);
        _settings.DataFolder = Blank(DataBox.Text);
        _settings.PluginsTxt = Blank(PluginsBox.Text);
        _settings.OutputFolder = Blank(OutputBox.Text);
        _settings.CleanOutput = CleanOutputCheck.IsChecked == true;
        _settings.Worldspaces = WorldspaceBox.Text.Trim();
        _settings.AllWorldspaces = AllWorldspacesCheck.IsChecked == true;
        _settings.Lod4 = Lod4Check.IsChecked == true;
        _settings.Lod8 = Lod8Check.IsChecked == true;
        _settings.Lod16 = Lod16Check.IsChecked == true;
        _settings.Lod32 = Lod32Check.IsChecked == true;
        _settings.DynDolodFolder = Blank(DynDolodBox.Text);
        _settings.Preset = PresetBox.SelectedIndex switch { 0 => "Low", 1 => "Medium", _ => "High" };
        _settings.Candles = CandlesCheck.IsChecked == true;
        _settings.FxGlow = FxGlowCheck.IsChecked == true;
        _settings.CustomRulesFile = Blank(CustomRulesBox.Text);
        _settings.CustomRulesEnabled = CustomRulesCheck.IsChecked == true;
        _settings.RemoveBuried = RemoveBuriedCheck.IsChecked == true;
        _settings.TreeLod = TreeLodCheck.IsChecked == true;
        _settings.LargeReferences = LargeRefsCheck.IsChecked == true;
        _settings.LargeRefsEsl = LargeRefsEslCheck.IsChecked == true;
        _settings.PbrLod = PbrLodCheck.IsChecked == true;
        _settings.PbrLodBrightness = BrightnessPercent(PbrLodBrightnessBox.SelectedIndex);
        _settings.Underside = UndersideCheck.IsChecked == true;
        _settings.Tree3D = Tree3DCheck.IsChecked == true;
        _settings.Tree3DLod8 = Tree3DLod8Check.IsChecked == true;
        _settings.Tree3DByName = Tree3DNameCheck.IsChecked == true;
        _settings.GrassLod = GrassLodCheck.IsChecked == true;
        _settings.TreeBrightness = BrightnessPercent(TreeBrightnessBox.SelectedIndex);
        _settings.ObjectBrightness = BrightnessPercent(ObjectBrightnessBox.SelectedIndex);
        _settings.DynamicLod = DynamicLodCheck.IsChecked == true;
        _settings.GridObjects = GridObjectsCheck.IsChecked == true;
        _settings.Seasons = SeasonsCheck.IsChecked == true;
        _settings.WaterStandIns = WaterStandInsCheck.IsChecked == true;
        _settings.SkseDll = Math.Max(0, SkseDllBox.SelectedIndex);
        _settings.GrassDensity = GrassDensityChoices[Math.Clamp(GrassDensityBox.SelectedIndex, 0, GrassDensityChoices.Length - 1)];
        _settings.GrassTop = GrassBrightnessValue(GrassTopBox, _settings.GrassTop);
        _settings.GrassBottom = GrassBrightnessValue(GrassBottomBox, _settings.GrassBottom);
        _settings.IncludeDisabled = IncludeDisabledCheck.IsChecked == true;
        _settings.IncludeEnableParented = IncludeEnableParentCheck.IsChecked == true;
        _settings.StoreActivePreset();   // the active preset always mirrors the column
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
        var vortex = VortexRadio.IsChecked == true;
        Mo2Panel.Visibility = mo2 ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = mo2 ? Visibility.Collapsed : Visibility.Visible;
        if (VortexPanel is null || DataInfoText is null) return;
        VortexPanel.Visibility = vortex ? Visibility.Visible : Visibility.Collapsed;
        DataInfoText.Text = vortex
            ? "Vortex deploys mods into the game's Data folder: leave Data folder empty to auto-detect, and plugins.txt empty for Vortex's load order. Deploy in Vortex before generating."
            : "For a plain (non-MO2) install.";
        if (vortex)
        {
            var staging = VortexInstall.StagingFolder();
            VortexOutputButton.IsEnabled = staging is not null;
            VortexInfoText.Text = staging is not null
                ? $"Staging folder: {staging}. After Generate, refresh Vortex (or restart it), enable \"{VortexInstall.OutputModName}\" and Deploy."
                : VortexInstall.IsInstalled()
                    ? "Vortex's Skyrim SE staging folder wasn't found at the default place (%APPDATA%\\Vortex\\skyrimse\\mods). Pick an output folder, then install it in Vortex as a mod."
                    : "Vortex doesn't seem to be installed for this user.";
        }
    }

    private void VortexOutput_Click(object sender, RoutedEventArgs e)
    {
        if (VortexInstall.StagingFolder() is not { } staging) return;
        var output = Path.Combine(staging, VortexInstall.OutputModName);
        Directory.CreateDirectory(output);
        OutputBox.Text = output;
    }

    private void BrowseMo2_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select the MO2 instance folder (contains ModOrganizer.ini)" };
        if (Directory.Exists(Mo2Box.Text)) d.InitialDirectory = Mo2Box.Text;
        if (d.ShowDialog(this) != true) return;
        Mo2Box.Text = d.FolderName;
        LoadProfiles(null);
    }

    /// <summary>The folders typed under Locations, or null when they are all empty (= auto-detect everything).</summary>
    private Mo2Locations? Mo2LocationsFromBoxes()
    {
        var l = new Mo2Locations(Blank(Mo2GameBox.Text), Blank(Mo2ModsBox.Text), Blank(Mo2ProfilesBox.Text), Blank(Mo2OverwriteBox.Text));
        return l.IsEmpty ? null : l;
    }

    private Mo2Locations? TypedLocations()
    {
        var l = new Mo2Locations(_settings.Mo2GamePath, _settings.Mo2ModsFolder, _settings.Mo2ProfilesFolder, _settings.Mo2OverwriteFolder);
        return l.IsEmpty ? null : l;
    }

    private void Mo2Box_LostFocus(object sender, RoutedEventArgs e)
    {
        LoadProfiles(ProfileBox.SelectedItem as string);
        UpdateLocationsButton();
    }

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
            var inst = Mo2Instance.Open(folder, Mo2LocationsFromBoxes());
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

    private AnvilLOD.Core.Lod.LodPreset RulePreset() => PresetBox.SelectedIndex switch
    {
        0 => AnvilLOD.Core.Lod.LodPreset.Low,
        1 => AnvilLOD.Core.Lod.LodPreset.Medium,
        _ => AnvilLOD.Core.Lod.LodPreset.High,
    };

    private void EditRules_Click(object sender, RoutedEventArgs e)
    {
        var editor = new RulesEditorWindow(Blank(DynDolodBox.Text), RulePreset(), CandlesCheck.IsChecked == true, FxGlowCheck.IsChecked == true,
            Blank(CustomRulesBox.Text), _settings.ActivePreset) { Owner = this };
        editor.ShowDialog();
        if (editor.ChosenFile is { } file && File.Exists(file))
        {
            CustomRulesBox.Text = file;
            CustomRulesCheck.IsChecked = true;
        }
    }

    private void BrowseCustomRules_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select your LOD rules file", Filter = "Rule files (*.ini)|*.ini|All files (*.*)|*.*", CheckFileExists = true };
        if (File.Exists(CustomRulesBox.Text) && Path.GetDirectoryName(CustomRulesBox.Text) is { } dir) d.InitialDirectory = dir;
        if (d.ShowDialog(this) != true) return;
        CustomRulesBox.Text = d.FileName;
        CustomRulesCheck.IsChecked = true;
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
            // No folder chosen: AnvilLOD Output, as its own mod folder next to the others (created when the run starts).
            var folder = OutputFolder.DefaultFolder(BuildGameOptions());
            OutputBox.Text = folder;
            CaptureSettings();
        }
        _ = RunAsync(generate: true);
    }

    private GameContextOptions BuildGameOptions() => _settings.UseMo2
        ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _settings.Mo2Instance, Mo2Profile: _settings.Mo2Profile, Mo2Locations: TypedLocations())
        : new GameContextOptions(_settings.DataFolder, _settings.PluginsTxt, Mode: _settings.UseVortex ? GameSourceMode.Vortex : GameSourceMode.DataFolder);

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
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _settings.Mo2Instance, Mo2Profile: _settings.Mo2Profile, Mo2Locations: TypedLocations())
                : new GameContextOptions(_settings.DataFolder, _settings.PluginsTxt, Mode: _settings.UseVortex ? GameSourceMode.Vortex : GameSourceMode.DataFolder),
            new ScanOptions(
                Worldspaces: worldspaces is { Length: > 0 } ? worldspaces : null,
                IncludeInitiallyDisabled: _settings.IncludeDisabled,
                IncludeEnableParented: _settings.IncludeEnableParented,
                TreeLod: _settings.TreeLod,
                DynamicLod: _settings.DynamicLod,
                GridObjects: _settings.GridObjects,
                Tree3D: _settings.Tree3D ? new AnvilLOD.Core.World.Tree3DSettings(true, _settings.Tree3DLod8, _settings.Tree3DByName) : null),
            OutputFolder: _settings.OutputFolder,
            Levels: levels,
            Generate: generate,
            DynDolodFolder: _settings.DynDolodFolder,
            RemoveBuried: _settings.RemoveBuried,
            GrassLod: _settings.GrassLod,
            TreeBrightness: _settings.TreeBrightness / 100f,
            ObjectBrightness: _settings.ObjectBrightness / 100f,
            Seasons: _settings.Seasons,
            Underside: _settings.Underside,
            PbrLod: _settings.PbrLod,
            LargeReferences: _settings.LargeReferences,
            LargeRefsEsl: _settings.LargeRefsEsl,
            CleanOutput: _settings.CleanOutput,
            Candles: _settings.Candles,
            FxGlow: _settings.FxGlow,
            CustomRulesFile: _settings.CustomRulesEnabled ? _settings.CustomRulesFile : null,
            PbrLodBrightness: _settings.PbrLodBrightness / 100f,
            GrassDensity: _settings.GrassDensity / 100f,
            WaterStandIns: _settings.WaterStandIns,
            GrassTop: _settings.GrassTop / 100f,
            GrassBottom: _settings.GrassBottom / 100f,
            SkseDll: (AnvilLOD.Plugins.SkseDllChoice)Math.Clamp(_settings.SkseDll, 0, 4),
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
                plugins = Mo2Instance.Open(_settings.Mo2Instance, TypedLocations()).OpenProfile(_settings.Mo2Profile).EnabledPlugins;
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
            var mods = Mo2Instance.Open(_settings.Mo2Instance, TypedLocations()).ModsFolder;
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
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _settings.Mo2Instance, Mo2Profile: _settings.Mo2Profile, Mo2Locations: TypedLocations())
                : new GameContextOptions(_settings.DataFolder, _settings.PluginsTxt),
            plugin, output,
            LodMeshes: generate && AuthorMeshesCheck.IsChecked == true,
            Billboards: generate && AuthorBillboardsCheck.IsChecked == true,
            RuleFile: generate && AuthorRulesCheck.IsChecked == true,
            MinObjectSize: Num(AuthorMinSizeBox, 400f),
            MinTreeHeight: Num(AuthorMinTreeBox, 256f),
            Overwrite: AuthorOverwriteCheck.IsChecked == true,
            Budget: AuthorBudgetCheck.IsChecked == true ? AnvilLOD.Meshes.Authoring.BudgetMode.Recommended : AnvilLOD.Meshes.Authoring.BudgetMode.Off,
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

    // ---------- LOD Mesh Maker ----------

    private void MakerAgree_Changed(object sender, RoutedEventArgs e)
    {
        MakerPanel.IsEnabled = MakerAgreeCheck.IsChecked == true;
        MakerTab_GotFocus(sender, e);
    }

    private void MakerTab_GotFocus(object sender, RoutedEventArgs e)
    {
        if (MakerWarningText.Text.Length == 0) MakerWarningText.Text = AnvilLOD.Plugins.Authoring.LodMeshMaker.Warning;
    }

    private void MakerBudget_Changed(object sender, RoutedEventArgs e)
    {
        if (MakerBudget0Box is null || MakerBudgetHint is null) return;   // fires while the window is being built
        bool custom = MakerBudgetBox.SelectedIndex == 1;
        MakerBudget0Box.IsEnabled = MakerBudget1Box.IsEnabled = MakerBudget2Box.IsEnabled = custom;
        MakerBudgetHint.Text = MakerBudgetBox.SelectedIndex switch
        {
            0 => "Most triangles at LOD 0 / 1 / 2, by the model's largest dimension: " + AnvilLOD.Meshes.Authoring.LodBudgets.Describe()
                 + ". Based on the " + AnvilLOD.Meshes.Authoring.LodBudgets.Basis + ".",
            1 => "Your own maximums per level; 0 means no limit for that level. A farther level never gets more triangles than a nearer one.",
            _ => "No budget: only the Detail setting limits the triangles, so very dense models stay heavy.",
        };
    }

    private void MakerOutMode_Changed(object sender, RoutedEventArgs e)
    {
        if (MakerModNameBox is null || MakerOutBox is null) return;   // fires while the window is being built
        MakerModNameBox.IsEnabled = MakerNewModRadio.IsChecked == true;
        MakerOutBox.IsEnabled = MakerFolderRadio.IsChecked == true;
    }

    private void AppendMakerInput(string path)
    {
        var lines = MakerInputBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!lines.Contains(path, StringComparer.OrdinalIgnoreCase)) lines.Add(path);
        MakerInputBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void MakerAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select full models", Filter = "Models (*.nif)|*.nif", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        foreach (var f in d.FileNames) AppendMakerInput(f);
    }

    private void MakerAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select a mod folder, a meshes folder or a folder of models", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        foreach (var f in d.FolderNames) AppendMakerInput(f);
    }

    private void MakerClear_Click(object sender, RoutedEventArgs e) => MakerInputBox.Clear();

    private void BrowseMakerOut_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select a folder for the LOD meshes (a new or existing mod folder)" };
        if (Directory.Exists(MakerOutBox.Text)) d.InitialDirectory = MakerOutBox.Text;
        if (d.ShowDialog(this) == true) MakerOutBox.Text = d.FolderName;
    }

    private string? _makerOutput;

    private void MakerOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_makerOutput is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void MakerGenerate_Click(object sender, RoutedEventArgs e) => _ = RunMakerAsync();

    private async Task RunMakerAsync()
    {
        CaptureSettings();
        _settings.MakerInputs = Blank(MakerInputBox.Text);
        _settings.MakerFolderMode = MakerFolderRadio.IsChecked == true;
        _settings.MakerModName = Blank(MakerModNameBox.Text);
        _settings.MakerOutput = Blank(MakerOutBox.Text);
        _settings.Save();

        var inputs = MakerInputBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (inputs.Length == 0)
        {
            MessageBox.Show(this, "Add the models (or folders of models) to make LOD meshes from.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string output;
        if (MakerFolderRadio.IsChecked == true)
        {
            if (Blank(MakerOutBox.Text) is not { } typed)
            {
                MessageBox.Show(this, "Choose the output folder.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            output = typed;
        }
        else
        {
            if (Blank(MakerModNameBox.Text) is not { } name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "Give the new mod a name (no \\ / : * ? \" < > |).", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_settings.UseMo2 || _settings.Mo2Instance is null)
            {
                MessageBox.Show(this, "A new mod needs MO2 instance mode (to find your mods folder). Choose \"A folder I choose\" instead, or switch the source to MO2 instance.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                output = Path.Combine(Mo2Instance.Open(_settings.Mo2Instance, TypedLocations()).ModsFolder, name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (_settings.OutputFolder is { } lodOut && Path.GetFullPath(output).TrimEnd('\\').Equals(Path.GetFullPath(lodOut).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Use a separate folder, not your LOD output folder.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var levels = new List<int>();
        if (MakerLod0Check.IsChecked == true) levels.Add(0);
        if (MakerLod1Check.IsChecked == true) levels.Add(1);
        if (MakerLod2Check.IsChecked == true) levels.Add(2);
        if (levels.Count == 0)
        {
            MessageBox.Show(this, "Choose at least one LOD level.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        float detail = MakerDetailBox.SelectedIndex switch { 0 => 0.5f, 2 => 2f, 3 => 4f, _ => 1f };
        float minSize = float.TryParse(MakerMinSizeBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 0 ? ms : 0f;

        int Num0(TextBox b) => int.TryParse(b.Text, out var v) && v > 0 ? v : 0;
        var budgetMode = MakerBudgetBox.SelectedIndex switch
        {
            1 => AnvilLOD.Meshes.Authoring.BudgetMode.Custom,
            2 => AnvilLOD.Meshes.Authoring.BudgetMode.Off,
            _ => AnvilLOD.Meshes.Authoring.BudgetMode.Recommended,
        };
        var req = new AnvilLOD.Plugins.Authoring.LodMakerRequest(inputs, output, Path.GetFileName(Path.TrimEndingDirectorySeparator(output)), levels, detail, minSize,
            RuleFile: MakerRulesCheck.IsChecked == true, Overwrite: MakerOverwriteCheck.IsChecked == true,
            Budget: budgetMode, CustomBudget: [Num0(MakerBudget0Box), Num0(MakerBudget1Box), Num0(MakerBudget2Box)]);

        SetBusy(true);
        MakerPanel.IsEnabled = false;
        LogBox.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(m =>
        {
            ProgressText.Text = m;
            MakerSummaryText.Text = m;
            LogBox.AppendText($"[{_elapsed.Elapsed:mm\\:ss\\.f}] {m}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
        try
        {
            var r = await Task.Run(() => AnvilLOD.Plugins.Authoring.LodMeshMaker.Run(req, progress, ct), ct);
            MakerGrid.ItemsSource = r.Items.OrderBy(i => i.Status == "Done" ? 1 : 0).ThenByDescending(i => i.Size).ToList();
            int skipped = r.Items.Count - r.ModelsDone;
            MakerSummaryText.Text = $"{r.ModelsDone} models done, {r.MeshesWritten} LOD meshes written"
                + (skipped > 0 ? $", {skipped} skipped (see the table)" : "") + (r.RuleFile is null ? "" : ", rule file written")
                + $". Output: {output}. Report: {r.ReportFile}";
            _makerOutput = output;
            MakerOpenButton.IsEnabled = true;
            StatusText.Text = $"LOD Mesh Maker finished in {r.Elapsed.TotalSeconds:F0}s";
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
            MakerSummaryText.Text = ex.Message;
            LogBox.AppendText(Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
            MessageBox.Show(this, ex.Message, "LOD Mesh Maker", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            MakerPanel.IsEnabled = MakerAgreeCheck.IsChecked == true;
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
        PresetBar.IsEnabled = !busy;
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

    // ---------- presets: three tabs that each remember the whole settings column ----------

    private bool _presetsReady;
    private bool _presetSwitching;
    private int _shownPreset;
    private int _renaming = -1;

    private RadioButton PresetTab(int i) => i switch { 0 => PresetTab0, 1 => PresetTab1, _ => PresetTab2 };

    private int CheckedPreset() => PresetTab1.IsChecked == true ? 1 : PresetTab2.IsChecked == true ? 2 : 0;

    /// <summary>Puts the saved names on the tabs and selects the active one, without animation. Called once the settings are on screen.</summary>
    private void InitPresets()
    {
        _settings.EnsurePresets();
        for (int i = 0; i < AppSettings.PresetCount; i++) PresetTab(i).Content = _settings.Presets[i].Name;
        _shownPreset = _settings.ActivePreset;
        _presetsReady = false;
        PresetTab(_shownPreset).IsChecked = true;
        _presetsReady = true;
        PresetIndicator.Loaded += (_, _) => MoveIndicator(_shownPreset, animate: false);
        MoveIndicator(_shownPreset, animate: false);
    }

    private void PresetGrid_SizeChanged(object sender, SizeChangedEventArgs e) => MoveIndicator(_shownPreset, animate: false);

    private void PresetTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_presetsReady) return;
        _ = SwitchPresetAsync(CheckedPreset());
    }

    /// <summary>The indicator under the active tab slides there, and glows while it travels.</summary>
    private void MoveIndicator(int index, bool animate)
    {
        double column = PresetGrid.ActualWidth / AppSettings.PresetCount;
        if (column <= 0) return;
        PresetIndicator.Width = Math.Max(0, column - 8);
        double x = index * column + 4;
        if (!animate)
        {
            PresetIndicatorMove.BeginAnimation(TranslateTransform.XProperty, null);
            PresetIndicatorMove.X = x;
            return;
        }
        PresetIndicatorMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut },
        });
        var pulse = new DoubleAnimation(8, 26, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        PresetGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, pulse);
        PresetGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(180)) { AutoReverse = true });
    }

    private static Task AnimateAsync(Action<TaskCompletionSource> start)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        start(done);
        return done.Task;
    }

    /// <summary>Slides and fades the settings column out (or back in) in the direction of travel.</summary>
    private Task SlideAsync(bool show, int direction)
    {
        const double distance = 30;
        double fromX = show ? direction * distance : 0, toX = show ? 0 : -direction * distance;
        double fromO = show ? 0 : 1, toO = show ? 1 : 0;
        var span = TimeSpan.FromMilliseconds(show ? 240 : 150);
        IEasingFunction ease = show ? new CubicEase { EasingMode = EasingMode.EaseOut } : new QuadraticEase { EasingMode = EasingMode.EaseIn };
        return AnimateAsync(done =>
        {
            var opacity = new DoubleAnimation(fromO, toO, span) { EasingFunction = ease };
            opacity.Completed += (_, _) =>
            {
                SettingsScroll.BeginAnimation(OpacityProperty, null);
                SettingsMove.BeginAnimation(TranslateTransform.XProperty, null);
                SettingsScroll.Opacity = toO;
                SettingsMove.X = toX;
                done.TrySetResult();
            };
            SettingsMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, span) { EasingFunction = ease });
            SettingsScroll.BeginAnimation(OpacityProperty, opacity);
        });
    }

    /// <summary>A bright line runs across the top of the settings column.</summary>
    private void Sweep(int direction)
    {
        double width = SettingsScroll.ActualWidth;
        if (width <= 0) return;
        double from = direction > 0 ? -PresetSweep.Width : width, to = direction > 0 ? width : -PresetSweep.Width;
        PresetSweepMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(520)) { EasingFunction = new QuadraticEase() });
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.25)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        fade.Duration = TimeSpan.FromMilliseconds(520);
        PresetSweep.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Switches to another preset: the column slides out, the saved values of the new preset replace the settings, and the column
    /// slides back in while the indicator glides to the new tab. The old preset is saved first, so nothing is lost.
    /// </summary>
    private async Task SwitchPresetAsync(int index)
    {
        if (_presetSwitching) return;   // the loop below picks up a click made meanwhile
        _presetSwitching = true;
        try
        {
            while (index != _shownPreset)
            {
                int direction = index > _shownPreset ? 1 : -1;
                CaptureSettings();   // controls -> settings, and into the slot that is leaving
                MoveIndicator(index, animate: true);
                await SlideAsync(show: false, direction);
                _settings.ActivatePreset(index);
                _shownPreset = index;
                ApplySettings();
                SettingsScroll.ScrollToTop();
                Sweep(direction);
                await SlideAsync(show: true, direction);
                _settings.Save();
                index = CheckedPreset();
            }
        }
        finally
        {
            _presetSwitching = false;
        }
    }

    private void PresetTab_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not RadioButton tab || !int.TryParse(tab.Tag as string, out var index)) return;
        _renaming = index;
        Grid.SetColumn(PresetRenameBox, index);
        PresetRenameBox.Text = _settings.Presets[index].Name;
        PresetRenameBox.Visibility = Visibility.Visible;
        PresetRenameBox.Focus();
        PresetRenameBox.SelectAll();
        e.Handled = true;
    }

    private void CommitRename()
    {
        if (_renaming < 0) return;
        var name = PresetRenameBox.Text.Trim();
        if (name.Length > 0)
        {
            _settings.Presets[_renaming].Name = name;
            PresetTab(_renaming).Content = name;
            _settings.Save();
        }
        _renaming = -1;
        PresetRenameBox.Visibility = Visibility.Collapsed;
    }

    private void PresetRename_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { CommitRename(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { _renaming = -1; PresetRenameBox.Visibility = Visibility.Collapsed; e.Handled = true; }
    }

    private void PresetRename_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    // ---------- locations popup (top bar) ----------

    private void Mo2Locations_Click(object sender, RoutedEventArgs e) => Mo2LocationsPopup.IsOpen = true;

    /// <summary>The button shows a dot when any location is typed, so an override can't hide.</summary>
    private void UpdateLocationsButton() =>
        Mo2LocationsButton.Content = Mo2LocationsFromBoxes() is null ? "Locations ▾" : "Locations ● ▾";

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

    // Brightness dropdowns: 11 entries, 10% .. 110% in 10% steps.
    /// <summary>The grass LOD density choices in the combo box, in percent (the same order as its items).</summary>
    private static readonly int[] GrassDensityChoices = [4, 8, 15, 25, 40, 60, 100];

    private static int GrassDensityIndex(int percent)
    {
        int best = 0;
        for (int i = 1; i < GrassDensityChoices.Length; i++)
            if (Math.Abs(GrassDensityChoices[i] - percent) < Math.Abs(GrassDensityChoices[best] - percent)) best = i;
        return best;
    }

    /// <summary>Lists 10%-120% in steps of 5 and selects the nearest to <paramref name="percent"/> (the list is only built once).</summary>
    private static void FillGrassBrightness(ComboBox box, int percent)
    {
        if (box.Items.Count == 0)
            for (int p = 10; p <= 120; p += 5) box.Items.Add(new ComboBoxItem { Content = $"{p}%", Tag = p });
        box.SelectedIndex = Math.Clamp((int)Math.Round((percent - 10) / 5.0), 0, box.Items.Count - 1);
    }

    private static int GrassBrightnessValue(ComboBox box, int fallback)
        => box.SelectedItem is ComboBoxItem { Tag: int p } ? p : fallback;

    private static int BrightnessIndex(int percent) => Math.Clamp((int)Math.Round(percent / 10.0) - 1, 0, 10);
    private static int BrightnessPercent(int index) => index < 0 ? 100 : (Math.Clamp(index, 0, 10) + 1) * 10;
}
