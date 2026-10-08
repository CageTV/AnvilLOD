namespace AnvilLOD.Plugins.Mo2;

/// <summary>
/// One MO2 profile: which mods are enabled and in what priority, the plugin load order,
/// and the INI archive list the game would use.
/// </summary>
public sealed class Mo2Profile
{
    public Mo2Instance Instance { get; }
    public string Name { get; }
    public string Folder { get; }

    /// <summary>Enabled mod folders, LOWEST priority first (the order files should be layered in).</summary>
    public IReadOnlyList<string> EnabledModsLowToHigh { get; }

    /// <summary>Enabled plugin file names in load order (base masters and CC included).</summary>
    public IReadOnlyList<string> EnabledPlugins { get; }

    public bool UsesLocalInis { get; }

    /// <summary>Plugins ticked in plugins.txt. Base masters and Skyrim.ccc entries are implicit and may legitimately be absent.</summary>
    public IReadOnlySet<string> ExplicitlyEnabled { get; }

    private static readonly string[] BaseMasters =
        ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    private Mo2Profile(Mo2Instance instance, string name, string folder)
    {
        Instance = instance;
        Name = name;
        Folder = folder;
        EnabledModsLowToHigh = ReadModList(Path.Combine(folder, "modlist.txt"));
        UsesLocalInis = ReadLocalSettingsFlag(Path.Combine(folder, "settings.ini"));
        EnabledPlugins = ReadPlugins(folder, instance.GamePath, out var explicitSet);
        ExplicitlyEnabled = explicitSet;
    }

    public static Mo2Profile Open(Mo2Instance instance, string name)
    {
        var folder = Path.Combine(instance.ProfilesFolder, name);
        if (!File.Exists(Path.Combine(folder, "modlist.txt")))
            throw new DirectoryNotFoundException($"MO2 profile \"{name}\" not found in {instance.ProfilesFolder}.");
        return new Mo2Profile(instance, name, folder);
    }

    /// <summary>
    /// modlist.txt is written highest priority first. "+" = enabled mod, "-" = disabled,
    /// "*" = unmanaged (DLC/CC already in the game Data folder). Separators end in "_separator".
    /// </summary>
    internal static List<string> ReadModList(string path)
    {
        var enabledHighToLow = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Length < 2 || line[0] != '+') continue;
            var name = line[1..];
            if (name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase)) continue;
            enabledHighToLow.Add(name);
        }
        enabledHighToLow.Reverse();
        return enabledHighToLow;
    }

    private static bool ReadLocalSettingsFlag(string settingsIni)
    {
        if (!File.Exists(settingsIni)) return false;
        var ini = Mo2Instance.ReadIni(settingsIni);
        return ini.TryGetValue("General", out var g)
               && g.TryGetValue("LocalSettings", out var v)
               && v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Order comes from loadorder.txt (it includes base masters and CC); "enabled" comes from
    /// plugins.txt ("*" prefix) plus the implicitly-active base masters and Skyrim.ccc entries.
    /// </summary>
    internal static List<string> ReadPlugins(string profileFolder, string gamePath, out HashSet<string> explicitlyEnabled)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginsTxtOrder = new List<string>();

        var pluginsTxt = Path.Combine(profileFolder, "plugins.txt");
        if (File.Exists(pluginsTxt))
        {
            foreach (var raw in File.ReadLines(pluginsTxt))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] == '#' || line[0] != '*') continue;
                var name = line[1..];
                if (enabled.Add(name)) pluginsTxtOrder.Add(name);
            }
        }

        explicitlyEnabled = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        foreach (var m in BaseMasters) enabled.Add(m);
        var ccc = Path.Combine(gamePath, "Skyrim.ccc");
        if (File.Exists(ccc))
            foreach (var l in File.ReadLines(ccc).Select(l => l.Trim()).Where(l => l.Length > 0))
                enabled.Add(l);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var loadOrderTxt = Path.Combine(profileFolder, "loadorder.txt");
        if (File.Exists(loadOrderTxt))
        {
            foreach (var raw in File.ReadLines(loadOrderTxt))
            {
                var name = raw.Trim();
                if (name.Length == 0 || name[0] == '#') continue;
                if (enabled.Contains(name) && seen.Add(name)) result.Add(name);
            }
        }
        else
        {
            foreach (var m in BaseMasters) if (seen.Add(m)) result.Add(m);
        }

        // Anything enabled in plugins.txt but missing from loadorder.txt goes at the end, in plugins.txt order.
        foreach (var p in pluginsTxtOrder)
            if (seen.Add(p)) result.Add(p);

        return result;
    }

    /// <summary>
    /// sResourceArchiveList + sResourceArchiveList2 from the profile's skyrim.ini (when the profile
    /// uses local INIs) or the user's Documents INI; falls back to the SSE defaults.
    /// </summary>
    public IReadOnlyList<string> IniArchives()
    {
        var candidates = new List<string>();
        if (UsesLocalInis)
        {
            candidates.Add(Path.Combine(Folder, "skyrimcustom.ini"));
            candidates.Add(Path.Combine(Folder, "skyrim.ini"));
        }
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
        candidates.Add(Path.Combine(docs, "SkyrimCustom.ini"));
        candidates.Add(Path.Combine(docs, "Skyrim.ini"));

        foreach (var ini in candidates.Where(File.Exists))
        {
            var parsed = Mo2Instance.ReadIni(ini);
            if (!parsed.TryGetValue("Archive", out var a)) continue;
            var list = new List<string>();
            foreach (var key in new[] { "sResourceArchiveList", "sResourceArchiveList2" })
                if (a.TryGetValue(key, out var v))
                    list.AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (list.Count > 0) return list;
        }

        return
        [
            "Skyrim - Misc.bsa", "Skyrim - Shaders.bsa", "Skyrim - Interface.bsa", "Skyrim - Animations.bsa",
            "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa", "Skyrim - Sounds.bsa",
            "Skyrim - Voices_en0.bsa", "Skyrim - Textures0.bsa", "Skyrim - Textures1.bsa", "Skyrim - Textures2.bsa",
            "Skyrim - Textures3.bsa", "Skyrim - Textures4.bsa", "Skyrim - Textures5.bsa", "Skyrim - Textures6.bsa",
            "Skyrim - Textures7.bsa", "Skyrim - Textures8.bsa", "Skyrim - Patch.bsa",
        ];
    }
}
