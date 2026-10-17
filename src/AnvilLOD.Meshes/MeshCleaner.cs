using System.Numerics;

namespace AnvilLOD.Meshes;

/// <summary>
/// Tidies a LOD mesh part once, when it is loaded: triangles that cover no area are dropped and vertices that are identical in
/// every attribute are merged. Nothing visible changes (a merged vertex is bit-for-bit the one it replaces), the blocks get smaller.
/// </summary>
public static class MeshCleaner
{
    /// <summary>Triangles with a cross product shorter than this (in game units squared, twice the area) have no area.</summary>
    public const float MinDoubleArea = 1e-4f;

    public readonly record struct Result(LodMeshPart Part, int TrianglesDropped, int VerticesMerged);

    public static Result Clean(LodMeshPart p)
    {
        int vertexCount = p.VertexCount;
        int triCount = p.TriangleCount;
        if (triCount == 0 || vertexCount == 0) return new Result(p, 0, 0);

        // Merge identical vertices. The key is every attribute, bit for bit.
        var map = new int[vertexCount];
        var seen = new Dictionary<VertexKey, int>(vertexCount);
        int unique = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            var key = new VertexKey(p.Positions[i], p.UVs[i], p.Normals[i], p.Tangents[i], p.Bitangents[i], p.Colors?[i] ?? 0u);
            if (seen.TryGetValue(key, out int first)) map[i] = first;
            else { seen[key] = i; map[i] = i; unique++; }
        }
        int merged = vertexCount - unique;

        // Drop triangles with a repeated corner or no area (judged on the merged corners, so a sliver between two copies of one point goes too).
        var kept = new List<ushort>(p.Triangles.Length);
        int dropped = 0;
        for (int t = 0; t < triCount; t++)
        {
            int a = map[p.Triangles[t * 3]], b = map[p.Triangles[t * 3 + 1]], c = map[p.Triangles[t * 3 + 2]];
            if (a == b || b == c || a == c || Vector3.Cross(p.Positions[b] - p.Positions[a], p.Positions[c] - p.Positions[a]).Length() < MinDoubleArea)
            {
                dropped++;
                continue;
            }
            kept.Add((ushort)a); kept.Add((ushort)b); kept.Add((ushort)c);
        }
        if (merged == 0 && dropped == 0) return new Result(p, 0, 0);

        // Compact: only the vertices a surviving triangle still points at (the merged originals and orphans disappear).
        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var nrm = new List<Vector3>();
        var tan = new List<Vector3>(); var bit = new List<Vector3>(); var col = p.Colors is null ? null : new List<uint>();
        var tris = new ushort[kept.Count];
        for (int k = 0; k < kept.Count; k++)
        {
            int i = kept[k];
            if (remap[i] < 0)
            {
                remap[i] = pos.Count;
                pos.Add(p.Positions[i]); uv.Add(p.UVs[i]); nrm.Add(p.Normals[i]); tan.Add(p.Tangents[i]); bit.Add(p.Bitangents[i]);
                col?.Add(p.Colors![i]);
            }
            tris[k] = (ushort)remap[i];
        }
        // The vertices that were merged away no longer count as removed vertices if they were orphaned anyway; report what the part lost.
        int verticesGone = vertexCount - pos.Count;
        var cleaned = new LodMeshPart
        {
            Name = p.Name, Material = p.Material, Positions = pos.ToArray(), UVs = uv.ToArray(), Normals = nrm.ToArray(),
            Tangents = tan.ToArray(), Bitangents = bit.ToArray(), Colors = col?.ToArray(), Triangles = tris,
        };
        return new Result(cleaned, dropped, verticesGone);
    }

    private readonly record struct VertexKey(Vector3 P, Vector2 Uv, Vector3 N, Vector3 T, Vector3 B, uint C);
}
