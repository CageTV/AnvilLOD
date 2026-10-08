using System.Numerics;

namespace AnvilLOD.Meshes.Authoring;

/// <summary>Settings for one generated LOD level.</summary>
/// <param name="MaxError">Largest surface deviation allowed by the simplifier, in game units.</param>
/// <param name="MinPartSize">Parts whose bounding box diagonal is smaller than this are dropped (details nobody sees from afar).</param>
public sealed record AuthorLevel(int Level, float MaxError, float MinPartSize);

/// <summary>
/// Turns a full model into object LOD source meshes (<c>name_lod_0/1/2.nif</c>): small parts are dropped, the rest
/// is simplified with <see cref="MeshSimplifier"/>, textures stay those of the full model.
/// </summary>
public static class LodMeshAuthor
{
    public static readonly AuthorLevel[] DefaultLevels =
    [
        new(0, 6f, 32f),     // LOD4: about 4–8 cells out
        new(1, 20f, 96f),    // LOD8
        new(2, 60f, 256f),   // LOD16
    ];

    public sealed record LevelResult(int Level, IReadOnlyList<LodMeshPart> Parts, int Triangles, int DroppedParts);

    public static List<LevelResult> Build(LodMesh full, IReadOnlyList<AuthorLevel>? levels = null)
    {
        levels ??= DefaultLevels;
        var results = new List<LevelResult>();
        var usable = full.Parts.Where(p => p.TriangleCount > 0 && !IsBlended(p.Material)).ToList();
        if (usable.Count == 0) return results;

        foreach (var lv in levels)
        {
            var kept = usable.Where(p => Diagonal(p) >= lv.MinPartSize).ToList();
            int dropped = usable.Count - kept.Count;
            if (kept.Count == 0)
            {
                kept = [usable.OrderByDescending(Diagonal).First()]; // always keep the main silhouette
                dropped = usable.Count - 1;
            }
            var parts = kept.Select(p => MeshSimplifier.Simplify(p, lv.MaxError)).Where(p => p.TriangleCount > 0).ToList();
            results.Add(new LevelResult(lv.Level, parts, parts.Sum(p => p.TriangleCount), dropped));
        }
        return results;
    }

    /// <summary>Alpha blending without alpha testing (decals, glass, fog cards): not drawn by object LOD anyway.</summary>
    private static bool IsBlended(LodMaterial m) => m.HasAlpha && (m.AlphaFlags & 0x0001) != 0 && (m.AlphaFlags & 0x0200) == 0;

    public static float Diagonal(LodMeshPart p)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return Vector3.Distance(min, max);
    }

    public static (Vector3 Min, Vector3 Max) Bounds(LodMesh mesh)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in mesh.Parts)
            foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }
}
