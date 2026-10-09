using System.Globalization;
using System.Numerics;

namespace AnvilLOD.Core.World;

/// <summary>
/// One tree billboard: a front-facing image (TexGen/LODGen <c>textures\terrain\lodgen\&lt;plugin&gt;\&lt;model&gt;_&lt;formid&gt;.dds</c>)
/// plus the size and vertical offset read from the matching <c>.txt</c>.
/// </summary>
public sealed record TreeBillboard(string TexturePath, float Width, float Height, float ShiftZ)
{
    /// <summary>
    /// Parses a TexGen/LODGen billboard <c>.txt</c>. <paramref name="useFirstView"/> picks the <c>_1</c> view's
    /// width/height (when only <c>name_1.dds</c> exists).
    /// </summary>
    public static TreeBillboard? Parse(string texturePath, string text, bool useFirstView = false)
    {
        var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (float.TryParse(line[(eq + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                values[line[..eq].Trim()] = v;
        }

        float scale = values.GetValueOrDefault("Scale", 1f);
        if (scale <= 0) scale = 1f;
        float width = useFirstView && values.TryGetValue("Width_1", out var w1) ? w1 : values.GetValueOrDefault("Width");
        float height = useFirstView && values.TryGetValue("Height_1", out var h1) ? h1 : values.GetValueOrDefault("Height");
        if (width <= 0 || height <= 0) return null;
        return new TreeBillboard(texturePath, width * scale, height * scale, values.GetValueOrDefault("ShiftZ") * scale);
    }
}

/// <summary>A placed tree that gets billboard tree LOD.</summary>
public sealed record TreeReference(
    string FormKey,
    string Worldspace,
    Vector3 Position,
    float RotationZ,          // radians, as stored in the REFR
    float Scale,
    uint RuntimeFormId,       // load-order-resolved FormID (the engine matches LOD to the loaded tree by it)
    TreeBillboard Billboard,
    bool ObjectLod = false,   // copied from a child worldspace: never loads here, so it goes into object LOD as cards
    string? Model3D = null,   // data-relative path of its 3D tree LOD model (…passthru_lod.nif), when the 3D option found one
    bool Model3DByName = false) // that model was found by plain name, not by the CRC32 of the tree's mesh
{
    public CellCoord Cell => CellCoord.FromWorld(Position.X, Position.Y);
}

/// <summary>
/// 3D tree LOD: trees with a 3D tree LOD model go into object LOD (the 3D model at LOD4, optionally LOD8, billboard
/// cards further out) instead of the billboard tree LOD files. Off by default.
/// </summary>
/// <param name="Enabled">Look for 3D tree LOD models.</param>
/// <param name="Lod8">Use the 3D model at LOD8 as well (otherwise LOD4 only, billboard cards from LOD8).</param>
/// <param name="PlainNameFallback">When no model matches the tree mesh's CRC32, accept the one stored under the plain tree name.
/// It may have been made for another version of the mesh, so it's opt-in.</param>
public sealed record Tree3DSettings(bool Enabled = false, bool Lod8 = false, bool PlainNameFallback = false)
{
    public static readonly Tree3DSettings Off = new();

    /// <summary>Part of the settings fingerprint, so a changed option rebuilds the blocks it affects.</summary>
    public string Fingerprint => Enabled ? $"tree3d:{(Lod8 ? "l48" : "l4")}{(PlainNameFallback ? "+name" : "")}" : "";
}

/// <summary>
/// Writers for the engine's tree LOD files:
/// <list type="bullet">
/// <item><c>meshes\terrain\&lt;ws&gt;\trees\&lt;ws&gt;.lst</c>: per tree type, its billboard size and atlas UVs.</item>
/// <item><c>meshes\terrain\&lt;ws&gt;\trees\&lt;ws&gt;.4.x.y.btt</c>: per LOD4 block, the placed instances by type.</item>
/// </list>
/// Layouts verified against vanilla and DynDOLOD output.
/// </summary>
public static class TreeLodFiles
{
    public sealed record TreeType(int Index, float Width, float Height, float U0, float V0, float U1, float V1);

    public sealed record Instance(int Type, Vector3 Position, float RotationZ, float Scale, uint FormId);

    public static string ListPath(string ws) => GamePath.Join("meshes", "terrain", ws, "trees", ws + ".lst");
    public static string BlockPath(string ws, int x, int y) => GamePath.Join("meshes", "terrain", ws, "trees", $"{ws}.4.{x}.{y}.btt");
    public static string AtlasPath(string ws) => GamePath.Join("textures", "terrain", ws, "trees", ws + "treelod.dds");

    public static byte[] WriteList(IReadOnlyList<TreeType> types)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(types.Count);
        foreach (var t in types)
        {
            w.Write(t.Index);
            w.Write(t.Width); w.Write(t.Height);
            w.Write(t.U0); w.Write(t.V0); w.Write(t.U1); w.Write(t.V1);
            w.Write(0u);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Instances are grouped by type. Rotation is stored the way the engine expects (2π − z).</summary>
    public static byte[] WriteBlock(IEnumerable<Instance> instances)
    {
        var byType = instances.GroupBy(i => i.Type).OrderBy(g => g.Key).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(byType.Count);
        foreach (var g in byType)
        {
            var list = g.OrderBy(i => i.FormId).ToList();
            w.Write(g.Key);
            w.Write(list.Count);
            foreach (var i in list)
            {
                w.Write(i.Position.X); w.Write(i.Position.Y); w.Write(i.Position.Z);
                w.Write(EngineRotation(i.RotationZ));
                w.Write(i.Scale);
                w.Write(i.FormId);
                w.Write(0u); w.Write(0u);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Empty block: overrides a vanilla .btt whose type indices no longer match our .lst.</summary>
    public static byte[] EmptyBlock() => BitConverter.GetBytes(0);

    public static float EngineRotation(float rz)
    {
        const float TwoPi = MathF.PI * 2f;
        float r = TwoPi - (rz % TwoPi);
        if (r > TwoPi) r -= TwoPi;
        return r;
    }
}
