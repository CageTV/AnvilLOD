using System.Numerics;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;

namespace AnvilLOD.Meshes;

/// <summary>
/// A grass billboard: textures plus the size and base offset from its TexGen <c>.txt</c>. With <paramref name="Rect"/> the
/// textures are the grass atlas and the quad's UVs are mapped into that rectangle (so every grass type shares one material).
/// </summary>
public sealed record GrassBillboard(string Diffuse, string? Normal, float Width, float Height, float ShiftZ, AtlasRect? Rect = null);

/// <summary>
/// Turns the grass of one cell (from the grass cache) into LOD geometry: per kept instance two crossed, upright,
/// double-sided, alpha-tested quads (like DynDOLOD's flat 2x2 grass billboards, so they never vanish edge-on).
/// Only a sample of the instances is kept: the ground is cut into bins and each occupied bin keeps ONE tuft (placed at
/// the centre of the bin's grass, as wide as the grass in that bin covers, capped at the bin), so coverage is even and
/// follows the real grass; random sampling left holes and clumps that read as big blocky patches.
/// Vertex colours darken the bottom to fake the shadow inside the grass.
/// Each grass type becomes one part (one material), so a block gets one draw call per grass type.
/// Coordinates are relative to <paramref name="origin"/> (the cell's south-west corner).
/// </summary>
public static class GrassPatchBuilder
{
    public const ushort AlphaTestFlags = 0x12EC; // alpha test on, blending off
    public const byte AlphaThreshold = 128;

    /// <summary>A tuft may be this much wider than its bin (slight overlap hides the seams between bins).</summary>
    private const float BinOverlap = 1.15f;
    private const float MaxTaller = 1.6f;
    /// <summary>Default vertex-colour brightness (share of white): darker at the roots, slightly dimmed at the top (LOD has no grass shading).</summary>
    public const float DefaultTop = 0.85f, DefaultBottom = 0.5f;

    /// <summary>Opaque grey vertex colour (RGBA little-endian) for a brightness between 0 and about 1.5 (values above 1 clamp to white).</summary>
    internal static uint Grey(float brightness)
    {
        uint v = (uint)Math.Clamp((int)MathF.Round(brightness * 255f), 0, 255);
        return 0xFF000000u | v << 16 | v << 8 | v;
    }

    /// <summary>Candidate bin sizes in game units; the smallest that keeps no more tufts than the density asks for is used.</summary>
    private static readonly float[] Spacings = [12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256, 320, 384, 512, 768, 1024];

    /// <summary>One kept tuft: where, how big (scale), which way, and how many cached instances it stands for.</summary>
    internal readonly record struct Tuft(Vector3 Position, float Scale, float Yaw, int Count, float Spacing);

    public static LodMesh Build(string path, Vector3 origin, IEnumerable<(GrassBillboard Billboard, IReadOnlyList<GrassInstance> Instances)> types,
        float density, float sizeScale, float top = DefaultTop, float bottom = DefaultBottom)
    {
        uint bottomColor = Grey(bottom), topColor = Grey(top);

        // Types that share textures (the atlas: all of them) go into the same parts, so a block needs few draw calls.
        var groups = new Dictionary<string, (LodMaterial Material, List<(GrassBillboard Bb, Tuft T)> Items)>(StringComparer.Ordinal);
        foreach (var (bb, instances) in types)
        {
            var kept = Stratify(instances, origin, density);
            if (kept.Count == 0) continue;
            var key = bb.Diffuse + "|" + bb.Normal;
            if (!groups.TryGetValue(key, out var group))
            {
                var material = new LodMaterial(
                    [bb.Diffuse, bb.Normal ?? "", "", "", "", "", "", "", ""],
                    0x8000_0300u,
                    0x0000_0005u | 0x10u, // double-sided
                    3,
                    true, AlphaTestFlags, AlphaThreshold,
                    Vector3.Zero, 1f);
                groups[key] = group = (material, []);
            }
            foreach (var t in kept) group.Items.Add((bb, t));
        }

        var parts = new List<LodMeshPart>();
        foreach (var (material, items) in groups.Values)
        {
            // Split so no part exceeds 65,535 vertices (8 per tuft: two crossed quads).
            const int MaxQuads = 8_000;
            for (int start = 0; start < items.Count; start += MaxQuads)
            {
                int n = Math.Min(MaxQuads, items.Count - start);
                var pos = new Vector3[n * 8];
                var uv = new Vector2[n * 8];
                var nrm = new Vector3[n * 8];
                var tan = new Vector3[n * 8];
                var bit = new Vector3[n * 8];
                var col = new uint[n * 8];
                var tris = new ushort[n * 12];
                for (int q = 0; q < n; q++)
                {
                    var (bb, g) = items[start + q];
                    float s = g.Scale * sizeScale;
                    float w0 = bb.Width * s;
                    // As wide as the grass in the bin would cover, but never wider than the bin itself.
                    float w = w0 <= 0 ? 0 : Math.Clamp(w0 * MathF.Sqrt(g.Count), w0, MathF.Max(w0, g.Spacing * BinOverlap));
                    float taller = w0 <= 0 ? 1f : MathF.Min(w / w0, MaxTaller);
                    float hw = w * 0.5f, h = bb.Height * s * taller;
                    var basePos = g.Position - origin + new Vector3(0, 0, bb.ShiftZ * g.Scale);
                    var (u0, v0, u1, v1) = bb.Rect is { } r ? (r.U0, r.V0, r.U1, r.V1) : (0f, 0f, 1f, 1f);
                    for (int c = 0; c < 2; c++)
                    {
                        float yaw = g.Yaw + c * MathF.PI * 0.5f;
                        var dir = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
                        int v = q * 8 + c * 4;
                        pos[v] = basePos - dir * hw;
                        pos[v + 1] = basePos + dir * hw;
                        pos[v + 2] = basePos + dir * hw + new Vector3(0, 0, h);
                        pos[v + 3] = basePos - dir * hw + new Vector3(0, 0, h);
                        uv[v] = new Vector2(u0, v1); uv[v + 1] = new Vector2(u1, v1); uv[v + 2] = new Vector2(u1, v0); uv[v + 3] = new Vector2(u0, v0);
                        // "Sphere" normals (DynDOLOD's grass templates do the same): mostly up, leaning out to the sides,
                        // so the tuft is lit like a soft clump instead of a flat card.
                        var side = Vector3.Cross(Vector3.UnitZ, dir);
                        nrm[v] = Vector3.Normalize(Vector3.UnitZ - dir * 0.5f);
                        nrm[v + 1] = Vector3.Normalize(Vector3.UnitZ + dir * 0.5f);
                        nrm[v + 2] = Vector3.Normalize(Vector3.UnitZ + dir * 0.35f);
                        nrm[v + 3] = Vector3.Normalize(Vector3.UnitZ - dir * 0.35f);
                        for (int k = 0; k < 4; k++) { tan[v + k] = dir; bit[v + k] = side; }
                        col[v] = col[v + 1] = bottomColor;
                        col[v + 2] = col[v + 3] = topColor;
                        int t = q * 12 + c * 6;
                        tris[t] = (ushort)v; tris[t + 1] = (ushort)(v + 1); tris[t + 2] = (ushort)(v + 2);
                        tris[t + 3] = (ushort)v; tris[t + 4] = (ushort)(v + 2); tris[t + 5] = (ushort)(v + 3);
                    }
                }
                parts.Add(new LodMeshPart
                {
                    Material = material, Positions = pos, UVs = uv, Normals = nrm, Tangents = tan, Bitangents = bit, Colors = col, Triangles = tris,
                });
            }
        }
        return new LodMesh { Path = path, Parts = parts };
    }

    /// <summary>
    /// Picks the tufts for one grass type in one cell: bins of the smallest candidate size that yields no more tufts
    /// than <c>density</c> x instances, one tuft per occupied bin at the centroid of the bin's instances (yaw from the
    /// instance nearest that centroid). Deterministic, so re-runs keep the same tufts.
    /// </summary>
    internal static List<Tuft> Stratify(IReadOnlyList<GrassInstance> instances, Vector3 origin, float density)
    {
        var result = new List<Tuft>();
        int n = instances.Count;
        if (n == 0) return result;
        if (density >= 1f)
        {
            foreach (var g in instances) result.Add(new Tuft(g.Position, g.Scale, g.Yaw, 1, 0f));
            return result;
        }

        int target = Math.Max(1, (int)MathF.Round(n * density));
        // Occupied-bin count only falls as the bins grow, so binary-search the candidates.
        int lo = 0, hi = Spacings.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (OccupiedBins(instances, origin, Spacings[mid]) <= target) hi = mid; else lo = mid + 1;
        }
        float spacing = Spacings[lo];

        var bins = new Dictionary<long, Bin>();
        foreach (var g in instances)
        {
            ref var b = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(bins, Key(g.Position, origin, spacing), out _);
            b.Sum += g.Position; b.Scale += g.Scale; b.Count++;
        }
        var best = new Dictionary<long, (float Dist, float Yaw)>(bins.Count);
        foreach (var g in instances)
        {
            long key = Key(g.Position, origin, spacing);
            var c = bins[key].Sum / bins[key].Count;
            float d = Vector2.DistanceSquared(new Vector2(g.Position.X, g.Position.Y), new Vector2(c.X, c.Y));
            if (!best.TryGetValue(key, out var cur) || d < cur.Dist) best[key] = (d, g.Yaw);
        }
        foreach (var (key, b) in bins.OrderBy(kv => kv.Key))
            result.Add(new Tuft(b.Sum / b.Count, b.Scale / b.Count, best[key].Yaw, b.Count, spacing));
        return result;
    }

    private struct Bin { public Vector3 Sum; public float Scale; public int Count; }

    private static long Key(Vector3 p, Vector3 origin, float spacing)
        => ((long)MathF.Floor((p.X - origin.X) / spacing) + 4096) << 32 | (uint)((long)MathF.Floor((p.Y - origin.Y) / spacing) + 4096);

    private static int OccupiedBins(IReadOnlyList<GrassInstance> instances, Vector3 origin, float spacing)
    {
        var seen = new HashSet<long>();
        foreach (var g in instances) seen.Add(Key(g.Position, origin, spacing));
        return seen.Count;
    }
}
