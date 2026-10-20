using System.IO;
using System.Windows;
using AnvilLOD.Core.Textures;
using AnvilLOD.Plugins.Mo2;
using Microsoft.Win32;

namespace AnvilLOD.App;

// The Texture fixer page: finds textures Skyrim SE cannot load (BC6H) in the folders you pick or in every enabled mod of the MO2 profile,
// and converts them to BC7 into a new mod, a folder, or in place (with a backup of every original).
public partial class MainWindow
{
    private sealed record TexRow(string Mod, string File, string Format, string Size, string Status);

    private IReadOnlyList<BadTexture> _texBad = [];
    private string? _texOutput;

    private void InitTextures()
    {
        TexInputBox.Text = _settings.TexFixInputs ?? "";
        TexAllModsCheck.IsChecked = _settings.TexFixAllMods;
        TexModNameBox.Text = _settings.TexFixModName ?? "AnvilLOD Texture Fixes";
        TexOutBox.Text = _settings.TexFixOutput ?? "";
        TexTexconvBox.Text = _settings.TexconvPath ?? "";
        switch (_settings.TexFixMode)
        {
            case 1: TexFolderRadio.IsChecked = true; break;
            case 2: TexInPlaceRadio.IsChecked = true; break;
            default: TexNewModRadio.IsChecked = true; break;
        }
        TexOutMode_Changed(this, new RoutedEventArgs());
    }

    private void TexOutMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TexModNameBox is null || TexOutBox is null) return;   // fires while the window is being built
        TexModNameBox.IsEnabled = TexNewModRadio.IsChecked == true;
        TexOutBox.IsEnabled = TexFolderRadio.IsChecked == true;
    }

    private void TexAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select a mod folder (or any folder with textures)", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        var lines = TexInputBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var f in d.FolderNames)
            if (!lines.Contains(f, StringComparer.OrdinalIgnoreCase)) lines.Add(f);
        TexInputBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void TexClear_Click(object sender, RoutedEventArgs e)
    {
        TexInputBox.Clear();
        TexAllModsCheck.IsChecked = false;
    }

    private void TexBrowseTexconv_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select texconv.exe", Filter = "texconv|texconv*.exe|Programs|*.exe" };
        if (d.ShowDialog(this) == true) TexTexconvBox.Text = d.FileName;
    }

    private void TexBrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Select the folder for the converted textures" };
        if (Directory.Exists(TexOutBox.Text)) d.InitialDirectory = TexOutBox.Text;
        if (d.ShowDialog(this) == true) TexOutBox.Text = d.FolderName;
    }

    private void TexOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_texOutput is not null && Directory.Exists(_texOutput))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_texOutput) { UseShellExecute = true });
    }

    private void CaptureTexSettings()
    {
        _settings.TexFixInputs = Blank(TexInputBox.Text);
        _settings.TexFixAllMods = TexAllModsCheck.IsChecked == true;
        _settings.TexFixMode = TexInPlaceRadio.IsChecked == true ? 2 : TexFolderRadio.IsChecked == true ? 1 : 0;
        _settings.TexFixModName = Blank(TexModNameBox.Text);
        _settings.TexFixOutput = Blank(TexOutBox.Text);
        _settings.TexconvPath = Blank(TexTexconvBox.Text);
    }

    /// <summary>The folders to scan: the ones typed in, plus every enabled mod of the MO2 profile when that box is ticked. Null (after a message) when it can't be done.</summary>
    private List<string>? TexFoldersToScan()
    {
        var folders = TexInputBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (TexAllModsCheck.IsChecked == true)
        {
            if (!_settings.UseMo2 || _settings.Mo2Instance is null)
            {
                MessageBox.Show(this, "\"All enabled mods\" needs MO2 instance mode (top bar) to know which mods are enabled.", "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            try { folders.AddRange(Mo2Instance.Open(_settings.Mo2Instance, TypedLocations()).OpenProfile(_settings.Mo2Profile).EnabledModFolders()); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }
        if (folders.Count == 0)
        {
            MessageBox.Show(this, "Add a mod folder to check, or tick \"All enabled mods\".", "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return folders;
    }

    private string? FindTexconvPath()
    {
        var instance = _settings.Mo2Instance;
        return TextureFixer.FindTexconv([
            TexTexconvBox.Text, _settings.XLodGenFolder, _settings.DynDolodFolder,
            instance is null ? null : Path.Combine(instance, "tools"),
            instance is null ? null : Path.Combine(Path.GetDirectoryName(instance.TrimEnd('\\', '/')) ?? instance, "tools"),
            Environment.GetEnvironmentVariable("TEXCONV"),
        ]);
    }

    private void TexScan_Click(object sender, RoutedEventArgs e) => _ = RunTexScanAsync();

    private async Task RunTexScanAsync()
    {
        CaptureSettings();
        CaptureTexSettings();
        _settings.Save();
        var folders = TexFoldersToScan();
        if (folders is null) return;

        SetBusy(true);
        TexScanButton.IsEnabled = TexFixButton.IsEnabled = false;
        LogBox.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(m =>
        {
            ProgressText.Text = m;
            TexSummaryText.Text = m;
            LogBox.AppendText($"[{_elapsed.Elapsed:mm\\:ss\\.f}] {m}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
        try
        {
            _texBad = await Task.Run(() => TextureFixer.Scan(folders, progress, ct), ct);
            ShowTexRows(_texBad.Select(b => new TexRow(b.Mod, b.Relative, b.Format, $"{b.Width}x{b.Height}",
                b.Fixable ? "Can be converted" : "Cannot be converted here")).ToList());
            int fixable = _texBad.Count(b => b.Fixable);
            TexSummaryText.Text = _texBad.Count == 0
                ? $"None of the textures in {folders.Count:N0} folder(s) is in a format the game cannot load."
                : $"{_texBad.Count:N0} texture(s) the game cannot load, in {_texBad.Select(b => b.Mod).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} mod(s); {fixable:N0} can be converted to BC7.";
            StatusText.Text = "Texture scan finished";
            ProgressText.Text = "Done";
            TexFixButton.IsEnabled = fixable > 0;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed — see Log";
            TexSummaryText.Text = ex.Message;
            LogBox.AppendText(Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
        }
        finally
        {
            SetBusy(false);
            TexScanButton.IsEnabled = true;
            TexFixButton.IsEnabled = _texBad.Any(b => b.Fixable);
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowTexRows(List<TexRow> rows) => TexGrid.ItemsSource = rows;

    private void TexFix_Click(object sender, RoutedEventArgs e) => _ = RunTexFixAsync();

    private async Task RunTexFixAsync()
    {
        CaptureSettings();
        CaptureTexSettings();
        _settings.Save();
        var todo = _texBad.Where(b => b.Fixable).ToList();
        if (todo.Count == 0) return;

        var texconv = FindTexconvPath();
        if (texconv is null)
        {
            MessageBox.Show(this, "texconv.exe was not found. Choose it (the \"…\" button): xEdit, xLODGen and DynDOLOD have it in their Edit Scripts folder, e.g. SSEEdit\\Edit Scripts\\Texconvx64.exe.",
                "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? output = null, backup = null;
        if (TexInPlaceRadio.IsChecked == true)
        {
            backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnvilLOD", "texture-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            if (MessageBox.Show(this, $"{todo.Count:N0} texture(s) will be replaced where they are, in {todo.Select(b => b.Mod).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} mod(s).\n\n"
                    + $"Every original is copied to:\n{backup}\n\nContinue?", "Texture fixer", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        else if (TexFolderRadio.IsChecked == true)
        {
            if (Blank(TexOutBox.Text) is not { } typed)
            {
                MessageBox.Show(this, "Choose the output folder.", "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            output = typed;
        }
        else
        {
            if (Blank(TexModNameBox.Text) is not { } name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "Give the new mod a name (no \\ / : * ? \" < > |).", "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_settings.UseMo2 || _settings.Mo2Instance is null)
            {
                MessageBox.Show(this, "A new mod needs MO2 instance mode (to find your mods folder). Choose \"A folder I choose\" instead, or switch the source to MO2 instance.", "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { output = Path.Combine(Mo2Instance.Open(_settings.Mo2Instance, TypedLocations()).ModsFolder, name); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        SetBusy(true);
        TexScanButton.IsEnabled = TexFixButton.IsEnabled = false;
        LogBox.Clear();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var progress = new Progress<string>(m =>
        {
            ProgressText.Text = m;
            TexSummaryText.Text = m;
            LogBox.AppendText($"[{_elapsed.Elapsed:mm\\:ss\\.f}] {m}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        });
        try
        {
            var results = await Task.Run(() => TextureFixer.Fix(todo, texconv, output, backup, dryRun: false, progress, ct), ct);
            int ok = results.Count(r => r.Status.StartsWith("Converted", StringComparison.Ordinal));
            ShowTexRows(results.Select(r => new TexRow(r.Source.Mod, r.Source.Relative, r.Source.Format, $"{r.Source.Width}x{r.Source.Height}", r.Status)).ToList());
            TexSummaryText.Text = $"{ok:N0} of {results.Count:N0} converted to BC7"
                + (output is not null ? $" into {output}. Enable that mod above the originals in MO2." : $". The originals are in {backup}.")
                + (ok < results.Count ? " Some failed: see the Result column." : "");
            _texOutput = output ?? backup;
            TexOpenButton.IsEnabled = _texOutput is not null;
            StatusText.Text = "Texture fixer finished";
            ProgressText.Text = "Done";
            _texBad = _texBad.Where(b => results.All(r => r.Source != b || !r.Status.StartsWith("Converted", StringComparison.Ordinal) || output is not null)).ToList();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed — see Log";
            TexSummaryText.Text = ex.Message;
            LogBox.AppendText(Environment.NewLine + "ERROR: " + ex + Environment.NewLine);
            MessageBox.Show(this, ex.Message, "Texture fixer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            TexScanButton.IsEnabled = true;
            TexFixButton.IsEnabled = _texBad.Any(b => b.Fixable);
            _cts.Dispose();
            _cts = null;
        }
    }
}
