using System.Numerics;
using System.Text.RegularExpressions;

namespace AnvilLOD.Meshes;

/// <summary>
/// Fits a mountain LOD mesh to its full model. Mountain mods ship LOD meshes that are decimated copies of the full mesh, and many of their vertices
/// lie outside the real surface by far more than any single inset can cover (Enhanced Rocks and Mountains: at LOD4 about 16% of the vertices of
/// MountainCliff04 are over 16 units outside, at LOD8 about a third). Where the engine draws the LOD and the full model together, those
/// stretches z-fight. Each vertex that lies outside is moved onto the nearest point of the full model's surface; vertices already inside stay.
/// The inset (<see cref="LodInset"/>) then pulls the whole mesh a little inside, evenly.
/// </summary>
public static class MountainFit
{
    /// <summary>A vertex further outside than this is not moved: the LOD is a different shape there, not a coarser copy.</summary>
    public const float MaxMove = 80f;

    private static readonly Regex LodName = new(@"^(?<dir>(?:.*\\)?)lod\\mountains\\(?<name>[^\\]+?)_lod_(?:0[hl]?|1)\.nif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The full model a mountain LOD mesh (LOD4 or LOD8 variant) was cut from, or null when the path is not one. Paths are relative to meshes\.</summary>
    public static string? FullModelFor(string lodPath)
    {
        var m = LodName.Match(lodPath.Replace('/', '\\').TrimStart('\\'));
        return m.Success ? m.Groups["dir"].Value + "landscape\\mountains\\" + m.Groups["name"].Value + ".nif" : null;
    }

    /// <summary>The LOD mesh with every vertex outside the full model moved onto its surface. Returns <paramref name="lod"/> itself when nothing moved.</summary>
    public static LodMesh Fit(LodMesh lod, LodMesh full, out int moved)
    {
        moved = 0;
        var tris = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)>();
        foreach (var p in full.Parts)
            for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
            {
                int i0 = p.Triangles[t], i1 = p.Triangles[t + 1], i2 = p.Triangles[t + 2];
                var a = p.Positions[i0]; var b = p.Positions[i1]; var c = p.Positions[i2];
                var n = Vector3.Cross(b - a, c - a);
                if (n.LengthSquared() < 1e-9f) continue;
                n = Vector3.Normalize(n);
                if (Vector3.Dot(n, p.Normals[i0] + p.Normals[i1] + p.Normals[i2]) < 0) n = -n; // outward, by the stored vertex normals
                tris.Add((a, b, c, n));
            }
        if (tris.Count == 0) return lod;

        var parts = new List<LodMeshPart>(lod.Parts.Count);
        var cache = new Dictionary<Vector3, Vector3>();
        foreach (var part in lod.Parts)
        {
            var pos = (Vector3[])part.Positions.Clone();
            bool changed = false;
            for (int i = 0; i < pos.Length; i++)
            {
                if (!cache.TryGetValue(pos[i], out var target))
                {
                    target = Nearest(pos[i], tris);
                    cache[pos[i]] = target;
                }
                if (target != pos[i]) { pos[i] = target; changed = true; moved++; }
            }
            parts.Add(changed ? CopyWith(part, pos) : part);
        }
        return moved == 0 ? lod : new LodMesh { Path = lod.Path, Parts = parts, Warnings = lod.Warnings, NoShapesInFile = lod.NoShapesInFile };
    }

    /// <summary>Where a vertex goes: itself when it is inside the full model (or too far to be a coarser copy), else the closest surface point.</summary>
    private static Vector3 Nearest(Vector3 v, List<(Vector3 A, Vector3 B, Vector3 C, Vector3 N)> tris)
    {
        float best = float.MaxValue; Vector3 point = v; float side = 0;
        foreach (var t in tris)
        {
            var q = ClosestOnTriangle(v, t.A, t.B, t.C);
            float d = Vector3.DistanceSquared(v, q);
            if (d < best) { best = d; point = q; side = Vector3.Dot(v - q, t.N); }
        }
        float dist = MathF.Sqrt(best);
        return side > 1e-3f && dist <= MaxMove ? point : v;
    }

    private static LodMeshPart CopyWith(LodMeshPart p, Vector3[] positions) => new()
    {
        Name = p.Name,
        Material = p.Material,
        Positions = positions,
        UVs = p.UVs,
        Normals = p.Normals,
        Tangents = p.Tangents,
        Bitangents = p.Bitangents,
        Colors = p.Colors,
        Triangles = p.Triangles,
        Tier = p.Tier,
    };

    /// <summary>Closest point of triangle ABC to P (Ericson, Real-Time Collision Detection).</summary>
    internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + d1 / (d1 - d3) * ab;
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + d2 / (d2 - d6) * ac;
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (d4 - d3) / (d4 - d3 + (d5 - d6)) * (c - b);
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
