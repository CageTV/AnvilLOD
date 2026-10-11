using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Lod;

/// <summary>What to show for one object at one LOD level.</summary>
public enum LodChoiceKind { None, Level, FullModel, Billboard }

public readonly record struct LodChoice(LodChoiceKind Kind, int Level = 0)
{
    public static readonly LodChoice None = new(LodChoiceKind.None);
    public static LodChoice Lod(int level) => new(LodChoiceKind.Level, level);
    public static readonly LodChoice Full = new(LodChoiceKind.FullModel);
    public static readonly LodChoice Billboard = new(LodChoiceKind.Billboard);

    public static LodChoice Parse(string s)
    {
        var t = s.Trim().Replace(" ", "").ToLowerInvariant();
        return t switch
        {
            "" or "none" => None,
            "level0" or "staticlod4" => Lod(0),
            "level1" or "staticlod8" => Lod(1),
            "level2" or "staticlod16" => Lod(2),
            "level3" or "staticlod32" => Lod(3),
            "full" or "fullmodel" => Full,
            _ when t.StartsWith("billboard", StringComparison.Ordinal) => Billboard,
            _ => None,
        };
    }
}

/// <summary>
/// DynDOLOD's Grid column: where dynamic LOD shows an object that has no static LOD (empty LOD columns): water
/// planes, waterfalls, fires, windmills, ships. Near = the near grid around the loaded cells, Far = out to the far
/// grid, FarFull = like Far but always the full model, NeverFade = drawn at any distance.
/// </summary>
public enum DynamicGrid { None, Near, Far, FarFull, NeverFade }

/// <summary>One rule line: a mesh mask (substring of the model path) or a reference/base FormID, and a choice per level.</summary>
public sealed record LodRule(
    string Mask,               // lower-case path substring, or "" for FormID rules
    string? FormId,            // "skyrim.esm;00071c5e" (lower-case), or null for mesh rules
    LodChoice Lod4, LodChoice Lod8, LodChoice Lod16, LodChoice Lod32,
    string SourceFile,
    DynamicGrid Grid = DynamicGrid.None,
    int Flags = 0)                // DynDOLOD's Flags column: 1 VWD, 2 NoGlow, 4 NoMATO, 8 Dynamic, 16 TREE
{
    /// <summary>"NoGlow": the object's LOD gets no glowing windows.</summary>
    public bool NoGlow => (Flags & RuleEntry.FlagNoGlow) != 0;

    /// <summary>
    /// An object DynDOLOD draws with dynamic LOD only (all LOD columns empty, a grid set): the SKSE plugin draws
    /// its full model (or <c>_dyndolod_lod.nif</c>) beyond the loaded cells, animated.
    /// </summary>
    public bool IsGridObject => Grid != DynamicGrid.None
        && Lod4.Kind == LodChoiceKind.None && Lod8.Kind == LodChoiceKind.None
        && Lod16.Kind == LodChoiceKind.None && Lod32.Kind == LodChoiceKind.None;

    public static DynamicGrid ParseGrid(string s) => s.Trim().Replace(" ", "").ToLowerInvariant() switch
    {
        "nearlod" or "near" => DynamicGrid.Near,
        "farlod" or "far" => DynamicGrid.Far,
        "farfull" => DynamicGrid.FarFull,
        "neverfadelod" or "neverfade" => DynamicGrid.NeverFade,
        _ => DynamicGrid.None,
    };

    public LodChoice For(LodLevel level) => level switch
    {
        LodLevel.Lod4 => Lod4,
        LodLevel.Lod8 => Lod8,
        LodLevel.Lod16 => Lod16,
        _ => Lod32,
    };
}

public enum LodPreset { Low, Medium, High }

/// <summary>
/// What DynDOLOD's Advanced window adds on top of the Low / Medium / High buttons, and the user's own rules.
/// </summary>
/// <param name="Candles">Also load the Candles rules (<c>DynDOLOD_SSE_candles_all/_low/_medium/_high.ini</c>): candle, lantern and sconce lights get Far LOD.</param>
/// <param name="FxGlow">Also load the FXGlow rules (<c>DynDOLOD_SSE_fxglow_low/_medium/_high.ini</c>): the glow cards of fires and lights get Far LOD.</param>
/// <param name="CustomFile">A rule file of the user's own (a DynDOLOD rule file, or one saved by AnvilLOD's rule editor). Its rules come
/// before every other rule, so they win; its catch-all rule (mask <c>\</c>) replaces the preset's. Null: only DynDOLOD's own files.</param>
public sealed record LodRuleOptions(bool Candles = false, bool FxGlow = false, string? CustomFile = null);

/// <summary>
/// Reads DynDOLOD mesh-mask / reference rule files (<c>DynDOLOD_SSE_*.ini</c>), so the rules that
/// DynDOLOD ships and that mods ship for it (FOLIP, MPXP, Bent Pines, …) are reused as-is.
/// <para>Both line formats are supported:</para>
/// <code>
/// LODGen1=mask,LOD4,LOD8,LOD16,Grid,Reference,Flags                        (7 columns; no LOD32: none, as DynDOLOD reads it)
/// LODGen1=mask,LOD4,LOD8,LOD16,LOD32,Grid,Reference,Flags,Description       (9 columns)
/// </code>
/// FormID rules ("plugin.esm;00ABCDEF") are checked before mesh masks; otherwise the first
/// matching rule wins. The LOD columns drive static object LOD; the Grid column marks objects that only get
/// dynamic LOD (see <see cref="LodRule.IsGridObject"/>), which the SKSE plugin draws.
/// Reference: https://dyndolod.info/Help/Mesh-Mask-Reference-Rules
/// </summary>
public sealed class LodRules
{
    private readonly List<LodRule> _formIdRules = [];
    private readonly List<LodRule> _meshRules = [];
    private LodRule? _catchAll; // the rule with mask "\": it matches everything, so it is kept apart and applies last (the first one added wins)
    private readonly Dictionary<string, LodRule> _formIdIndex = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<string>> _ignoreWorlds = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Files { get; private set; } = [];

    /// <summary>
    /// True if references added by <paramref name="plugin"/> in <paramref name="worldspace"/> are ignored for LOD:
    /// <c>IgnoreWorlds=</c> in a rule file's <c>[… Settings]</c> section, and DynDOLOD's
    /// <c>Configs\DynDOLOD_SSE_mod_world_ignore.txt</c> ("plugin=world,world").
    /// </summary>
    public bool IgnoresWorld(string plugin, string worldspace) =>
        _ignoreWorlds.TryGetValue(plugin, out var w) && w.Contains(worldspace);

    public int IgnoredWorldEntries => _ignoreWorlds.Values.Sum(v => v.Count);

    public void AddIgnoreWorlds(string plugin, IEnumerable<string> worlds)
    {
        if (!_ignoreWorlds.TryGetValue(plugin, out var set))
            _ignoreWorlds[plugin] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in worlds) if (w.Length > 0) set.Add(w);
    }

    /// <summary>DynDOLOD's mod_world_ignore.txt: "Plugin.esp=WorldA,WorldB" ("//" comments).</summary>
    public void AddModWorldIgnore(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            AddIgnoreWorlds(line[..eq].Trim(), line[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
    }
    public int Count => _formIdRules.Count + _meshRules.Count + (_catchAll is null ? 0 : 1);

    /// <summary>Used when no rule files are available: each object's own LOD meshes, level 0/1/2, and level 2 for LOD32.</summary>
    public static readonly LodRule Default = new("\\", null, LodChoice.Lod(0), LodChoice.Lod(1), LodChoice.Lod(2), LodChoice.Lod(2), "(built-in default)");

    public bool HasFormIdRule(string formIdKey) => _formIdIndex.ContainsKey(formIdKey);

    /// <summary>First matching rule for a reference/base and model path (FormID rules first).</summary>
    public LodRule Match(string? refFormId, string? baseFormId, string modelPath)
    {
        if (refFormId is not null && _formIdIndex.TryGetValue(refFormId, out var r)) return r;
        if (baseFormId is not null && _formIdIndex.TryGetValue(baseFormId, out r)) return r;

        var path = "\\" + LodMeshIndex.Normalize(modelPath)["meshes\\".Length..];
        foreach (var rule in _meshRules)
            if (path.Contains(rule.Mask, StringComparison.Ordinal)) return rule;
        return _catchAll ?? Default;
    }

    // ---------- loading ----------

    /// <summary>
    /// Picks the rule files DynDOLOD would use for a preset and load order, and reads them in priority order:
    /// plugin-specific files (later plugins first), then <c>_all</c>, then the preset file (which ends with the
    /// catch-all rule). Files in the game's Data\DynDOLOD folder replace same-named files from the DynDOLOD install.
    /// </summary>
    /// <param name="installRulesFolder">"…\DynDOLOD\Edit Scripts\DynDOLOD\Rules", or null.</param>
    /// <param name="dataRuleFiles">Rule files found under Data\DynDOLOD (file name → content reader).</param>
    /// <param name="pluginsInLoadOrder">Plugin file names, load order.</param>
    public static LodRules Load(
        string? installRulesFolder,
        IReadOnlyDictionary<string, Func<string>> dataRuleFiles,
        IReadOnlyList<string> pluginsInLoadOrder,
        LodPreset preset,
        string gameMode = "SSE",
        LodRuleOptions? options = null)
    {
        options ??= new LodRuleOptions();
        var available = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
        if (installRulesFolder is not null && Directory.Exists(installRulesFolder))
            foreach (var f in Directory.EnumerateFiles(installRulesFolder, "*.ini"))
            {
                var path = f;
                available[Path.GetFileName(f)] = () => File.ReadAllText(path);
            }
        foreach (var (name, read) in dataRuleFiles)
            available[name] = read; // Data\DynDOLOD wins

        var (ordered, pluginOf) = SelectFiles(available.Keys, pluginsInLoadOrder, preset, gameMode, options);

        var rules = new LodRules();
        var files = new List<string>();
        // The user's own file first: the first matching rule wins, so their rules override everything below.
        if (!string.IsNullOrWhiteSpace(options.CustomFile))
        {
            if (!File.Exists(options.CustomFile)) throw new FileNotFoundException("The custom LOD rules file does not exist.", options.CustomFile);
            var name = "custom: " + Path.GetFileName(options.CustomFile);
            rules.AddFile(name, File.ReadAllText(options.CustomFile));
            files.Add(name);
        }
        foreach (var f in ordered) { rules.AddFile(f, available[f](), pluginOf.GetValueOrDefault(f)); files.Add(f); }
        rules.Files = files;
        return rules;
    }

    /// <summary>The folder with DynDOLOD's rule files (<c>DynDOLOD_SSE_*.ini</c>) inside a DynDOLOD install folder, or null.</summary>
    public static string? FindInstallRulesFolder(string? dyndolod)
    {
        if (string.IsNullOrWhiteSpace(dyndolod)) return null;
        foreach (var candidate in new[]
        {
            Path.Combine(dyndolod, "Edit Scripts", "DynDOLOD", "Rules"),
            Path.Combine(dyndolod, "DynDOLOD", "Rules"),
            Path.Combine(dyndolod, "Rules"),
            dyndolod,
        })
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "DynDOLOD_*.ini").Any()) return candidate;
        return null;
    }

    /// <summary>
    /// The rule files DynDOLOD's own rules are read from, in priority order: plugin-specific files (later plugins first), then the
    /// Candles and FXGlow files when asked for, then <c>_all</c>, then the preset file.
    /// </summary>
    private static (List<string> Ordered, Dictionary<string, string> PluginOf) SelectFiles(
        IEnumerable<string> availableNames, IReadOnlyList<string> pluginsInLoadOrder, LodPreset preset, string gameMode, LodRuleOptions options)
    {
        var have = new HashSet<string>(availableNames, StringComparer.OrdinalIgnoreCase);
        var p = preset.ToString().ToLowerInvariant();
        var ordered = new List<string>();
        var pluginOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Want(string fileName, string? plugin = null)
        {
            if (have.Contains(fileName) && !ordered.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(fileName);
                if (plugin is not null) pluginOf[fileName] = plugin;
            }
        }

        foreach (var plugin in pluginsInLoadOrder.Reverse())
        {
            var key = PluginKey(plugin);
            Want($"DynDOLOD_{gameMode}_{key}_{p}.ini", plugin);
            Want($"DynDOLOD_{gameMode}_{key}_all.ini", plugin);
            Want($"DynDOLOD_{gameMode}_{key}.ini", plugin);
            Want($"DynDOLOD_{key}.ini", plugin);
        }
        // The "_all" candle file holds the switched-off variants ("...off"), which have to come before their lit masks.
        if (options.Candles) { Want($"DynDOLOD_{gameMode}_candles_all.ini"); Want($"DynDOLOD_{gameMode}_candles_{p}.ini"); }
        if (options.FxGlow) Want($"DynDOLOD_{gameMode}_fxglow_{p}.ini");
        Want($"DynDOLOD_{gameMode}_all.ini");
        Want($"DynDOLOD_{gameMode}_{p}.ini");
        return (ordered, pluginOf);
    }

    /// <summary>
    /// The rules DynDOLOD's own files give for a preset (no plugin-specific files: those depend on the load order), for the rule
    /// editor to show and start from. Same order as <see cref="Load"/> reads them.
    /// </summary>
    public static List<RuleEntry> DefaultEntries(string? installRulesFolder, LodPreset preset, LodRuleOptions? options = null, string gameMode = "SSE")
    {
        options ??= new LodRuleOptions();
        var result = new List<RuleEntry>();
        if (installRulesFolder is null || !Directory.Exists(installRulesFolder)) return result;
        var files = Directory.EnumerateFiles(installRulesFolder, "*.ini").ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var name in SelectFiles(files.Keys, [], preset, gameMode, options).Ordered)
            result.AddRange(RuleEntry.ParseFile(File.ReadAllText(files[name]), name));
        // The catch-all goes last, as in DynDOLOD's list.
        return result.Where(r => !r.IsCatchAll).Concat(result.Where(r => r.IsCatchAll).Take(1)).ToList();
    }

    /// <summary>DynDOLOD plugin key: lower-case letters, digits and underscores only ("MPXP0.0 [SELUA-II].esp" → "mpxp00seluaiiesp").</summary>
    public static string PluginKey(string pluginFileName) =>
        new string(pluginFileName.ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());

    /// <param name="plugin">The plugin the file belongs to (for its <c>[… Settings]</c> section), or null.</param>
    public void AddFile(string sourceName, string content, string? plugin = null)
    {
        var lines = new List<(int N, string Value)>();
        bool settings = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '/') continue;
            if (line[0] == '[') { settings = line.TrimEnd(']').EndsWith("Settings", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (settings)
            {
                // "[Skyrim Settings] IgnoreWorlds=RiftenWorld": this plugin's references in those worlds get no LOD.
                if (plugin is not null && line[..eq].Trim().Equals("IgnoreWorlds", StringComparison.OrdinalIgnoreCase))
                    AddIgnoreWorlds(plugin, line[(eq + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            if (!line.StartsWith("LODGen", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line.AsSpan(6, eq - 6), out var n)) n = int.MaxValue;
            lines.Add((n, line[(eq + 1)..]));
        }

        foreach (var (_, value) in lines.OrderBy(l => l.N))
        {
            var rule = ParseRule(value, sourceName);
            if (rule is null) continue;
            if (rule.FormId is null && rule.Mask == "\\") { _catchAll ??= rule; continue; }
            if (rule.FormId is not null)
            {
                _formIdRules.Add(rule);
                _formIdIndex.TryAdd(rule.FormId, rule); // earlier file/line wins
            }
            else _meshRules.Add(rule);
        }
    }

    internal static LodRule? ParseRule(string value, string source)
    {
        var c = value.Split(',');
        if (c.Length < 4) return null;
        var mask = c[0].Trim().ToLowerInvariant().Replace('/', '\\');
        if (mask.Length == 0) return null;

        LodChoice l4 = LodChoice.Parse(c[1]), l8 = LodChoice.Parse(c[2]), l16 = LodChoice.Parse(c[3]);
        // 7 columns: mask,4,8,16,Grid,Ref,Flags. 8-9 columns: mask,4,8,16,32,Grid,Ref,Flags[,Desc].
        bool hasLod32 = c.Length >= 8;
        // A 7-column line has no LOD32 column and DynDOLOD gives it no LOD32 (checked against DynDOLOD 3.0 Alpha-215's Object_Report:
        // only rules with an explicit LOD32 column, e.g. FOLIP's roads, are listed for LOD32). Copying LOD16 into LOD32 put
        // about 3.0M triangles into Tamriel's LOD32 blocks where DynDOLOD has 0.15M.
        LodChoice l32 = hasLod32 ? LodChoice.Parse(c[4]) : LodChoice.None;
        int gridCol = hasLod32 ? 5 : 4;
        var grid = c.Length > gridCol ? LodRule.ParseGrid(c[gridCol]) : DynamicGrid.None;

        string? formId = null;
        int semi = mask.IndexOf(';');
        if (semi > 0)
        {
            // Authors often write the FormID as seen in xEdit, with their load order index ("Mod.esp;0600187D");
            // only the record's own ID counts.
            if (!TryParseFormId(mask[(semi + 1)..], out var id)) return null;
            formId = FormIdKey(mask[..semi].Trim(), id);
            mask = "";
        }
        int.TryParse(c.Length > gridCol + 2 ? c[gridCol + 2].Trim() : "", out var flags);
        return new LodRule(mask, formId, l4, l8, l16, l32, source, grid, flags);
    }

    /// <summary>
    /// Hex FormID as written in rule and config files, without the load order prefix: the low 24 bits, or the low
    /// 12 bits for light plugin IDs written with their FE xxx prefix ("FE01A801" → 0x801).
    /// </summary>
    public static bool TryParseFormId(string hex, out uint id)
    {
        if (!uint.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var raw)) { id = 0; return false; }
        id = (raw >> 24) == 0xFE ? raw & 0xFFF : raw & 0x00FF_FFFF;
        return true;
    }

    /// <summary>Normalizes "skyrim.esm;00071C5E" or a Mutagen FormKey ("071C5E:Skyrim.esm") to the rule key form.</summary>
    public static string FormIdKey(string plugin, uint id) => $"{plugin.ToLowerInvariant()};{(id & 0x00FF_FFFF):x}";
}
