namespace AnvilLOD.Core.Lod;

/// <summary>A form as written in a Seasons of Skyrim INI: "0x826~Plugin.esp" (local ID + plugin) or an EditorID.</summary>
public sealed record SeasonFormRef(string? Plugin, uint LocalId, string? EditorId)
{
    public static SeasonFormRef? Parse(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        int tilde = s.IndexOf('~');
        if (tilde > 0)
        {
            var hex = s[..tilde].Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            if (!LodRules.TryParseFormId(hex, out var id)) return null;
            var plugin = s[(tilde + 1)..].Trim();
            return plugin.Length == 0 ? null : new SeasonFormRef(plugin, id, null);
        }
        return new SeasonFormRef(null, 0, s);
    }
}

public sealed record SeasonSwap(string Section, SeasonFormRef Base, SeasonFormRef Swap);

/// <summary>
/// Seasons of Skyrim (powerof3) form swaps: <c>Data\Seasons\*_WIN.ini</c> / <c>_SPR</c> / <c>_SUM</c> / <c>_AUT</c>, one
/// section per record type ("[Statics]", "[Trees]", …), lines "Base|Swap". When seasonal LOD files exist
/// (<c>&lt;World&gt;.&lt;L&gt;.&lt;X&gt;.&lt;Y&gt;.WIN.bto</c>), the plugin loads them instead of the normal ones for that season.
/// Source: github.com/powerof3/SeasonsOfSkyrim (LODSwap.h, FormSwapMap.cpp).
/// </summary>
public static class SeasonSwaps
{
    public static readonly IReadOnlyList<(string Suffix, string Name)> Seasons =
        [("WIN", "Winter"), ("SPR", "Spring"), ("SUM", "Summer"), ("AUT", "Autumn")];

    /// <summary>Record types that can have object LOD (TREE is tree LOD, flora and land textures have none).</summary>
    public static readonly IReadOnlySet<string> ObjectSections =
        new HashSet<string>(["Activators", "Furniture", "MovableStatics", "Statics"], StringComparer.OrdinalIgnoreCase);

    /// <summary>"Floral Sky_WIN.ini" → "WIN"; null for other files (e.g. "_SNOW.ini" shader settings).</summary>
    public static string? SeasonOf(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return null;
        int us = stem.LastIndexOf('_');
        if (us < 0) return null;
        var suffix = stem[(us + 1)..].ToUpperInvariant();
        return Seasons.Any(s => s.Suffix == suffix) ? suffix : null;
    }

    public static List<SeasonSwap> Parse(string content)
    {
        var list = new List<SeasonSwap>();
        string? section = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[') { section = line.Trim('[', ']').Trim(); continue; }
            if (section is null) continue;
            // CSimpleIni keys: the whole line is the key ("Base|Swap"); a trailing "=" may appear.
            int eq = line.IndexOf('=');
            if (eq >= 0) line = line[..eq].Trim();
            var pair = line.Split('|');
            if (pair.Length != 2) continue;
            if (SeasonFormRef.Parse(pair[0]) is { } b && SeasonFormRef.Parse(pair[1]) is { } s)
                list.Add(new SeasonSwap(section, b, s));
        }
        return list;
    }

    /// <summary>Seasonal file name: "meshes\terrain\tamriel\objects\tamriel.4.0.0.bto" → "….tamriel.4.0.0.WIN.bto".</summary>
    public static string SeasonalPath(string relativePath, string suffix)
    {
        var ext = Path.GetExtension(relativePath);
        return relativePath[..^ext.Length] + "." + suffix + ext;
    }
}
