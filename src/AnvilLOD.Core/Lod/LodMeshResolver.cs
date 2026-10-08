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

        var set = new LodMeshSet(Pick(rule.Lod4), Pick(rule.Lod8), Pick(rule.Lod16), Pick(rule.Lod32));
        return new Resolution(set, rule, usedNamed, usedMnam);
    }
}
