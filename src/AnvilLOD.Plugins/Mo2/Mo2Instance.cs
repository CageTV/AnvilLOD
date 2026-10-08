using System.Text.RegularExpressions;

namespace AnvilLOD.Plugins.Mo2;

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

    private Mo2Instance(string folder, Dictionary<string, Dictionary<string, string>> ini)
    {
        InstanceFolder = folder;

        string? Get(string section, string key) =>
            ini.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        GameName = Get("General", "gameName");
        SelectedProfile = Get("General", "selected_profile");
        GamePath = Get("General", "gamePath")
                   ?? throw new InvalidDataException("ModOrganizer.ini has no gamePath.");

        var baseDir = ExpandPath(Get("Settings", "base_directory") ?? folder, folder, folder);
        ModsFolder = ExpandPath(Get("Settings", "mod_directory") ?? "%BASE_DIR%/mods", baseDir, folder);
        ProfilesFolder = ExpandPath(Get("Settings", "profiles_directory") ?? "%BASE_DIR%/profiles", baseDir, folder);
        OverwriteFolder = ExpandPath(Get("Settings", "overwrite_directory") ?? "%BASE_DIR%/overwrite", baseDir, folder);

        SkipDirectories = (Get("Settings", "skip_directories") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Opens an instance from its folder (the one containing ModOrganizer.ini).</summary>
    public static Mo2Instance Open(string instanceFolder)
    {
        var iniPath = Path.Combine(instanceFolder, "ModOrganizer.ini");
        if (!File.Exists(iniPath))
            throw new FileNotFoundException($"No ModOrganizer.ini in \"{instanceFolder}\". Pick the MO2 instance folder (where ModOrganizer.exe or ModOrganizer.ini lives).");
        return new Mo2Instance(Path.GetFullPath(instanceFolder), ReadIni(iniPath));
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
