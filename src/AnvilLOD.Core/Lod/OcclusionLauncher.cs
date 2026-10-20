using System.Globalization;

namespace AnvilLOD.Core.Lod;

/// <summary>The program AnvilLOD starts to make the occlusion data (Occlusion.esp).</summary>
public enum OcclusionTool
{
    XLodGen,
    DynDolod,
}

/// <summary>What <see cref="OcclusionLauncher.ImportResult"/> did.</summary>
public sealed record OcclusionImport(string Source, string Target, long Bytes, string? BackupOfOld);

/// <summary>A plugin an occlusion run wrote; <c>LeftInGameFolders</c> = it was saved to Overwrite/Data, not to the run folder.</summary>
public sealed record OcclusionFound(string Path, bool LeftInGameFolders);

/// <summary>
/// Occlusion.esp holds the CELL records' TVDT/MHDT data (which LOD is hidden behind terrain). AnvilLOD does not compute it itself yet:
/// it starts the occlusion run of xLODGen or DynDOLOD that the user has installed, with the folders AnvilLOD already knows, and
/// copies the plugin that run wrote into the LOD output. Nothing of those programs is bundled or copied.
/// </summary>
public static class OcclusionLauncher
{
    public const string PluginName = "Occlusion.esp";

    public static string ExeName(OcclusionTool tool) => tool == OcclusionTool.XLodGen ? "xLODGenx64.exe" : "DynDOLODx64.exe";

    public static string ToolName(OcclusionTool tool) => tool == OcclusionTool.XLodGen ? "xLODGen" : "DynDOLOD";

    public static string? FindExe(OcclusionTool tool, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var path = Path.Combine(folder.Trim(), ExeName(tool));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Where the tool most likely is: next to the DynDOLOD folder (tools\DynDOLOD and tools\xLODGen are siblings in a usual MO2 setup),
    /// else in the MO2 instance's tools folder. Null when no folder with the program is found.
    /// </summary>
    public static string? GuessFolder(OcclusionTool tool, string? dynDolodFolder, string? mo2InstanceFolder)
    {
        var name = tool == OcclusionTool.XLodGen ? "xLODGen" : "DynDOLOD";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(dynDolodFolder))
        {
            var dd = dynDolodFolder.Trim().TrimEnd('\\', '/');
            if (tool == OcclusionTool.DynDolod) candidates.Add(dd);
            var parent = Path.GetDirectoryName(dd);
            if (parent is not null) candidates.Add(Path.Combine(parent, name));
        }
        if (!string.IsNullOrWhiteSpace(mo2InstanceFolder)) candidates.Add(Path.Combine(mo2InstanceFolder.Trim(), "tools", name));
        return candidates.FirstOrDefault(c => FindExe(tool, c) is not null);
    }

    /// <summary>
    /// The command line, as separate arguments: Skyrim SE, the real Data folder (the tool sees the mods through MO2's virtual folder when
    /// it is started from MO2) and an output folder of its own, so nothing lands in the game or the mod manager's folders.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string dataFolder, string outputFolder) =>
    [
        "-sse",
        "-D:" + dataFolder.Trim().TrimEnd('\\', '/'),
        "-o:" + outputFolder.Trim().TrimEnd('\\', '/'),
    ];

    /// <summary>
    /// How many threads may read object LOD meshes at once for a PC with this much RAM. Unlimited threads took over 80 GB of committed memory
    /// on a 64 GB PC with 3D tree LOD, so this allows about one thread per 24 GB (1 to 4). An estimate: the page logs the peak of every run.
    /// </summary>
    public static int SuggestedThreads(long installedRamBytes)
    {
        var gb = installedRamBytes / (1024.0 * 1024 * 1024);
        return Math.Clamp((int)Math.Floor((gb + 2.0) / 24.0), 1, 4);   // +2: a "48 GB" PC reports a little under 48
    }

    /// <summary>A new, empty folder for one run, under the user's local app data.</summary>
    public static string NewRunFolder(OcclusionTool tool, DateTime? now = null)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnvilLOD", "occlusion", ToolName(tool));
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var folder = Path.Combine(root, "run-" + stamp);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The Occlusion.esp a run wrote. xLODGen and DynDOLOD put LOD files into the -o: folder but save plugins into the Data folder, which
    /// under MO2 ends up in the Overwrite folder. So: the run folder first (or one level down), then the extra folders (Overwrite, Data),
    /// where only a file written since <paramref name="since"/> counts, never an older one that was already there.
    /// </summary>
    public static OcclusionFound? FindResult(string runFolder, IEnumerable<string?>? alsoLook = null, DateTime? since = null)
    {
        if (Directory.Exists(runFolder))
        {
            var direct = Path.Combine(runFolder, PluginName);
            if (File.Exists(direct)) return new OcclusionFound(direct, false);
            var deeper = Directory.EnumerateFiles(runFolder, PluginName, SearchOption.AllDirectories).FirstOrDefault();
            if (deeper is not null) return new OcclusionFound(deeper, false);
        }
        foreach (var folder in alsoLook ?? [])
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var path = Path.Combine(folder.Trim(), PluginName);
            if (File.Exists(path) && (since is null || File.GetLastWriteTime(path) >= since)) return new OcclusionFound(path, true);
        }
        return null;
    }

    /// <summary>
    /// Copies the plugin into the LOD output folder (moves it when it was left in Overwrite/Data). An Occlusion.esp already there is kept as <c>Occlusion.esp.bak_&lt;stamp&gt;</c>, never lost.
    /// </summary>
    public static OcclusionImport ImportResult(string source, string outputFolder, DateTime? now = null, bool move = false)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The occlusion run left no " + PluginName, source);
        Directory.CreateDirectory(outputFolder);
        var target = Path.Combine(outputFolder, PluginName);
        string? backup = null;
        if (File.Exists(target))
        {
            backup = target + ".bak_" + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(target, backup);
        }
        if (move) File.Move(source, target);   // a plugin saved to Overwrite/Data would otherwise stay there and load twice
        else File.Copy(source, target);
        return new OcclusionImport(source, target, new FileInfo(target).Length, backup);
    }

    /// <summary>The occlusion lines of a DynDOLOD_SSE.ini (read only): Height, Radius, Mode and the per-worldspace overrides.</summary>
    public static IReadOnlyList<string> ReadIniSummary(string? dynDolodFolder)
    {
        if (string.IsNullOrWhiteSpace(dynDolodFolder)) return [];
        var ini = Path.Combine(dynDolodFolder.Trim(), "Edit Scripts", "DynDOLOD", "DynDOLOD_SSE.ini");
        if (!File.Exists(ini)) return [];
        var lines = new List<string>();
        foreach (var raw in File.ReadLines(ini))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            var key = line.Split('=', 2)[0];
            if (key is "OcclusionHeight" or "OcclusionRadius" or "OcclusionMode"
                || key.StartsWith("OcclusionMode_", StringComparison.Ordinal) || key.StartsWith("Occlusion_", StringComparison.Ordinal))
                lines.Add(line);
        }
        return lines;
    }
}
