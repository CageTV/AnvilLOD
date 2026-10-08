using System.Numerics;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Meshes.Authoring;

/// <summary>
/// Writes an object LOD source mesh (<c>name_lod_N.nif</c>): a BSFadeNode with one BSTriShape per part,
/// each with its own BSLightingShaderProperty and texture set. This is the format AnvilLOD, LODGen and DynDOLOD
/// read LOD meshes from.
/// </summary>
public static class LodNifWriter
{
    public static byte[] Write(string name, IReadOnlyList<LodMeshPart> parts, string author = "AnvilLOD Mod Author tools")
    {
        var w = new NifWriter { Author = author };
        int rootName = w.String(name);
        int root = w.Reserve("BSFadeNode");
        var children = new List<int>();

        int n = 0;
        foreach (var p in parts)
        {
            if (p.TriangleCount == 0) continue;
            int shape = w.Reserve("BSTriShape");
            int shader = w.Reserve("BSLightingShaderProperty");
            int texSet = w.Reserve("BSShaderTextureSet");
            int alpha = p.Material.HasAlpha ? w.Reserve("NiAlphaProperty") : -1;
            children.Add(shape);
            int shapeName = w.String($"{name}:{n++}");
            bool colors = p.Colors is not null;

            w.Set(shape, b => WriteShape(b, p, shapeName, shader, alpha));
            w.Set(shader, b => BtoBuilder.WriteShader(b, p.Material, texSet, colors));
            w.Set(texSet, b =>
            {
                b.Write((uint)p.Material.Textures.Count);
                foreach (var t in p.Material.Textures) NifWriter.WriteSizedString(b, t);
            });
            if (alpha >= 0)
                w.Set(alpha, b =>
                {
                    NifWriter.WriteObjectNet(b, -1);
                    b.Write(p.Material.AlphaFlags);
                    b.Write(p.Material.AlphaThreshold);
                });
        }

        w.Set(root, b =>
        {
            NifWriter.WriteObjectNet(b, rootName);
            NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);
            b.Write((uint)children.Count);
            foreach (var c in children) b.Write(c);
            b.Write(0u); // effects
        });
        return w.ToBytes(root);
    }

    private static void WriteShape(BinaryWriter b, LodMeshPart p, int name, int shader, int alpha)
    {
        NifWriter.WriteObjectNet(b, name);
        NifWriter.WriteAvObject(b, 0xE, Vector3.Zero, 1f);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        var center = (min + max) * 0.5f;
        float radius = 0;
        foreach (var v in p.Positions) radius = MathF.Max(radius, Vector3.Distance(center, v));
        b.Write(center.X); b.Write(center.Y); b.Write(center.Z); b.Write(radius);

        b.Write(-1);      // skin
        b.Write(shader);
        b.Write(alpha);

        bool colors = p.Colors is not null;
        ulong attrs = colors ? 0x3BUL : 0x1BUL;
        ulong desc = colors
            ? 0x8UL | (4UL << 8) | (5UL << 16) | (6UL << 20) | (7UL << 24)
            : 0x7UL | (4UL << 8) | (5UL << 16) | (6UL << 20);
        desc |= attrs << 44;
        int vsize = colors ? 32 : 28;
        b.Write(desc);
        b.Write((ushort)p.TriangleCount);
        b.Write((ushort)p.VertexCount);
        b.Write((uint)(vsize * p.VertexCount + 6 * p.TriangleCount));
        for (int i = 0; i < p.VertexCount; i++)
        {
            var v = p.Positions[i];
            b.Write(v.X); b.Write(v.Y); b.Write(v.Z);
            b.Write(p.Bitangents[i].X);
            b.Write((Half)p.UVs[i].X); b.Write((Half)p.UVs[i].Y);
            b.Write(Transforms.EncodeByte(p.Normals[i].X)); b.Write(Transforms.EncodeByte(p.Normals[i].Y)); b.Write(Transforms.EncodeByte(p.Normals[i].Z));
            b.Write(Transforms.EncodeByte(p.Bitangents[i].Y));
            b.Write(Transforms.EncodeByte(p.Tangents[i].X)); b.Write(Transforms.EncodeByte(p.Tangents[i].Y)); b.Write(Transforms.EncodeByte(p.Tangents[i].Z));
            b.Write(Transforms.EncodeByte(p.Bitangents[i].Z));
            if (colors) b.Write(p.Colors![i]);
        }
        foreach (var t in p.Triangles) b.Write(t);
        b.Write(0u); // particle data size
    }
}
