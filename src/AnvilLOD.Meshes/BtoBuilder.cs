using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Meshes;

/// <summary>
/// Builds one object-LOD block (.bto) in the layout the engine and LODGen use:
/// <code>
/// NiNode "obj"
///  └─ BSMultiBoundNode (one per material chunk)
///      ├─ BSSubIndexTriShape  (translation = block SW corner, scale = LOD level)
///      │    ├─ BSLightingShaderProperty → BSShaderTextureSet
///      │    └─ NiAlphaProperty (if the source used alpha)
///      └─ BSMultiBound → BSMultiBoundAABB (world-space box)
/// </code>
/// Vertices are stored block-local and divided by the level, exactly as LODGen does.
/// Geometry sharing a material is merged into one shape (one draw call), split only when
/// a shape would exceed 65,535 vertices or triangles.
/// </summary>
public static class BtoBuilder
{
    public const string Author = "AnvilLOD";
    private const int MaxIndex = ushort.MaxValue;

    public sealed record Result(byte[] Bytes, int Shapes, int Triangles, int Vertices, int RefsPlaced, int RefsSkipped, int TrianglesCulled = 0);

    /// <summary>
    /// Drops triangles that are completely buried: every corner, edge midpoint and the centre must be at least
    /// <see cref="Margin"/> below the terrain. Points over cells without terrain data always keep the triangle.
    /// </summary>
    public sealed class TerrainTest(Func<float, float, float?> heightAt, float margin)
    {
        public float Margin { get; } = margin;

        public bool Buried(Vector3 a, Vector3 b, Vector3 c) =>
            Below(a) && Below(b) && Below(c)
            && Below((a + b) * 0.5f) && Below((b + c) * 0.5f) && Below((c + a) * 0.5f)
            && Below((a + b + c) / 3f);

        private bool Below(Vector3 p) => heightAt(p.X, p.Y) is { } h && p.Z < h - Margin;
    }

    /// <param name="terrain">Optional buried-triangle test (see <see cref="TerrainTest"/>).</param>
    public static Result Build(QuadKey quad, IReadOnlyList<LodReference> refs, Func<string, LodMesh?> meshes, TerrainTest? terrain = null)
    {
        var origin = new Vector3(quad.X * CellCoord.CellSize, quad.Y * CellCoord.CellSize, 0);
        float level = (int)quad.Level;
        // LOD4 shapes carry one segment per cell (x-major, 4x4) so the engine can hide the cells that are
        // loaded with full models. Without them the LOD stays visible up close and inside child worlds.
        bool segmented = quad.Level == LodLevel.Lod4;
        float inv = 1f / level;

        var chunks = new Dictionary<string, List<Chunk>>(StringComparer.Ordinal);
        int placed = 0, skipped = 0, culled = 0;

        foreach (var r in refs)
        {
            var path = r.Meshes.For(quad.Level);
            var mesh = path is null ? null : meshes(path);
            if (mesh is null || mesh.Parts.Count == 0) { skipped++; continue; }

            int segment = 0;
            if (segmented)
            {
                var cell = r.Cell;
                segment = Math.Clamp(cell.X - quad.X, 0, 3) * 4 + Math.Clamp(cell.Y - quad.Y, 0, 3);
            }
            var m = Transforms.Reference(r.Position, r.RotationRadians, r.Scale);
            var rot = Transforms.RotationOnly(m);
            placed++;

            foreach (var part in mesh.Parts)
            {
                if (part.VertexCount > MaxIndex || part.TriangleCount > MaxIndex) continue; // can't happen for valid SSE shapes
                if (!chunks.TryGetValue(part.Material.Key, out var list))
                    chunks[part.Material.Key] = list = [new Chunk(part.Material, segmented)];
                var chunk = list[^1];
                if (chunk.Vertices + part.VertexCount > MaxIndex || chunk.TriangleCount + part.TriangleCount > MaxIndex)
                    list.Add(chunk = new Chunk(part.Material, segmented));
                culled += chunk.Append(part, m, rot, origin, inv, terrain, segment);
            }
        }

        var w = new NifWriter { Author = Author };
        int objName = w.String("obj");
        int root = w.Reserve("NiNode");
        var ordered = chunks.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value).Where(c => c.TriangleCount > 0).ToList();
        var children = new List<int>();
        int tris = 0, verts = 0;

        foreach (var c in ordered)
        {
            int mbNode = w.Reserve("BSMultiBoundNode");
            int shape = w.Reserve("BSSubIndexTriShape");
            int shader = w.Reserve("BSLightingShaderProperty");
            int texSet = w.Reserve("BSShaderTextureSet");
            int alpha = c.Material.HasAlpha ? w.Reserve("NiAlphaProperty") : -1;
            int mb = w.Reserve("BSMultiBound");
            int aabb = w.Reserve("BSMultiBoundAABB");
            children.Add(mbNode);
            tris += c.TriangleCount;
            verts += c.Vertices;

            w.Set(mbNode, b =>
            {
                NifWriter.WriteObjectNet(b, -1);
                NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);
                b.Write(1u); b.Write(shape);
                b.Write(0u);          // effects
                b.Write(mb);
                b.Write(1u);          // culling mode
            });
            w.Set(shape, b => c.WriteShape(b, objName, origin, level, shader, alpha));
            w.Set(shader, b => WriteShader(b, c.Material, texSet, c.HasColors));
            w.Set(texSet, b =>
            {
                b.Write((uint)c.Material.Textures.Count);
                foreach (var t in c.Material.Textures) NifWriter.WriteSizedString(b, t);
            });
            if (alpha >= 0)
                w.Set(alpha, b =>
                {
                    NifWriter.WriteObjectNet(b, -1);
                    b.Write(c.Material.AlphaFlags);
                    b.Write(c.Material.AlphaThreshold);
                });
            w.Set(mb, b => b.Write(aabb));
            w.Set(aabb, b =>
            {
                var (center, extent) = c.WorldBox(origin, level);
                b.Write(center.X); b.Write(center.Y); b.Write(center.Z);
                b.Write(extent.X); b.Write(extent.Y); b.Write(extent.Z);
            });
        }

        w.Set(root, b =>
        {
            NifWriter.WriteObjectNet(b, objName);
            NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);
            b.Write((uint)children.Count);
            foreach (var ch in children) b.Write(ch);
            b.Write(0u); // effects
        });

        return new Result(w.ToBytes(root), ordered.Count, tris, verts, placed, skipped, culled);
    }

    internal static void WriteShader(BinaryWriter b, LodMaterial m, int texSet, bool colors)
    {
        b.Write(0u);                          // shader type: default
        NifWriter.WriteObjectNet(b, -1);
        b.Write(m.ShaderFlags1);
        b.Write(m.ShaderFlags2 | (colors ? 0x20u : 0u));
        b.Write(0f); b.Write(0f);             // uv offset
        b.Write(1f); b.Write(1f);             // uv scale
        b.Write(texSet);
        b.Write(m.EmissiveColor.X); b.Write(m.EmissiveColor.Y); b.Write(m.EmissiveColor.Z);
        b.Write(m.EmissiveMultiple);
        b.Write(m.ClampMode);
        b.Write(1f);                          // alpha
        b.Write(0f);                          // refraction
        b.Write(1f);                          // glossiness (LODGen value)
        b.Write(1f); b.Write(1f); b.Write(1f);// specular color
        b.Write(1f);                          // specular strength
        b.Write(0f); b.Write(0f);             // lighting effects
    }

    /// <summary>Merged geometry for one material, in block-local, level-scaled coordinates.</summary>
    private sealed class Chunk(LodMaterial material, bool segmented)
    {
        public LodMaterial Material { get; } = material;
        private readonly List<Vector3> _pos = [];
        private readonly List<Vector2> _uv = [];
        private readonly List<Vector3> _n = [];
        private readonly List<Vector3> _t = [];
        private readonly List<Vector3> _b = [];
        private readonly List<uint> _c = [];
        private readonly List<ushort>[] _segTris = Enumerable.Range(0, segmented ? 16 : 1).Select(_ => new List<ushort>()).ToArray();
        private int _indexCount;
        public bool HasColors { get; private set; }

        public int Vertices => _pos.Count;
        public int TriangleCount => _indexCount / 3;

        /// <returns>Number of triangles dropped because they were buried under the terrain.</returns>
        public int Append(LodMeshPart p, Matrix4x4 m, Matrix4x4 rot, Vector3 origin, float inv, TerrainTest? terrain, int segment)
        {
            var world = new Vector3[p.VertexCount];
            for (int i = 0; i < world.Length; i++) world[i] = Vector3.Transform(p.Positions[i], m);

            // Decide which triangles survive, then copy only the vertices they use.
            var keep = new bool[p.TriangleCount];
            int culled = 0;
            for (int t = 0; t < keep.Length; t++)
            {
                keep[t] = terrain is null || !terrain.Buried(world[p.Triangles[t * 3]], world[p.Triangles[t * 3 + 1]], world[p.Triangles[t * 3 + 2]]);
                if (!keep[t]) culled++;
            }

            var tris = _segTris[segmented ? segment : 0];
            var remap = new int[p.VertexCount];
            Array.Fill(remap, -1);
            for (int t = 0; t < keep.Length; t++)
            {
                if (!keep[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int i = p.Triangles[t * 3 + k];
                    if (remap[i] < 0)
                    {
                        remap[i] = _pos.Count;
                        _pos.Add((world[i] - origin) * inv);
                        _uv.Add(p.UVs[i]);
                        _n.Add(Transforms.SafeNormalize(Vector3.TransformNormal(p.Normals[i], rot), Vector3.UnitZ));
                        _t.Add(Transforms.SafeNormalize(Vector3.TransformNormal(p.Tangents[i], rot), Vector3.UnitX));
                        _b.Add(Transforms.SafeNormalize(Vector3.TransformNormal(p.Bitangents[i], rot), Vector3.UnitY));
                        _c.Add(p.Colors?[i] ?? 0xFFFF_FFFFu);
                    }
                    tris.Add((ushort)remap[i]);
                    _indexCount++;
                }
            }
            if (p.Colors is not null && culled < keep.Length) HasColors = true;
            return culled;
        }

        public (Vector3 Center, Vector3 Extent) WorldBox(Vector3 origin, float level)
        {
            var (min, max) = LocalBounds();
            var wmin = origin + min * level;
            var wmax = origin + max * level;
            return ((wmin + wmax) * 0.5f, (wmax - wmin) * 0.5f);
        }

        private (Vector3 Min, Vector3 Max) LocalBounds()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in _pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (min, max);
        }

        public void WriteShape(BinaryWriter b, int name, Vector3 origin, float level, int shader, int alpha)
        {
            NifWriter.WriteObjectNet(b, name);
            NifWriter.WriteAvObject(b, 0xE, origin, level);

            var (min, max) = LocalBounds();
            var center = (min + max) * 0.5f;
            float radius = 0;
            foreach (var p in _pos) radius = MathF.Max(radius, Vector3.Distance(center, p));
            b.Write(center.X); b.Write(center.Y); b.Write(center.Z); b.Write(radius);

            b.Write(-1);      // skin
            b.Write(shader);
            b.Write(alpha);

            // Vertex layout: position(12) bitangentX(4) uv(4) normal+bitangentY(4) tangent+bitangentZ(4) [color(4)]
            ulong attrs = HasColors ? 0x3BUL : 0x1BUL;
            ulong desc = HasColors
                ? 0x8UL | (4UL << 8) | (5UL << 16) | (6UL << 20) | (7UL << 24)
                : 0x7UL | (4UL << 8) | (5UL << 16) | (6UL << 20);
            desc |= attrs << 44;
            int vsize = HasColors ? 32 : 28;
            b.Write(desc);
            b.Write((ushort)TriangleCount);
            b.Write((ushort)Vertices);
            b.Write((uint)(vsize * Vertices + 6 * TriangleCount));

            for (int i = 0; i < _pos.Count; i++)
            {
                var p = _pos[i];
                b.Write(p.X); b.Write(p.Y); b.Write(p.Z);
                b.Write(_b[i].X);
                b.Write((Half)_uv[i].X); b.Write((Half)_uv[i].Y);
                b.Write(Transforms.EncodeByte(_n[i].X)); b.Write(Transforms.EncodeByte(_n[i].Y)); b.Write(Transforms.EncodeByte(_n[i].Z));
                b.Write(Transforms.EncodeByte(_b[i].Y));
                b.Write(Transforms.EncodeByte(_t[i].X)); b.Write(Transforms.EncodeByte(_t[i].Y)); b.Write(Transforms.EncodeByte(_t[i].Z));
                b.Write(Transforms.EncodeByte(_b[i].Z));
                if (HasColors) b.Write(_c[i]);
            }
            foreach (var seg in _segTris)
                foreach (var t in seg) b.Write(t);

            b.Write(0u);               // particle data size

            // Segments: start index (into the index array) + triangle count. Trailing empty cells are left
            // out, as LODGen does; empty cells in between get (0, 0).
            int last = _segTris.Length - 1;
            while (last > 0 && _segTris[last].Count == 0) last--;
            b.Write((uint)(last + 1));
            uint start = 0;
            for (int k = 0; k <= last; k++)
            {
                uint n = (uint)(_segTris[k].Count / 3);
                b.Write((byte)0);
                b.Write(n == 0 ? 0u : start);
                b.Write(n);
                start += (uint)_segTris[k].Count;
            }
        }
    }
}
