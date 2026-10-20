using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// Decides which mesh each object shows at each LOD level:
/// rules pick a choice per level (an LOD level, the full model, or nothing); LOD levels are
/// resolved from the base record's MNAM first, then from meshes found by file name.
/// </summary>
public sealed class LodMeshResolver
{
    private readonly LodMeshIndex _index;
    private readonly LodRules _rules;
    private readonly Func<string, bool> _exists;
    private readonly IReadOnlyDictionary<string, string> _lookup;

    /// <param name="meshLookup">Full model path → the path whose LOD meshes it should use
    /// (DynDOLOD's <c>Configs\DynDOLOD_SSE_mesh_lookup.txt</c>, for mods that didn't follow the naming convention).</param>
    public LodMeshResolver(LodMeshIndex index, LodRules rules, Func<string, bool> exists, IReadOnlyDictionary<string, string>? meshLookup = null)
    {
        _index = index;
        _rules = rules;
        _exists = exists;
        _lookup = meshLookup ?? new Dictionary<string, string>();
    }

    /// <summary>The glow LOD mesh for a full model (see <see cref="LodMeshIndex.GlowFor"/>), honouring the mesh lookup file.</summary>
    public (string Path, int Level)? GlowMeshFor(string modelPath)
    {
        var key = LodMeshIndex.Normalize(modelPath);
        return _index.GlowFor(_lookup.TryGetValue(key, out var other) ? other : modelPath);
    }

    /// <summary>Parses "full\path.nif=other\path.nif" lines ("//" comments) into normalized "meshes\…" keys.</summary>
    public static Dictionary<string, string> ParseMeshLookup(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//") || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[LodMeshIndex.Normalize(line[..eq].Trim())] = LodMeshIndex.Normalize(line[(eq + 1)..].Trim());
        }
        return map;
    }

    public bool HasFormIdRule(string formIdKey) => _rules.HasFormIdRule(formIdKey);

    /// <summary>
    /// Replaces the LOD meshes chosen for a model (full model path, the set the rules and MNAM gave) with others, or returns the set
    /// as it is. Used to draw whole ships from their real models.
    /// </summary>
    public Func<string, LodMeshSet, LodMeshSet>? ModelUpgrade { get; init; }

    /// <summary>
    /// LOD32 is only shown on the world map (uLockedObjectMapLOD=32, as A Clear Map of Skyrim sets it). DynDOLOD leaves it empty unless a rule
    /// names a LOD32 model (rule files of 7 columns have none), and so does AnvilLOD by default (-1). 0..2 gives every object whose rule has
    /// no LOD32 column the LOD mesh of that level at LOD32 (the map mod asks for Level0 on DynDOLOD's catch-all rule). Objects drawn by
    /// dynamic LOD only, and rules that name LOD32 themselves (even None), keep what they have.
    /// </summary>
    public int MapObjectLevel { get; init; } = -1;

    /// <summary>
    /// LOD meshes for a model that has none anywhere (no MNAM, no file by name): the full model's path and whether the rules want LOD for it
    /// in, a set out, or null. The callback decides whether to override a rule that leaves the model out.
    /// </summary>
    public Func<string, bool, LodMeshSet?>? ModelFallback { get; init; }

    public LodRules Rules => _rules;
    public LodMeshIndex Index => _index;

    public sealed record Resolution(LodMeshSet Meshes, LodRule Rule, bool UsedNamedLod, bool UsedMnam);

    /// <param name="mnam">Base record MNAM level 0..3 paths ("meshes\..."), entries may be null.</param>
    public Resolution Resolve(string? refFormIdKey, string? baseFormIdKey, string fullModelPath, IReadOnlyList<string?>? mnam)
    {
        var rule = _rules.Match(refFormIdKey, baseFormIdKey, fullModelPath);

        var named = (_lookup.Count > 0 && _lookup.TryGetValue(LodMeshIndex.Normalize(fullModelPath), out var alias) ? _index.Find(alias) : null)
                    ?? _index.Find(fullModelPath);
        var levels = new string?[4];
        bool usedNamed = false, usedMnam = false;
        for (int l = 0; l < 4; l++)
        {
            // The plugin's own MNAM wins (it's what the object's author or a LOD patch chose; DynDOLOD output
            // was verified to use it), named name_lod_N.nif files fill the levels MNAM doesn't provide.
            if (mnam is not null && l < mnam.Count && !string.IsNullOrEmpty(mnam[l]) && _exists(mnam[l]!))
            {
                levels[l] = mnam[l];
                usedMnam = true;
            }
            else if (named?[l] is { } n) { levels[l] = n; usedNamed = true; }
        }
        // Same fallback as for named meshes: a missing lower level uses the next higher one.
        for (int l = 2; l >= 0; l--) levels[l] ??= levels[l + 1];

        string? Pick(LodChoice c) => c.Kind switch
        {
            LodChoiceKind.Level => levels[Math.Clamp(c.Level, 0, 3)],
            LodChoiceKind.FullModel => _exists(fullModelPath) ? fullModelPath : null,
            _ => null, // None; Billboard is handled by tree LOD (milestone 3)
        };

        var lod32 = Pick(rule.Lod32);
        if (lod32 is null && MapObjectLevel >= 0 && !rule.Lod32Explicit && !rule.IsGridObject && WantsLod(rule))
            lod32 = levels[Math.Clamp(MapObjectLevel, 0, 2)];
        var set = new LodMeshSet(Pick(rule.Lod4), Pick(rule.Lod8), Pick(rule.Lod16), lod32);
        if (ModelUpgrade is not null && set.HasAny) set = ModelUpgrade(fullModelPath, set);
        else if (ModelFallback is not null && !set.HasAny && !rule.IsGridObject && ModelFallback(fullModelPath, WantsLod(rule)) is { HasAny: true } made) set = made;
        return new Resolution(set, rule, usedNamed, usedMnam);
    }

    /// <summary>The rule asks for a LOD mesh at some level (as opposed to leaving the object out of LOD).</summary>
    private static bool WantsLod(LodRule r) =>
        r.Lod4.Kind == LodChoiceKind.Level || r.Lod8.Kind == LodChoiceKind.Level || r.Lod16.Kind == LodChoiceKind.Level || r.Lod32.Kind == LodChoiceKind.Level;

    /// <summary>
    /// Mesh for a grid object: DynDOLOD's dynamic LOD mesh (<c>meshes\dyndolod\lod\&lt;model path&gt;_dyndolod_lod.nif</c>,
    /// shipped by DynDOLOD Resources and water mods), otherwise the full model. "Far Full" always uses the full model.
    /// </summary>
    /// <param name="isPbrModel">When given, a waterfall whose full model is PBR-patched (its textures live under <c>textures\pbr\</c>)
    /// is drawn with that model: the plain DynDOLOD LOD mesh would show the non-PBR water tile.</param>
    public static string? GridMesh(string modelPath, DynamicGrid grid, Func<string, bool> exists, Func<string, bool>? isPbrModel = null)
    {
        var full = LodMeshIndex.Normalize(modelPath);
        if (isPbrModel is not null && full.Contains("waterfall", StringComparison.Ordinal) && exists(full) && isPbrModel(full))
            return full;
        if (grid != DynamicGrid.FarFull && full.StartsWith("meshes\\", StringComparison.Ordinal) && full.EndsWith(".nif", StringComparison.Ordinal))
        {
            // PGPatcher gives meshes that need several shader variants copies under meshes\_pgpatcher_dups\<n>\ and points the
            // base record at one of them. The dynamic LOD model is named after the original path, so look for that one.
            var original = PgPatcherDups.Replace(full, "meshes\\");
            var lod = "meshes\\dyndolod\\lod\\" + original["meshes\\".Length..^4] + "_dyndolod_lod.nif";
            if (exists(lod)) return lod;
        }
        return exists(full) ? full : null;
    }

    private static readonly System.Text.RegularExpressions.Regex PgPatcherDups = new(@"^meshes\\_pgpatcher_dups\\\d+\\",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

}
