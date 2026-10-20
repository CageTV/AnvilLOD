using System.Diagnostics;
using System.IO;
using System.Windows;
using AnvilLOD.Core.Lod;
using AnvilLOD.Plugins;
using AnvilLOD.Plugins.Mo2;
using Microsoft.Win32;

namespace AnvilLOD.App;

// The Occlusion page: starts xLODGen's or DynDOLOD's occlusion run with the game's folders and brings the Occlusion.esp it writes into the LOD output.
public partial class MainWindow
{
    private string? _occRunFolder;
    private int _occToolShown;   // the tool whose folder the box shows

    private OcclusionTool OccTool => OccToolBox.SelectedIndex switch { 1 => OcclusionTool.DynDolod, 2 => OcclusionTool.Builtin, _ => OcclusionTool.XLodGen };

    private static readonly int[] ClearanceChoices = [4096, 3072, 2048, 1024];

    private bool _occLoading;   // filling the page from the settings: the change handlers must not write back

    private void ApplyOcclusionSettings()
    {
        _occLoading = true;
        _occToolShown = Math.Clamp(_settings.OcclusionTool, 0, 2);
        OccToolBox.SelectedIndex = _occToolShown;
        OccModeBox.ItemsSource = XLodGenSettings.Modes;
        OccModeBox.SelectedItem = XLodGenSettings.Modes.Contains(_settings.OccMode) ? _settings.OccMode : "-Flat +Border";
        OccQualityBox.SelectedIndex = Math.Clamp(_settings.OccQuality, 1, 3) - 1;
        OccThreadsBox.SelectedIndex = _settings.OccThreads switch { 0 => 0, 1 => 1, 2 => 2, 4 => 3, _ => 4 };
        OccHeightBox.Text = _settings.OccHeight.ToString();
        OccRadiusBox.Text = _settings.OccRadius.ToString();
        OccPresetCheck.IsChecked = _settings.OccPreset;
        OccClearanceBox.SelectedIndex = Math.Max(0, Array.IndexOf(ClearanceChoices, _settings.OccClearance));
        OccAutoCloseCheck.IsChecked = _settings.OccAutoClose;
        OccAfterGenerateCheck.IsChecked = _settings.OccAfterGenerate;
        ShowOccFolder();
        _occLoading = false;
    }

    private void CaptureOcclusionSettings()
    {
        if (OccFolderBox is null || OccToolBox is null || _occLoading) return;
        _settings.OcclusionTool = Math.Clamp(OccToolBox.SelectedIndex, 0, 2);
        _settings.OccClearance = ClearanceChoices[Math.Clamp(OccClearanceBox.SelectedIndex, 0, ClearanceChoices.Length - 1)];
        StoreOccFolder(OccToolBox.SelectedIndex);
        _settings.OccPreset = OccPresetCheck.IsChecked == true;
        _settings.OccAutoClose = OccAutoCloseCheck.IsChecked == true;
        _settings.OccAfterGenerate = OccAfterGenerateCheck.IsChecked == true;
        _settings.OccQuality = Math.Clamp(OccQualityBox.SelectedIndex + 1, 1, 3);
        _settings.OccThreads = OccThreadsBox.SelectedIndex switch { 0 => 0, 1 => 1, 2 => 2, 3 => 4, _ => -1 };
        _settings.OccHeight = int.TryParse(OccHeightBox.Text.Trim(), out var h) ? Math.Max(0, h) : 0;
        _settings.OccRadius = int.TryParse(OccRadiusBox.Text.Trim(), out var r) ? Math.Max(1, r) : 100;
        _settings.OccMode = OccModeBox.SelectedItem as string ?? "-Flat +Border";
    }

    private string? OccStoredFolder(int tool) => tool == 1 ? Blank(DynDolodBox.Text) : _settings.XLodGenFolder;

    private void StoreOccFolder(int tool)
    {
        if (tool == 2) return;   // the built-in calculation has no folder
        if (tool == 1) DynDolodBox.Text = OccFolderBox.Text.Trim();   // DynDOLOD's folder is the one on the LOD rules page
        else _settings.XLodGenFolder = Blank(OccFolderBox.Text);
    }

    private void ShowOccFolder()
    {
        var tool = OccTool;
        var stored = OccStoredFolder(OccToolBox.SelectedIndex);
        stored ??= OcclusionLauncher.GuessFolder(tool, Blank(DynDolodBox.Text), _settings.UseMo2 ? Blank(Mo2Box.Text) : null);
        OccFolderBox.Text = tool == OcclusionTool.Builtin ? "" : stored ?? "";
        OccFolderBox.IsEnabled = tool != OcclusionTool.Builtin;
        OccFolderLabel.Text = tool == OcclusionTool.Builtin ? "No other program is needed" : OcclusionLauncher.ToolName(tool) + " folder";
        UpdateOccInfo();
    }

    private void OccTool_Changed(object sender, RoutedEventArgs e)
    {
        if (OccFolderBox is null || OccInfoText is null || _occLoading) return;
        StoreOccFolder(_occToolShown);   // keep what was typed for the tool that was showing
        _occToolShown = OccToolBox.SelectedIndex;
        ShowOccFolder();
    }

    private void OccPreset_Changed(object sender, RoutedEventArgs e)
    {
        if (OccModeBox is null || OccInfoText is null) return;
        UpdateOccInfo();
    }

    private void OccFolder_LostFocus(object sender, RoutedEventArgs e)
    {
        StoreOccFolder(OccToolBox.SelectedIndex);
        UpdateOccInfo();
    }

    private void BrowseOccFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = $"Select the {OcclusionLauncher.ToolName(OccTool)} folder (contains {OcclusionLauncher.ExeName(OccTool)})" };
        if (Directory.Exists(OccFolderBox.Text)) d.InitialDirectory = OccFolderBox.Text;
        if (d.ShowDialog(this) != true) return;
        OccFolderBox.Text = d.FolderName;
        OccFolder_LostFocus(sender, e);
    }

    private void UpdateOccInfo()
    {
        var tool = OccTool;
        OccBuiltinPanel.Visibility = tool == OcclusionTool.Builtin ? Visibility.Visible : Visibility.Collapsed;
        if (tool == OcclusionTool.Builtin)
        {
            OccInfoText.Text = "Runs inside AnvilLOD: it reads your landscape and the objects and trees of your load order, then writes Occlusion.esp into the LOD output.";
            OccStartButton.IsEnabled = true;
            OccOptionsPanel.Visibility = Visibility.Collapsed;
            return;
        }
        OccOptionsPanel.Visibility = Visibility.Visible;
        var exe = OcclusionLauncher.FindExe(tool, Blank(OccFolderBox.Text));
        var text = exe is null
            ? $"{OcclusionLauncher.ExeName(tool)} was not found in that folder."
            : $"Found {exe}.";
        if (tool == OcclusionTool.DynDolod)
        {
            var ini = OcclusionLauncher.ReadIniSummary(Blank(OccFolderBox.Text));
            if (ini.Count > 0) text += "\nYour DynDOLOD_SSE.ini (not changed by AnvilLOD): " + string.Join(";  ", ini);
        }
        OccInfoText.Text = text;
        OccStartButton.IsEnabled = exe is not null;
        OccOptionsPanel.IsEnabled = tool == OcclusionTool.XLodGen;
        OccModeBox.IsEnabled = OccPresetCheck.IsChecked == true;
        OccQualityBox.IsEnabled = OccHeightBox.IsEnabled = OccRadiusBox.IsEnabled = OccThreadsBox.IsEnabled = OccModeBox.IsEnabled;
    }

    private void OccOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_occRunFolder is { } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async void OccStart_Click(object sender, RoutedEventArgs e) => await RunOcclusionAsync(automatic: false);

    /// <summary>Starts the occlusion program and brings its Occlusion.esp into the output. <paramref name="automatic"/>: it follows a Generate, so nothing is asked that Generate already asked.</summary>
    private async Task RunOcclusionAsync(bool automatic)
    {
        CaptureSettings();
        var tool = OccTool;
        if (tool == OcclusionTool.Builtin)
        {
            if (automatic) return;   // it already ran inside Generate
            if (_settings.OutputFolder is null)
            {
                OutputBox.Text = OutputFolder.DefaultFolder(BuildGameOptions());
                CaptureSettings();
            }
            OccSummaryText.Text = "Working out the occlusion data (see the Log tab)...";
            await RunAsync(generate: false, occlusionOnly: true);
            OccSummaryText.Text = File.Exists(Path.Combine(_settings.OutputFolder!, OcclusionLauncher.PluginName)) ? $"Occlusion.esp is in {_settings.OutputFolder}. Put it last in your plugin order." : "No Occlusion.esp was written: see the Log tab.";
            return;
        }
        var exe = OcclusionLauncher.FindExe(tool, Blank(OccFolderBox.Text));
        if (exe is null)
        {
            if (automatic)
            {
                OccSummaryText.Text = $"Occlusion was not run: {OcclusionLauncher.ExeName(tool)} was not found. Set its folder on the Occlusion page (or untick the automatic run).";
                LogBox.AppendText($"{Environment.NewLine}Occlusion was not run: {OcclusionLauncher.ExeName(tool)} was not found (Occlusion page).{Environment.NewLine}");
                return;
            }
            MessageBox.Show(this, $"Pick the folder that contains {OcclusionLauncher.ExeName(tool)}.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dataFolder;
        try
        {
            dataFolder = _settings.UseMo2
                ? Mo2Instance.Open(_settings.Mo2Instance ?? throw new InvalidOperationException("Choose your MO2 instance folder first."), TypedLocations()).GameDataFolder
                : _settings.DataFolder ?? throw new InvalidOperationException("Enter the game's Data folder first (top bar).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (automatic && _settings.UseMo2 && !StartupLog.IsUnderMo2())
        {
            // Started on its own outside MO2 the program would see only the game's own files and write an occlusion file for the wrong world: don't.
            OccSummaryText.Text = "Occlusion was not run: AnvilLOD was not started from MO2, so the program would not see your mods. Start AnvilLOD from MO2, or run it from the Occlusion page.";
            LogBox.AppendText($"{Environment.NewLine}Occlusion was not run: AnvilLOD was not started from MO2.{Environment.NewLine}");
            return;
        }
        if (_settings.UseMo2 && !StartupLog.IsUnderMo2() && !automatic
            && MessageBox.Show(this,
                $"AnvilLOD was not started from MO2, so {OcclusionLauncher.ToolName(tool)} would only see the game's own files, not your mods. Add AnvilLOD to MO2's executables and start it from there.\n\nStart it anyway?",
                "Occlusion", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var output = _settings.OutputFolder ?? OutputFolder.DefaultFolder(BuildGameOptions());
        var run = OcclusionLauncher.NewRunFolder(tool);
        _occRunFolder = run;
        OccOpenButton.IsEnabled = true;

        var presetNote = "";
        var occThreads = 0;
        if (tool == OcclusionTool.XLodGen && _settings.OccPreset)
        {
            try
            {
                var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var threads = _settings.OccThreads == 0 ? OcclusionLauncher.SuggestedThreads(ram) : _settings.OccThreads;
                occThreads = threads;
                var set = XLodGenSettings.ApplyToFile(XLodGenSettings.DefaultPath(),
                    new OcclusionOptions(_settings.OccQuality, _settings.OccHeight, _settings.OccRadius, _settings.OccMode, threads));
                presetNote = set ? $" Options set: quality {_settings.OccQuality}, height {_settings.OccHeight}, radius {_settings.OccRadius}, mode {_settings.OccMode}, {(occThreads < 1 ? "unlimited" : occThreads)} object LOD threads."
                    : " xLODGen has not saved any settings yet, so its window opens with its defaults: tick only Occlusion yourself this time.";
            }
            catch (Exception ex)
            {
                presetNote = " Could not set the options (" + ex.Message + "): set them in the window.";
            }
        }

        var started = DateTime.Now;
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        // xLODGen reads LOD files from its -o: folder before the ones in the Data folder, so pointing it at the AnvilLOD output lets it
        // see LOD that MO2's virtual folder does not hold yet. It writes no LOD files in an occlusion-only run, and the plugin goes to Data/Overwrite.
        var outArg = tool == OcclusionTool.XLodGen && Directory.Exists(output) ? output : run;
        foreach (var a in OcclusionLauncher.Arguments(dataFolder, outArg)) psi.ArgumentList.Add(a);

        OccStartButton.IsEnabled = false;
        OccSummaryText.Text = $"{OcclusionLauncher.ToolName(tool)} is running. Pick the worldspaces, then close it when it has finished." + presetNote;
        StatusText.Text = $"{OcclusionLauncher.ToolName(tool)} is running…";
        double peakGb = 0;
        try
        {
            var logPath = tool == OcclusionTool.XLodGen ? XLodGenLog.PathFor(Path.GetDirectoryName(exe)!) : null;
            var logOffset = logPath is null ? 0 : XLodGenLog.Length(logPath);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("The program did not start.");
            var exited = proc.WaitForExitAsync();
            var announced = false;
            var seenWorking = false;   // xEdit tools show "[Generating LOD] ..." in the window title while they work, and their normal title when done
            var idlePolls = 0;
            long peak = 0;
            while (!exited.IsCompleted)
            {
                await Task.WhenAny(exited, Task.Delay(2000));
                try { proc.Refresh(); peak = Math.Max(peak, proc.PrivateMemorySize64); }
                catch { /* it just ended */ }
                peakGb = peak / 1073741824.0;
                if (exited.IsCompleted || announced || logPath is null) continue;
                bool finished;
                try { finished = XLodGenLog.Scan(logPath, ref logOffset); }
                catch (IOException) { finished = false; }   // the program has the log open for writing at that moment
                // The log is only written out when the program closes, so the window title is the live signal.
                string title;
                try { title = proc.MainWindowTitle; }
                catch { title = ""; }
                if (title.Contains("[Generating", StringComparison.OrdinalIgnoreCase)) { seenWorking = true; idlePolls = 0; }
                else if (seenWorking && title.Length > 0 && ++idlePolls >= 2) finished = true;
                if (!finished) continue;
                announced = true;
                if (_settings.OccAutoClose)
                {
                    OccSummaryText.Text = "xLODGen says it has finished. Closing it: confirm that Occlusion.esp is saved." + presetNote;
                    StatusText.Text = "xLODGen finished: confirm the save";
                    await Task.Delay(1500);
                    if (!proc.HasExited) proc.CloseMainWindow();
                }
                else
                {
                    OccSummaryText.Text = "xLODGen says it has finished. Close it and confirm that Occlusion.esp is saved." + presetNote;
                    StatusText.Text = "xLODGen finished: close it";
                }
            }
        }
        catch (Exception ex)
        {
            OccSummaryText.Text = "Could not run it: " + ex.Message;
            StatusText.Text = "Occlusion: failed to start";
            UpdateOccInfo();
            return;
        }

        UpdateOccInfo();
        if (peakGb > 0)
            LogBox.AppendText($"{Environment.NewLine}Occlusion run: peak committed memory {peakGb:0.0} GB on a PC with {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB RAM, object LOD threads {(occThreads < 1 ? "unlimited" : occThreads)}.{Environment.NewLine}");
        // The tools save plugins into the Data folder, not into -o:. Under MO2 that lands in Overwrite.
        string? overwrite = null;
        try
        {
            if (_settings.UseMo2 && _settings.Mo2Instance is { } inst) overwrite = Mo2Instance.Open(inst, TypedLocations()).OverwriteFolder;
        }
        catch
        {
            // the lookup is a convenience: the run folder and the Data folder are still searched
        }
        var found = OcclusionLauncher.FindResult(run, [overwrite, dataFolder], started.AddSeconds(-5));
        if (found is null)
        {
            OccSummaryText.Text = $"The run left no Occlusion.esp (nothing was selected, or it was closed early). Looked in {run}" + (overwrite is null ? "" : $" and in Overwrite ({overwrite})") + ".";
            StatusText.Text = "Occlusion: no Occlusion.esp written";
            return;
        }

        try
        {
            var done = OcclusionLauncher.ImportResult(found.Path, output, move: found.LeftInGameFolders);
            OccSummaryText.Text = $"Occlusion.esp ({done.Bytes / 1048576.0:0.0} MB) {(found.LeftInGameFolders ? "moved out of Overwrite" : "copied")} to {done.Target}."
                + (done.BackupOfOld is { } bak ? $" The earlier one is kept as {Path.GetFileName(bak)}." : "")
                + " Put it last in your plugin order.";
            StatusText.Text = "Occlusion.esp is in the LOD output";
        }
        catch (Exception ex)
        {
            OccSummaryText.Text = $"Could not copy it into the output: {ex.Message}. The file is at {found.Path}.";
            StatusText.Text = "Occlusion: copy failed";
        }
    }
}
