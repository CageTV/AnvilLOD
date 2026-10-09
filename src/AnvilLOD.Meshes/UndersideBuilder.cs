using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Meshes;

/// <summary>
/// Writes <c>meshes\Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c>, laid out like DynDOLOD's:
/// <code>
/// BSFadeNode
///  └─ BSMultiBoundNode × one per LOD32 block
///      ├─ BSTriShape (translation = block SW corner, scale 32)
///      │    └─ BSLightingShaderProperty → BSShaderTextureSet (terrain LOD diffuse of the block + black.dds)
///      └─ BSMultiBound → BSMultiBoundAABB
/// </code>
/// Vertices are position + UV only (20 bytes), the faces point down, and the shapes cast shadows.
/// </summary>
public static class UndersideBuilder
{
    public const string Author = "AnvilLOD";
    public const float Scale = 32f;

    /// <summary>The path the engine-side placement points at, e.g. <c>Terrain\Tamriel\Tamriel_Underside.nif</c> (relative to meshes).</summary>
    public static string ModelPath(string worldspace) => $"Terrain\\{worldspace}\\{worldspace}_Underside.nif";

    public static string RelativePath(string worldspace) => GamePath.Join("meshes", "Terrain", worldspace, $"{worldspace}_Underside.nif");

    public static string DiffusePath(string worldspace, int blockX, int blockY) =>
        $"Textures\\Terrain\\{worldspace}\\{worldspace}.32.{blockX}.{blockY}.dds";

    // ZBuffer_Test | Cast_Shadows | Model_Space_Normals, ZBuffer_Write: the flags DynDOLOD's underside shapes carry.
    private const uint ShaderFlags1 = 0x8000_1200;
    private const uint ShaderFlags2 = 0x0000_0001;

    public sealed record Result(byte[] Bytes, int Shapes, int Triangles, int Vertices);

    /// <returns>The NIF, or null when there is nothing to write.</returns>
    public static Result? Build(string worldspace, IReadOnlyList<UndersideMesher.Shape> shapes)
    {
        shapes = shapes.Where(s => s.Positions.Length > 0 && s.Indices.Length > 0).OrderBy(s => s.BlockX).ThenBy(s => s.BlockY).ToList();
        if (shapes.Count == 0) return null;

        var w = new NifWriter { Author = Author };
        int root = w.Reserve("BSFadeNode");
        var children = new List<int>();
        int tris = 0, verts = 0;

        // One BSMultiBoundNode per block, each with its own world-space box, as DynDOLOD writes it.
        foreach (var mesh in shapes)
        {
            int mbNode = w.Reserve("BSMultiBoundNode");
            int shape = w.Reserve("BSTriShape");
            int shader = w.Reserve("BSLightingShaderProperty");
            int texSet = w.Reserve("BSShaderTextureSet");
            int mb = w.Reserve("BSMultiBound");
            int aabb = w.Reserve("BSMultiBoundAABB");
            children.Add(mbNode);
            tris += mesh.TriangleCount;
            verts += mesh.Positions.Length;

            var origin = new Vector3(mesh.BlockX * CellCoord.CellSize, mesh.BlockY * CellCoord.CellSize, 0);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in mesh.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }

            w.Set(mbNode, b =>
            {
                NifWriter.WriteObjectNet(b, -1);
                NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);
                b.Write(1u); b.Write(shape);
                b.Write(0u);          // effects
                b.Write(mb);
                b.Write(0u);          // culling mode
            });
            w.Set(shape, b => WriteShape(b, mesh, origin, shader));
            w.Set(shader, b => BtoBuilder.WriteShader(b, Material, texSet, colors: false));
            w.Set(texSet, b =>
            {
                b.Write(2u);
                NifWriter.WriteSizedString(b, DiffusePath(worldspace, mesh.BlockX, mesh.BlockY));
                NifWriter.WriteSizedString(b, "Textures\\black.dds");
            });
            w.Set(mb, b => b.Write(aabb));
            w.Set(aabb, b =>
            {
                var center = (min + max) * 0.5f;
                var extent = (max - min) * 0.5f;
                b.Write(center.X); b.Write(center.Y); b.Write(center.Z);
                b.Write(extent.X); b.Write(extent.Y); b.Write(extent.Z);
            });
        }

        w.Set(root, b =>
        {
            NifWriter.WriteObjectNet(b, -1);
            NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);
            b.Write((uint)children.Count);
            foreach (var c in children) b.Write(c);
            b.Write(0u); // effects
        });

        return new Result(w.ToBytes(root), shapes.Count, tris, verts);
    }

    private static readonly LodMaterial Material = new(
        Textures: [], ShaderFlags1: ShaderFlags1, ShaderFlags2: ShaderFlags2, ClampMode: 0,
        HasAlpha: false, AlphaFlags: 0, AlphaThreshold: 0,
        EmissiveColor: Vector3.Zero, EmissiveMultiple: 1f);

    private static void WriteShape(BinaryWriter b, UndersideMesher.Shape mesh, Vector3 origin, int shader)
    {
        NifWriter.WriteObjectNet(b, -1);
        NifWriter.WriteAvObject(b, 0xE, origin, Scale);

        var local = new Vector3[mesh.Positions.Length];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < local.Length; i++)
        {
            local[i] = (mesh.Positions[i] - origin) / Scale;
            min = Vector3.Min(min, local[i]);
            max = Vector3.Max(max, local[i]);
        }
        var center = (min + max) * 0.5f;
        float radius = 0;
        foreach (var p in local) radius = MathF.Max(radius, Vector3.Distance(center, p));
        b.Write(center.X); b.Write(center.Y); b.Write(center.Z); b.Write(radius);

        b.Write(-1);      // skin
        b.Write(shader);
        b.Write(-1);      // alpha property

        // Vertex layout: position(12) + unused float(4) + uv(4). No normals, tangents or colours.
        const ulong desc = 0x0000_3000_0000_0405UL;
        const int vsize = 20;
        b.Write(desc);
        b.Write((ushort)mesh.TriangleCount);
        b.Write((ushort)local.Length);
        b.Write((uint)(vsize * local.Length + 6 * mesh.TriangleCount));

        // UVs run across the block: u west to east, v north to south.
        const float span = CellCoord.CellSize * UndersideMesher.BlockCells;
        foreach (var (p, world) in local.Zip(mesh.Positions))
        {
            b.Write(p.X); b.Write(p.Y); b.Write(p.Z);
            b.Write(0f);
            b.Write((Half)((world.X - origin.X) / span));
            b.Write((Half)(1f - (world.Y - origin.Y) / span));
        }
        foreach (var i in mesh.Indices) b.Write(i);
        b.Write(0u);      // particle data size
    }
}
