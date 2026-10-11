namespace AnvilLOD.Core.Lod;

/// <summary>An inclusive rectangle of exterior cells.</summary>
public readonly record struct CellRect(int MinX, int MinY, int MaxX, int MaxY)
{
    public bool Contains(int x, int y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
    public CellRect Expand(int cells) => new(MinX - cells, MinY - cells, MaxX + cells, MaxY + cells);

    /// <summary>"swX,swY,neX,neY[,minHeight]" as DynDOLOD's childworld ini writes it; null if it isn't four numbers.</summary>
    public static CellRect? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 4) return null;
        var v = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(p[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out v[i])) return null;
        return new CellRect(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}

/// <summary>
/// DynDOLOD's "Parent &gt; Child" copy for one child worldspace (<c>ScanParent=1</c>): parent references around a walled city are
/// copied into the child, because the child worldspace deliberately leaves out the objects outside the walls and the view over the
/// wall is barren. Measured on a real DynDOLOD run: the copies come from the cells inside the ini's <c>Bounds</c> grown by
/// <see cref="BoundsMargin"/> cells, minus <c>BoundsIgnore</c>.
/// Reference: https://dyndolod.info/Help/Child-Parent-Worldspace-Copies
/// </summary>
public sealed record ParentCopyWorld(
    string Child,
    string Parent,
    CellRect? Bounds,                        // null = no Bounds in the ini (Dragonsreach): every cell not in BoundsIgnore
    CellRect? BoundsIgnore,
    bool NoCellsWithNavmesh,
    IReadOnlyList<string> IgnoreParentEditorIds,   // lower-case substrings of the base record's Editor ID
    string SourceFile)
{
    /// <summary>How far past the ini's Bounds the copies reach (5 cells in every direction in DynDOLOD's output).</summary>
    public const int BoundsMargin = 5;

    public bool Covers(int cellX, int cellY) =>
        (Bounds is null || Bounds.Value.Expand(BoundsMargin).Contains(cellX, cellY))
        && !(BoundsIgnore is { } ig && ig.Contains(cellX, cellY));

    public bool IgnoresEditorId(string? edid)
    {
        if (string.IsNullOrEmpty(edid)) return false;
        var e = edid.ToLowerInvariant();
        foreach (var s in IgnoreParentEditorIds)
            if (e.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }
}

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
    private readonly Dictionary<string, ParentCopyWorld> _parentByChild = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _matched = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _standIns = new(StringComparer.OrdinalIgnoreCase);   // the parent side of ChildworldMatches

    public static readonly ChildWorldCopies None = new();

    public IReadOnlyCollection<ChildWorldCopy> Worlds => _byChild.Values;

    /// <summary>The same configuration without the child-to-parent copies (walled cities in the parent's LOD turned off).</summary>
    public ChildWorldCopies WithoutChildCopies()
    {
        var c = new ChildWorldCopies();
        foreach (var kv in _parentByChild) c._parentByChild[kv.Key] = kv.Value;
        foreach (var s in _standIns) c._standIns.Add(s);
        return c;
    }
    /// <summary>Child worldspaces that get copies of their parent's references (ScanParent=1).</summary>
    public IReadOnlyCollection<ParentCopyWorld> ParentCopyWorlds => _parentByChild.Values;
    public int MatchedCount => _matched.Count;

    public ChildWorldCopy? ForChild(string? childWorldspace) =>
        childWorldspace is not null && _byChild.TryGetValue(childWorldspace, out var c) ? c : null;

    /// <summary>True if ChildworldMatches lists the child reference ("plugin;id" rule key).</summary>
    public bool IsMatched(string refKey) => _matched.ContainsKey(refKey);

    /// <summary>
    /// True if ChildworldMatches lists this parent reference as the stand-in for a child reference (Tamriel's low-detail wall, tower or
    /// house outline of a city that the child worldspace has the real object for). A Parent &gt; Child copy would put it on top of the real one.
    /// </summary>
    public bool IsParentStandIn(string parentRefKey) => _standIns.Contains(parentRefKey);

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
            var text = File.ReadAllText(file);
            if (Parse(child, text, loaded, preset, Path.GetFileName(file)) is { } c)
                result._byChild[child] = c;
            if (ParseParentCopy(child, text, loaded, Path.GetFileName(file)) is { } pc)
                result._parentByChild[child] = pc;
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

    /// <summary>Parses the Parent &gt; Child part of one childworld ini; null if ScanParent isn't 1 or a blocking plugin is loaded.</summary>
    public static ParentCopyWorld? ParseParentCopy(string child, string content, IReadOnlySet<string> pluginsLoaded, string source)
    {
        string? parent = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split((char)10))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line.EndsWith(']')) { parent ??= line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (string.IsNullOrEmpty(parent)) return null;
        if (!values.TryGetValue("ScanParent", out var scan) || scan != "1") return null;
        if (values.TryGetValue("NoScanIfPluginExists", out var blockers) && List(blockers).Any(pluginsLoaded.Contains)) return null;
        if (values.TryGetValue("NoScanParentIfPluginExists", out var parentBlockers) && List(parentBlockers).Any(pluginsLoaded.Contains)) return null;
        return new ParentCopyWorld(child, parent,
            CellRect.Parse(values.GetValueOrDefault("Bounds")),
            CellRect.Parse(values.GetValueOrDefault("BoundsIgnore")),
            values.TryGetValue("NoCellsWithNAVM", out var nav) && nav == "1",
            List(values.GetValueOrDefault("IgnoreParentEDID", "")).Select(s => s.ToLowerInvariant()).ToList(),
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
            _standIns.Add(LodRules.FormIdKey(c[2].Trim(), pid));
        }
    }

    private static IEnumerable<string> List(string s) =>
        s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
