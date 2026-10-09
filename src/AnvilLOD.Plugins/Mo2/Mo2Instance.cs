using System.Text.RegularExpressions;

namespace AnvilLOD.Plugins.Mo2;

/// <summary>
/// Folders the user typed in, which win over what <c>ModOrganizer.ini</c> says. Every one is optional: empty means
/// "use the ini" (the auto-detect). Typing is for lists whose game, mods or profiles live on another drive, or whose
/// ini paths are stale. With the game, mods and profiles folders typed, <c>ModOrganizer.ini</c> isn't needed at all.
/// </summary>
public sealed record Mo2Locations(string? GamePath = null, string? ModsFolder = null, string? ProfilesFolder = null, string? OverwriteFolder = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(GamePath) && string.IsNullOrWhiteSpace(ModsFolder)
                           && string.IsNullOrWhiteSpace(ProfilesFolder) && string.IsNullOrWhiteSpace(OverwriteFolder);

    /// <summary>Enough to open an instance without a ModOrganizer.ini.</summary>
    public bool CanStandAlone => !string.IsNullOrWhiteSpace(GamePath) && !string.IsNullOrWhiteSpace(ModsFolder) && !string.IsNullOrWhiteSpace(ProfilesFolder);
}

/// <summary>
/// A Mod Organizer 2 instance, read straight from disk (no VFS, no need to launch through MO2).
/// Works for portable instances (folder with ModOrganizer.ini) and global ones
/// (%LocalAppData%\ModOrganizer\&lt;name&gt;).
/// </summary>
public sealed class Mo2Instance
{
    public string InstanceFolder { get; }
    public string GamePath { get; }
    public string GameDataFolder => Path.Combine(GamePath, "Data");
    public string ModsFolder { get; }
    public string ProfilesFolder { get; }
    public string OverwriteFolder { get; }
    public string? SelectedProfile { get; }
    public string? GameName { get; }

    /// <summary>Directory names MO2 is told to ignore inside mods (Settings/skip_directories).</summary>
    public IReadOnlySet<string> SkipDirectories { get; }

    private Mo2Instance(string folder, Dictionary<string, Dictionary<string, string>> ini, Mo2Locations? typed = null)
    {
        InstanceFolder = folder;

        string? Get(string section, string key) =>
            ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        GameName = Get("General", "gameName");
        SelectedProfile = Get("General", "selected_profile");
        GamePath = Typed(typed?.GamePath) is { } typedGame
                       ? GameFolderOf(typedGame)
                       : Get("General", "gamePath")
                         ?? throw new InvalidDataException("ModOrganizer.ini has no gamePath. Type the game folder under Locations.");

        var baseDir = ExpandPath(Get("Settings", "base_directory") ?? folder, folder, folder);
        ModsFolder = Typed(typed?.ModsFolder) ?? ExpandPath(Get("Settings", "mod_directory") ?? "%BASE_DIR%/mods", baseDir, folder);
        ProfilesFolder = Typed(typed?.ProfilesFolder) ?? ExpandPath(Get("Settings", "profiles_directory") ?? "%BASE_DIR%/profiles", baseDir, folder);
        OverwriteFolder = Typed(typed?.OverwriteFolder) ?? ExpandPath(Get("Settings", "overwrite_directory") ?? "%BASE_DIR%/overwrite", baseDir, folder);

        SkipDirectories = (Get("Settings", "skip_directories") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A typed folder as a full path, or null when it was left empty (= use the ini).</summary>
    private static string? Typed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')));

    /// <summary>Accepts the game folder or its Data folder.</summary>
    private static string GameFolderOf(string typed)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(typed);
        return Path.GetFileName(trimmed).Equals("Data", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(trimmed) is { } parent
            ? parent
            : trimmed;
    }

    /// <summary>
    /// Opens an instance from its folder (the one containing ModOrganizer.ini). Folders in <paramref name="typed"/> win over
    /// the ini; with the game, mods and profiles folders typed the ini isn't needed.
    /// </summary>
    public static Mo2Instance Open(string instanceFolder, Mo2Locations? typed = null)
    {
        var iniPath = Path.Combine(instanceFolder, "ModOrganizer.ini");
        Dictionary<string, Dictionary<string, string>> ini;
        if (File.Exists(iniPath)) ini = ReadIni(iniPath);
        else if (typed is { CanStandAlone: true }) ini = new(StringComparer.OrdinalIgnoreCase);
        else
            throw new FileNotFoundException($"No ModOrganizer.ini in \"{instanceFolder}\". Pick the MO2 instance folder (where ModOrganizer.exe or ModOrganizer.ini lives), "
                                            + "or type the game, mods and profiles folders under Locations.");
        var instance = new Mo2Instance(Path.GetFullPath(instanceFolder), ini, typed);
        if (!Directory.Exists(instance.GameDataFolder))
            throw new DirectoryNotFoundException($"The game folder \"{instance.GamePath}\" has no Data folder. Type the folder that holds SkyrimSE.exe under Locations.");
        return instance;
    }

    public IReadOnlyList<string> ListProfiles() =>
        Directory.Exists(ProfilesFolder)
            ? Directory.EnumerateDirectories(ProfilesFolder)
                .Where(d => File.Exists(Path.Combine(d, "modlist.txt")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public Mo2Profile OpenProfile(string? name = null)
    {
        name ??= SelectedProfile ?? throw new InvalidDataException("No profile selected in ModOrganizer.ini; choose one.");
        return Mo2Profile.Open(this, name);
    }

    // ---------- ini parsing (Qt QSettings flavour) ----------

    internal static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = result[""] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1];
                if (!result.TryGetValue(name, out section!))
                    section = result[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            section[line[..eq].Trim()] = DecodeQtValue(line[(eq + 1)..].Trim());
        }
        return result;
    }

    /// <summary>Strips @ByteArray(...) / quotes and un-escapes \\ as QSettings writes them.</summary>
    internal static string DecodeQtValue(string v)
    {
        var m = Regex.Match(v, @"^@ByteArray\((.*)\)$");
        if (m.Success) v = m.Groups[1].Value;
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
        return v.Replace(@"\\", @"\");
    }

    internal static string ExpandPath(string value, string baseDir, string instanceFolder)
    {
        var v = value.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase)
                     .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(instanceFolder, v));
    }
}
