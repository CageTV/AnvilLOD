using System.Numerics;
using AnvilLOD.Core.World;

namespace AnvilLOD.Meshes;

/// <summary>A grass billboard: textures plus the size and base offset from its TexGen <c>.txt</c>.</summary>
public sealed record GrassBillboard(string Diffuse, string? Normal, float Width, float Height, float ShiftZ);

/// <summary>
/// Turns the grass of one cell (from the grass cache) into LOD geometry: per kept instance two crossed, upright,
/// double-sided, alpha-tested quads (like DynDOLOD's flat 2x2 grass billboards, so they never vanish edge-on).
/// Only a sample of the instances is kept, so the kept tufts are made wider (and a bit taller) to cover about as
/// much ground as the full grass does; vertex colours darken the bottom to fake the shadow inside the grass.
/// Each grass type becomes one part (one material), so a block gets one draw call per grass type.
/// Coordinates are relative to <paramref name="origin"/> (the cell's south-west corner).
/// </summary>
public static class GrassPatchBuilder
{
    public const ushort AlphaTestFlags = 0x12EC; // alpha test on, blending off
    public const byte AlphaThreshold = 128;

    /// <summary>Share of the ground the kept tufts should cover together, relative to the full grass.</summary>
    private const float CoverageTarget = 0.6f;
    private const float MaxWiden = 5f;
    private const float MaxTaller = 1.6f;
    // Vertex colours (RGBA little-endian): darker at the roots, slightly dimmed at the top (LOD has no grass shading).
    private const uint BottomColor = 0xFF_80_80_80;
    private const uint TopColor = 0xFF_D9_D9_D9;

    public static LodMesh Build(string path, Vector3 origin, IEnumerable<(GrassBillboard Billboard, IReadOnlyList<GrassInstance> Instances)> types,
        float density, float sizeScale, int seed)
    {
        var parts = new List<LodMeshPart>();
        int typeNo = 0;
        // Keep the covered area: n·d tufts at w² each ≈ n tufts at full size → widen by 1/√d (capped).
        float widen = Math.Clamp(MathF.Sqrt(CoverageTarget / Math.Max(density, 1e-3f)), 1f, MaxWiden);
        float taller = MathF.Min(widen, MaxTaller);
        foreach (var (bb, instances) in types)
        {
            int typeSeed = seed + 7919 * typeNo++;
            var kept = new List<GrassInstance>();
            for (int i = 0; i < instances.Count; i++)
                if (Keep(typeSeed, i, density)) kept.Add(instances[i]);
            if (kept.Count == 0) continue;

            var material = new LodMaterial(
                [bb.Diffuse, bb.Normal ?? "", "", "", "", "", "", "", ""],
                0x8000_0300u,
                0x0000_0005u | 0x10u, // double-sided
                3,
                true, AlphaTestFlags, AlphaThreshold,
                Vector3.Zero, 1f);

            // Split so no part exceeds 65,535 vertices (8 per tuft: two crossed quads).
            const int MaxQuads = 8_000;
            for (int start = 0; start < kept.Count; start += MaxQuads)
            {
                int n = Math.Min(MaxQuads, kept.Count - start);
                var pos = new Vector3[n * 8];
                var uv = new Vector2[n * 8];
                var nrm = new Vector3[n * 8];
                var tan = new Vector3[n * 8];
                var bit = new Vector3[n * 8];
                var col = new uint[n * 8];
                var tris = new ushort[n * 12];
                for (int q = 0; q < n; q++)
                {
                    var g = kept[start + q];
                    float s = g.Scale * sizeScale;
                    float hw = bb.Width * s * widen * 0.5f, h = bb.Height * s * taller;
                    var basePos = g.Position - origin + new Vector3(0, 0, bb.ShiftZ * g.Scale);
                    for (int c = 0; c < 2; c++)
                    {
                        float yaw = g.Yaw + c * MathF.PI * 0.5f;
                        var dir = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
                        int v = q * 8 + c * 4;
                        pos[v] = basePos - dir * hw;
                        pos[v + 1] = basePos + dir * hw;
                        pos[v + 2] = basePos + dir * hw + new Vector3(0, 0, h);
                        pos[v + 3] = basePos - dir * hw + new Vector3(0, 0, h);
                        uv[v] = new Vector2(0, 1); uv[v + 1] = new Vector2(1, 1); uv[v + 2] = new Vector2(1, 0); uv[v + 3] = new Vector2(0, 0);
                        // "Sphere" normals (DynDOLOD's grass templates do the same): mostly up, leaning out to the sides,
                        // so the tuft is lit like a soft clump instead of a flat card.
                        var side = Vector3.Cross(Vector3.UnitZ, dir);
                        nrm[v] = Vector3.Normalize(Vector3.UnitZ - dir * 0.5f);
                        nrm[v + 1] = Vector3.Normalize(Vector3.UnitZ + dir * 0.5f);
                        nrm[v + 2] = Vector3.Normalize(Vector3.UnitZ + dir * 0.35f);
                        nrm[v + 3] = Vector3.Normalize(Vector3.UnitZ - dir * 0.35f);
                        for (int k = 0; k < 4; k++) { tan[v + k] = dir; bit[v + k] = side; }
                        col[v] = col[v + 1] = BottomColor;
                        col[v + 2] = col[v + 3] = TopColor;
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

    /// <summary>Deterministic per-instance sampling, so re-runs keep the same tufts.</summary>
    private static bool Keep(int seed, int index, float density)
    {
        if (density >= 1f) return true;
        uint h = (uint)seed * 0x9E3779B1u ^ (uint)index * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) < density * 0x1000000;
    }
}
