using System.Globalization;
using AnvilLOD.Core.Lod;
using AnvilLOD.Meshes;

namespace AnvilLOD.Plugins;

/// <summary>
/// Where the object LOD bytes go: per LOD level and per kind of thing (objects, 3D trees, tree cards, grass), and per
/// season. Big LOD files cost load time, memory and frame rate, so the log says what they are made of.
/// </summary>
public static class SizeBreakdown
{
    /// <summary>The kind of thing a mesh path is, from the prefix the synthetic mesh sources give their paths.</summary>
    public static string Categorize(string meshPath)
    {
        if (meshPath.StartsWith(GrassLodSource.Prefix, StringComparison.Ordinal)) return LodLayers.Grass;
        if (meshPath.StartsWith(Tree3DSource.Prefix, StringComparison.Ordinal)) return LodLayers.Tree3D;
        if (meshPath.StartsWith(TreeCardSource.Prefix, StringComparison.Ordinal)) return LodLayers.TreeCard;
        return LodLayers.Objects;
    }

    // Rough bytes per vertex / per triangle in a .bto (position, uv, normal, tangent, colour; 3 x ushort indices).
    // Only used to split the real file size between categories, never reported as a size itself.
    private const double VertexBytes = 40, TriangleBytes = 6;

    /// <summary>Lines for the log: the base files per level split by category (shares of the real bytes), then the whole output per season.</summary>
    public static IEnumerable<string> Lines(GenerateStats gen, string outputFolder)
    {
        if (gen.BytesByLevel is { Count: > 0 } byLevel && gen.SizeBreakdown is { } breakdown)
        {
            foreach (var level in byLevel.Keys.OrderBy(l => l))
            {
                var parts = breakdown.Where(kv => kv.Key.Level == level)
                    .Select(kv => (Cat: LodLayers.Display(kv.Key.Category), Est: EstimatedBytes(kv.Value.Triangles, kv.Value.Vertices), Tris: kv.Value.Triangles))
                    .OrderByDescending(p => p.Est).ToList();
                double total = parts.Sum(p => p.Est);
                if (total <= 0) continue;
                yield return $"Size, blocks written this run, LOD{level}: {Mb(byLevel[level])} = "
                             + string.Join(", ", parts.Select(p => $"{p.Cat} {p.Est / total:P0} ({p.Tris / 1e6:F1}M tris)"));
            }
        }

        foreach (var (season, levels) in DiskBySeason(outputFolder))
            yield return $"Size on disk, {season} object LOD: {Mb(levels.Values.Sum())} (" + string.Join(", ", levels.Select(l => $"LOD{l.Key} {Mb(l.Value)}")) + ")";
    }

    /// <summary>Estimated bytes of some geometry in a .bto, only used to split the real file size between layers.</summary>
    public static double EstimatedBytes(long triangles, long vertices) => vertices * VertexBytes + triangles * TriangleBytes;

    /// <summary>Object LOD (.bto) on disk: season ("base", "WIN", ...) -> LOD level -> bytes.</summary>
    public static SortedDictionary<string, SortedDictionary<int, long>> DiskBySeason(string outputFolder)
    {
        var bySeason = new SortedDictionary<string, SortedDictionary<int, long>>(StringComparer.Ordinal);
        var meshes = Path.Combine(outputFolder, "meshes", "Terrain");
        if (!Directory.Exists(meshes)) return bySeason;
        foreach (var file in Directory.EnumerateFiles(meshes, "*.bto", SearchOption.AllDirectories))
        {
            // <world>.<level>.<x>.<y>[.<SUF>].bto
            var name = Path.GetFileNameWithoutExtension(file).Split('.');
            if (name.Length < 4 || !int.TryParse(name[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) continue;
            var season = name.Length >= 5 ? name[4].ToUpperInvariant() : "base";
            if (!bySeason.TryGetValue(season, out var levels)) bySeason[season] = levels = [];
            levels[level] = levels.GetValueOrDefault(level) + new FileInfo(file).Length;
        }
        return bySeason;
    }

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F2} GB" : $"{bytes / 1048576.0:F0} MB";
}
