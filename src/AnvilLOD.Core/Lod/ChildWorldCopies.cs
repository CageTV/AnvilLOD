namespace AnvilLOD.Core.Lod;

/// <summary>One child worldspace whose references are copied into the parent worldspace's LOD.</summary>
public sealed record ChildWorldCopy(
    string Child,
    string Parent,
    bool NoCellsWithNavmesh,
    IReadOnlyList<string> IgnoreEditorIds,   // lower-case substrings of the base record's Editor ID
    string SourceFile)
{
    public bool IgnoresEditorId(string? edid)
    {
        if (string.IsNullOrEmpty(edid)) return false;
        var e = edid.ToLowerInvariant();
        foreach (var s in IgnoreEditorIds)
            if (e.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>
/// Child-to-parent worldspace copies, read from DynDOLOD's <c>Configs\DynDOLOD_SSE_childworld_&lt;child&gt;.ini</c>.
/// Walled cities (Whiterun, Solitude, Windhelm, Riften, Markarth) are child worldspaces: their buildings live there,
/// and the parent (Tamriel) only has a few low-detail placeholders, several of which the rules remove from LOD
/// (Dragonsreach's <c>WRCastleMainBuilding01LOD</c>, for example). Seen from outside, the city's LOD has to come from
/// the child's references, placed into the parent's LOD at the same coordinates (child and parent share them).
/// <para><c>Configs\DynDOLOD_SSE_ChildworldMatches.txt</c> lists child references that already have a copy in the
/// parent, so they aren't copied twice.</para>
/// Reference: https://dyndolod.info/Help/Child-Parent-Worldspace-Copies
/// </summary>
public sealed class ChildWorldCopies
{
    private readonly Dictionary<string, ChildWorldCopy> _byChild = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _matched = new(StringComparer.OrdinalIgnoreCase);

    public static readonly ChildWorldCopies None = new();

    public IReadOnlyCollection<ChildWorldCopy> Worlds => _byChild.Values;
    public int MatchedCount => _matched.Count;

    public ChildWorldCopy? ForChild(string? childWorldspace) =>
        childWorldspace is not null && _byChild.TryGetValue(childWorldspace, out var c) ? c : null;

    /// <summary>True if ChildworldMatches lists the child reference ("plugin;id" rule key).</summary>
    public bool IsMatched(string refKey) => _matched.ContainsKey(refKey);

    /// <summary>
    /// The parent references (rule keys) that stand in for this child reference: the parent's own copy, or a
    /// low-detail placeholder. The child is only copied when none of them is in the parent's LOD.
    /// </summary>
    public IReadOnlyCollection<string>? MatchedParents(string childKey) => _matched.TryGetValue(childKey, out var p) ? p : null;

    /// <param name="configsFolder">"…\Edit Scripts\DynDOLOD\Configs", or null.</param>
    public static ChildWorldCopies Load(string? configsFolder, IReadOnlyCollection<string> pluginsLoaded, LodPreset preset, string gameMode = "SSE")
    {
        var result = new ChildWorldCopies();
        if (configsFolder is null || !Directory.Exists(configsFolder)) return result;
        var loaded = new HashSet<string>(pluginsLoaded, StringComparer.OrdinalIgnoreCase);
        var prefix = $"DynDOLOD_{gameMode}_childworld_";
        foreach (var file in Directory.EnumerateFiles(configsFolder, prefix + "*.ini"))
        {
            var child = Path.GetFileNameWithoutExtension(file)[prefix.Length..];
            if (Parse(child, File.ReadAllText(file), loaded, preset, Path.GetFileName(file)) is { } c)
                result._byChild[child] = c;
        }
        var matches = Path.Combine(configsFolder, $"DynDOLOD_{gameMode}_ChildworldMatches.txt");
        if (File.Exists(matches))
            result.AddMatches(File.ReadAllText(matches), loaded);
        return result;
    }

    /// <summary>Parses one childworld ini; null if copying from the child is off (ScanChild=0 or a blocking plugin is loaded).</summary>
    public static ChildWorldCopy? Parse(string child, string content, IReadOnlySet<string> pluginsLoaded, LodPreset preset, string source)
    {
        string? parent = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line.EndsWith(']')) { parent ??= line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (string.IsNullOrEmpty(parent)) return null;
        if (!values.TryGetValue("ScanChild", out var scan) || scan != "1") return null;
        if (values.TryGetValue("NoScanIfPluginExists", out var blockers)
            && List(blockers).Any(pluginsLoaded.Contains))
            return null;

        var ignoreKey = "IgnoreChildEDID" + preset;
        var ignore = values.TryGetValue(ignoreKey, out var ig) ? ig : values.GetValueOrDefault("IgnoreChildEDID", "");
        return new ChildWorldCopy(child, parent,
            values.TryGetValue("NoCellsWithNAVM", out var nav) && nav == "1",
            List(ignore).Select(s => s.ToLowerInvariant()).ToList(),
            source);
    }

    /// <summary>ChildworldMatches lines: "childPlugin;childId;parentPlugin;parentId;description".</summary>
    public void AddMatches(string content, IReadOnlySet<string> pluginsLoaded)
    {
        foreach (var raw in content.Split('\n'))
        {
            var c = raw.Trim().Split(';');
            if (c.Length < 4) continue;
            if (!pluginsLoaded.Contains(c[0].Trim()) || !pluginsLoaded.Contains(c[2].Trim())) continue;
            if (!LodRules.TryParseFormId(c[1], out var id) || !LodRules.TryParseFormId(c[3], out var pid)) continue;
            var key = LodRules.FormIdKey(c[0].Trim(), id);
            if (!_matched.TryGetValue(key, out var set)) _matched[key] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(LodRules.FormIdKey(c[2].Trim(), pid));
        }
    }

    private static IEnumerable<string> List(string s) =>
        s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
