using System.IO;
using System.Windows;
using System.Windows.Controls;
using AnvilLOD.App.Localization;
using AnvilLOD.Core.Lod;
using Microsoft.Win32;

namespace AnvilLOD.App;

/// <summary>A rule as the list shows it.</summary>
public sealed class RuleRow(RuleEntry entry)
{
    public RuleEntry Entry { get; } = entry;
    public bool IsCustom => Entry.IsCustom;
    public string Mask => Entry.Mask;
    public string Lod4 => Shown(Entry.Lod4);
    public string Lod8 => Shown(Entry.Lod8);
    public string Lod16 => Shown(Entry.Lod16);
    public string Lod32 => Shown(Entry.Lod32);
    public string Grid => Shown(Entry.Grid);
    public string Reference => Entry.Reference;
    public string Source => Entry.IsCustom ? L.T("Editor_MyRules") : Entry.Source;
    public string FlagsText => string.Join(' ', new[]
    {
        Entry.HasFlag(RuleEntry.FlagVwd) ? "VWD" : null,
        Entry.HasFlag(RuleEntry.FlagNoGlow) ? "NoGlow" : null,
        Entry.HasFlag(RuleEntry.FlagNoMato) ? "NoMATO" : null,
        Entry.HasFlag(RuleEntry.FlagDynamic) ? "Dynamic" : null,
        Entry.HasFlag(RuleEntry.FlagTree) ? "TREE" : null,
    }.Where(s => s is not null));

    private static string Shown(string s) => string.IsNullOrWhiteSpace(s) ? L.T("Rule_None") : s;
}

/// <summary>
/// DynDOLOD's "Mesh and Reference rules" list for the chosen preset, with the user's own rules on top. Editing one of DynDOLOD's
/// rules makes a copy among the user's rules, which wins over it; the result is saved as a DynDOLOD-format rule file that
/// AnvilLOD loads before all its other rules.
/// </summary>
public partial class RulesEditorWindow : Window
{
    private readonly List<RuleEntry> _custom;
    private readonly List<RuleEntry> _defaults;
    private readonly string? _installRulesFolder;
    private readonly int _presetIndex;
    private string? _file;
    private bool _dirty;

    /// <summary>The custom rules file in use after closing (saved or loaded), or null if there is none.</summary>
    public string? ChosenFile => _file;

    public RulesEditorWindow(string? dynDolodFolder, LodPreset preset, bool candles, bool fxGlow, string? customFile, int presetIndex)
    {
        InitializeComponent();
        _presetIndex = presetIndex;
        _installRulesFolder = LodRules.FindInstallRulesFolder(dynDolodFolder);
        _defaults = LodRules.DefaultEntries(_installRulesFolder, preset, new LodRuleOptions(candles, fxGlow));
        _custom = [];
        _file = string.IsNullOrWhiteSpace(customFile) ? null : customFile;
        if (_file is not null && File.Exists(_file))
            _custom.AddRange(RuleEntry.ParseFile(File.ReadAllText(_file), L.T("Editor_MyRules"), isCustom: true));

        TitleText.Text = L.F("Editor_TitleFmt", PresetName(preset), candles ? L.T("Editor_WithCandles") : "", fxGlow ? L.T("Editor_WithFxGlow") : "");
        InfoText.Text = _installRulesFolder is null
            ? L.T("Editor_InfoNoDynDolod")
            : L.T("Editor_Info");
        Refresh();
    }

    /// <summary>The preset's localized name (the rule files themselves always use the English low/medium/high).</summary>
    private static string PresetName(LodPreset preset) => preset switch
    {
        LodPreset.Low => L.T("Preset_Low"),
        LodPreset.Medium => L.T("Preset_Medium"),
        _ => L.T("Preset_High"),
    };

    private void Refresh(RuleEntry? select = null)
    {
        select ??= Selected?.Entry;
        var overridden = new HashSet<string>(_custom.Select(c => c.Mask), StringComparer.OrdinalIgnoreCase);
        var all = _custom.Concat(_defaults.Where(d => !overridden.Contains(d.Mask))).ToList();
        var filter = FilterBox.Text.Trim();
        var rows = all.Where(e => filter.Length == 0
                || e.Mask.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(e => new RuleRow(e)).ToList();
        RuleGrid.ItemsSource = rows;
        if (select is not null && rows.FirstOrDefault(r => ReferenceEquals(r.Entry, select)) is { } row)
        {
            RuleGrid.SelectedItem = row;
            RuleGrid.ScrollIntoView(row);
        }
        CountText.Text = L.F("Editor_CountFmt", _custom.Count, all.Count - _custom.Count,
            filter.Length > 0 ? L.F("Editor_ShownFmt", rows.Count) : "");
        FileText.Text = _file is null
            ? L.T("Editor_NoFile")
            : L.F("Editor_FileFmt", _file, _dirty ? L.T("Editor_Unsaved") : "");
        UpdateButtons();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Refresh(Selected?.Entry);
    }

    private RuleRow? Selected => RuleGrid.SelectedItem as RuleRow;

    private void UpdateButtons()
    {
        var sel = Selected;
        EditButton.IsEnabled = sel is not null;
        RemoveButton.IsEnabled = sel?.IsCustom == true;
        int i = sel is { IsCustom: true } ? _custom.IndexOf(sel.Entry) : -1;
        UpButton.IsEnabled = i > 0;
        DownButton.IsEnabled = i >= 0 && i < _custom.Count - 1;
    }

    private void RuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void RuleGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: RuleRow }) Edit_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new RuleEntry { Mask = "", Lod4 = "Level0", Lod8 = "Level1", Lod16 = "Level2", Lod32 = "", Flags = RuleEntry.FlagVwd, IsCustom = true, Source = L.T("Editor_MyRules") };
        if (new RuleEditDialog(rule) { Owner = this }.ShowDialog() != true) return;
        _custom.Insert(0, rule);
        _dirty = true;
        Refresh(rule);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var copy = row.Entry.Clone();
        copy.IsCustom = true;
        copy.Source = L.T("Editor_MyRules");
        if (new RuleEditDialog(copy) { Owner = this }.ShowDialog() != true) return;
        if (row.IsCustom) _custom[_custom.IndexOf(row.Entry)] = copy;
        else _custom.Insert(0, copy); // an edited default rule becomes the user's own, and wins over the original
        _dirty = true;
        Refresh(copy);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsCustom: true } row) return;
        _custom.Remove(row.Entry);
        _dirty = true;
        Refresh();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int by)
    {
        if (Selected is not { IsCustom: true } row) return;
        int i = _custom.IndexOf(row.Entry), j = i + by;
        if (j < 0 || j >= _custom.Count) return;
        (_custom[i], _custom[j]) = (_custom[j], _custom[i]);
        _dirty = true;
        Refresh(row.Entry);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_custom.Count == 0) return;
        if (MessageBox.Show(this, L.T("Msg_ResetRules"), "AnvilLOD",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _custom.Clear();
        _dirty = true;
        Refresh();
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = L.T("Dlg_OpenRuleFile"), Filter = L.T("Filter_RuleFiles") + "|*.ini|" + L.T("Filter_AllFilesStar") + "|*.*", CheckFileExists = true };
        if (!string.IsNullOrEmpty(_file) && Directory.Exists(Path.GetDirectoryName(_file))) d.InitialDirectory = Path.GetDirectoryName(_file);
        if (d.ShowDialog(this) != true) return;
        var rules = RuleEntry.ParseFile(File.ReadAllText(d.FileName), L.T("Editor_MyRules"), isCustom: true);
        if (rules.Count == 0 && MessageBox.Show(this, L.T("Msg_NoRulesInFile"), "AnvilLOD", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _custom.Clear();
        _custom.AddRange(rules);
        _file = d.FileName;
        _dirty = false;
        Refresh();
    }

    private bool InsideDynDolodRules(string path) =>
        _installRulesFolder is not null
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(_installRulesFolder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnvilLOD", "Rules");

    private bool Save(bool asNew)
    {
        var path = _file;
        if (asNew || path is null || InsideDynDolodRules(path))
        {
            Directory.CreateDirectory(DefaultFolder);
            var d = new SaveFileDialog
            {
                Title = L.T("Dlg_SaveRules"),
                Filter = L.T("Filter_RuleFiles") + "|*.ini",
                InitialDirectory = DefaultFolder,
                FileName = Path.GetFileName(path is null || InsideDynDolodRules(path) ? L.F("Editor_DefaultFileFmt", _presetIndex + 1) : path),
                OverwritePrompt = true,
            };
            if (d.ShowDialog(this) != true) return false;
            path = d.FileName;
        }
        try
        {
            File.WriteAllText(path, RuleEntry.FormatFile(_custom));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.F("Msg_CouldNotWriteFmt", ex.Message), "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _file = path;
        _dirty = false;
        Refresh();
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => Save(true);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;
        var answer = MessageBox.Show(this, L.T("Msg_SaveChanges"), "AnvilLOD", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save(false))) e.Cancel = true;
    }
}
