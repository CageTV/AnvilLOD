using System.Numerics;

namespace AnvilLOD.Meshes.Nif;

/// <summary>
/// Extracts drawable geometry from an SSE NIF: walks the scene graph from the root,
/// accumulates node transforms, and returns every visible BSTriShape-family shape that uses
/// a BSLightingShaderProperty, baked into mesh space.
/// </summary>
public static class NifGeometryReader
{
    // NiNode and subclasses whose children we follow. Subclass-specific fields come after the
    // children list, so the NiNode layout is enough for traversal.
    private static readonly HashSet<string> NodeTypes = new(StringComparer.Ordinal)
    {
        "NiNode", "BSFadeNode", "BSMultiBoundNode", "BSLeafAnimNode", "BSTreeNode", "BSOrderedNode",
        "BSValueNode", "NiBillboardNode", "NiSwitchNode", "NiLODNode", "BSDebrisNode", "BSBlastNode",
        "BSDamageStage", "BSRangeNode", "BSWArray", "NiSortAdjustNode", "BSMasterParticleSystem",
    };

    private static readonly HashSet<string> ShapeTypes = new(StringComparer.Ordinal)
    {
        "BSTriShape", "BSSubIndexTriShape", "BSMeshLODTriShape", "BSDynamicTriShape",
    };

    // Shader flag bits LOD keeps from the source (everything else is set the LODGen way).
    private const uint KeepF1 = 0x0000_1000 /* Model_Space_Normals */ | 0x0000_0008 /* Vertex_Alpha */;
    private const uint KeepF2 = 0x0000_0010 /* Double_Sided */ | 0x0000_0040 /* Glow_Map */;

    /// <summary>Prefix of the warning added to a mesh for every water-shader shape read as lit fake water.</summary>
    public const string WaterPlaneWarning = "WaterPlane:";

    /// <summary>Colour (RGBA, little-endian) of fake water: the dark teal and ~69% opacity of the CS Water Mod stream twins.</summary>
    public const uint FakeWaterColor = 0xAF33342Au;

    /// <summary>
    /// A BSWaterShaderProperty plane drawn outside the engine's water system comes out black, so LOD stand-ins draw it with
    /// a lit shader instead, the way CS Water Mod's stream twins do ("FakeWater": FXwaterTile01 with vertex alpha, alpha
    /// blended). Same flags, alpha property and shader values as that shape.
    /// </summary>
    public static readonly LodMaterial FakeWaterMaterial = new(
        ["textures\\effects\\FXwaterTile01.dds", "textures\\effects\\FXwaterTile01_n.dds", "", "", "", "", "", "", ""],
        0x8E40_0309u, 0x0000_8020u, 3,
        true, 0x10ED, 128,
        Vector3.Zero, 1f,
        new LodShaderPassthru(0, 0f, 0f, 1f, 1f, 1f, 0f, 202f, Vector3.One, 2f, 0.3f, 2f));

    /// <summary>The fake-water plane's UVs are tiled this many times over the source plane's 0-1 range.</summary>
    public const float FakeWaterUvTiling = 4f;

    /// <param name="passthru">Keep every shape's own shader settings (LODGen's "passthru" LOD models) instead of
    /// reducing them to the standard LOD shader.</param>
    /// <param name="waterAsLit">Read BSWaterShaderProperty shapes as lit fake water (<see cref="FakeWaterMaterial"/>) instead of skipping them.</param>
    public static LodMesh Read(string path, byte[] data, bool passthru = false, bool waterAsLit = false)
    {
        var nif = NifFile.Read(data);
        var parts = new List<LodMeshPart>();
        var warnings = new List<string>();
        var visited = new HashSet<int>();

        foreach (var root in nif.Roots)
            Walk(nif, root, Matrix4x4.Identity, parts, warnings, visited, passthru, waterAsLit);

        bool anyShape = nif.Blocks.Any(b => ShapeTypes.Contains(b.Type) || b.Type is "NiTriShape" or "NiTriStrips");
        return new LodMesh { Path = path, Parts = parts, Warnings = warnings, NoShapesInFile = !anyShape };
    }

    // ---------- traversal ----------

    private struct AvObject
    {
        public string? Name;
        public uint Flags;
        public Matrix4x4 Local;
    }

    private static AvObject ReadAvObject(NifFile nif, ref NifSpanReader r)
    {
        var o = new AvObject();
        o.Name = nif.String(r.I32());
        uint numExtra = r.U32();
        r.Skip((int)numExtra * 4);
        r.I32(); // controller
        o.Flags = r.U32();
        var t = new Vector3(r.F32(), r.F32(), r.F32());
        // 9 floats, read as rows; v' = R * v (nifly / Gamebryo NiMatrix3 convention).
        float m11 = r.F32(), m12 = r.F32(), m13 = r.F32();
        float m21 = r.F32(), m22 = r.F32(), m23 = r.F32();
        float m31 = r.F32(), m32 = r.F32(), m33 = r.F32();
        float s = r.F32();
        r.I32(); // collision object
        o.Local = Transforms.Compose(m11, m12, m13, m21, m22, m23, m31, m32, m33, s, t);
        return o;
    }

    private static void Walk(NifFile nif, int index, Matrix4x4 parent, List<LodMeshPart> parts, List<string> warnings, HashSet<int> visited, bool passthru, bool waterAsLit)
    {
        if (index < 0 || index >= nif.Blocks.Count || !visited.Add(index)) return;
        var block = nif.Blocks[index];

        if (NodeTypes.Contains(block.Type))
        {
            var r = nif.ReaderFor(block);
            var av = ReadAvObject(nif, ref r);
            if ((av.Flags & 1) != 0) return; // hidden
            var world = av.Local * parent;
            uint numChildren = r.U32();
            var children = new int[numChildren];
            for (int i = 0; i < numChildren; i++) children[i] = r.I32();
            foreach (var c in children) Walk(nif, c, world, parts, warnings, visited, passthru, waterAsLit);
        }
        else if (ShapeTypes.Contains(block.Type))
        {
            try
            {
                var part = ReadShape(nif, block, parent, warnings, passthru, waterAsLit);
                if (part is not null) parts.Add(part);
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
            {
                warnings.Add($"Shape {block.Index} ({block.Type}) could not be read: {ex.Message}");
            }
        }
        else if (block.Type is "NiTriShape" or "NiTriStrips")
        {
            warnings.Add($"{block.Type} shapes are Skyrim LE format and are skipped; convert the mesh to SSE.");
        }
    }

    // ---------- shapes ----------

    private static LodMeshPart? ReadShape(NifFile nif, NifBlock block, Matrix4x4 parent, List<string> warnings, bool passthru, bool waterAsLit)
    {
        var r = nif.ReaderFor(block);
        var av = ReadAvObject(nif, ref r);
        if ((av.Flags & 1) != 0) return null;

        r.Skip(16); // bounding sphere
        int skin = r.I32();
        int shaderRef = r.I32();
        int alphaRef = r.I32();
        ulong desc = r.U64();
        int numTris = r.U16();
        int numVerts = r.U16();
        uint dataSize = r.U32();

        if (skin >= 0) { warnings.Add($"Skinned shape '{av.Name}' skipped."); return null; }
        if (dataSize == 0 || numVerts == 0 || numTris == 0) return null;

        var material = ReadMaterial(nif, shaderRef, alphaRef, passthru, waterAsLit);
        if (material is null) { warnings.Add($"Shape '{av.Name}' has no lighting shader; skipped."); return null; }
        bool water = waterAsLit && ReferenceEquals(material, FakeWaterMaterial);
        if (water) warnings.Add(WaterPlaneWarning + av.Name);

        uint attrs = (uint)(desc >> 44);
        int vertexSize = (int)(desc & 0xF) * 4;
        bool hasPos = (attrs & 0x1) != 0, hasUV = (attrs & 0x2) != 0, hasNormal = (attrs & 0x8) != 0,
             hasTangent = (attrs & 0x18) == 0x18, hasColor = (attrs & 0x20) != 0, hasSkin = (attrs & 0x40) != 0,
             hasEye = (attrs & 0x100) != 0;
        if (!hasPos) return null;

        var world = av.Local * parent;
        var normalMatrix = Transforms.RotationOnly(world);

        var pos = new Vector3[numVerts];
        var uv = new Vector2[numVerts];
        var nrm = new Vector3[numVerts];
        var tan = new Vector3[numVerts];
        var bit = new Vector3[numVerts];
        uint[]? col = hasColor ? new uint[numVerts] : null;

        for (int i = 0; i < numVerts; i++)
        {
            int start = r.Position;
            var p = new Vector3(r.F32(), r.F32(), r.F32());
            float bx = 0;
            if ((attrs & 0x11) == 0x11) bx = r.F32();
            else if ((attrs & 0x11) == 0x1) r.Skip(4);
            if (hasUV) uv[i] = new Vector2((float)r.F16(), (float)r.F16());
            Vector3 n = Vector3.UnitZ, t = Vector3.UnitX;
            float by = 0, bz = 0;
            if (hasNormal) { n = Transforms.DecodeDir(r.U8(), r.U8(), r.U8()); by = Transforms.DecodeByte(r.U8()); }
            if (hasTangent) { t = Transforms.DecodeDir(r.U8(), r.U8(), r.U8()); bz = Transforms.DecodeByte(r.U8()); }
            if (hasColor) col![i] = r.U32();
            if (hasSkin) r.Skip(12);
            if (hasEye) r.Skip(4);
            if (r.Position - start != vertexSize) r.Position = start + vertexSize; // trust the descriptor

            pos[i] = Vector3.Transform(p, world);
            nrm[i] = Transforms.SafeNormalize(Vector3.TransformNormal(n, normalMatrix), Vector3.UnitZ);
            tan[i] = Transforms.SafeNormalize(Vector3.TransformNormal(t, normalMatrix), Vector3.UnitX);
            var b = hasTangent ? new Vector3(bx, by, bz) : Vector3.Cross(n, t);
            bit[i] = Transforms.SafeNormalize(Vector3.TransformNormal(b, normalMatrix), Vector3.UnitY);
        }

        var tris = new ushort[numTris * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = r.U16();
        foreach (var idx in tris)
            if (idx >= numVerts) throw new InvalidDataException("Triangle index out of range.");

        if (water)
        {
            // A flat water surface: face up, one body colour with the fake water's alpha, tiled texture.
            col = new uint[numVerts];
            for (int i = 0; i < numVerts; i++)
            {
                col[i] = FakeWaterColor;
                nrm[i] = Vector3.UnitZ; tan[i] = Vector3.UnitX; bit[i] = Vector3.UnitY;
                uv[i] *= FakeWaterUvTiling;
            }
        }

        return new LodMeshPart
        {
            Name = av.Name,
            Material = material,
            Positions = pos, UVs = uv, Normals = nrm, Tangents = tan, Bitangents = bit,
            Colors = col, Triangles = tris,
        };
    }

    private static LodMaterial? ReadMaterial(NifFile nif, int shaderRef, int alphaRef, bool passthru, bool waterAsLit)
    {
        if (shaderRef < 0 || shaderRef >= nif.Blocks.Count) return null;
        var sb = nif.Blocks[shaderRef];
        if (waterAsLit && sb.Type == "BSWaterShaderProperty") return FakeWaterMaterial;
        if (sb.Type != "BSLightingShaderProperty") return null;

        var r = nif.ReaderFor(sb);
        uint shaderType = r.U32();
        r.I32();            // name
        uint ne = r.U32(); r.Skip((int)ne * 4);
        r.I32();            // controller
        uint f1 = r.U32();
        uint f2 = r.U32();
        float uvOx = r.F32(), uvOy = r.F32(), uvSx = r.F32(), uvSy = r.F32();
        int texSetRef = r.I32();
        var emissive = new Vector3(r.F32(), r.F32(), r.F32());
        float emissiveMult = r.F32();
        uint clamp = r.U32();

        LodShaderPassthru? keep = null;
        if (passthru)
        {
            float alpha = r.F32(), refraction = r.F32(), gloss = r.F32();
            var spec = new Vector3(r.F32(), r.F32(), r.F32());
            float specStrength = r.F32(), le1 = r.F32(), le2 = r.F32();
            keep = new LodShaderPassthru(shaderType, uvOx, uvOy, uvSx, uvSy, alpha, refraction, gloss, spec, specStrength, le1, le2);
        }

        var textures = new List<string>();
        if (texSetRef >= 0 && texSetRef < nif.Blocks.Count && nif.Blocks[texSetRef].Type == "BSShaderTextureSet")
        {
            var tr = nif.ReaderFor(nif.Blocks[texSetRef]);
            uint n = tr.U32();
            for (int i = 0; i < n; i++) textures.Add(tr.SizedString());
        }
        if (textures.Count == 0 || string.IsNullOrWhiteSpace(textures[0])) return null;
        while (textures.Count < 9) textures.Add("");

        bool hasAlpha = false; ushort alphaFlags = 0; byte threshold = 0;
        if (alphaRef >= 0 && alphaRef < nif.Blocks.Count && nif.Blocks[alphaRef].Type == "NiAlphaProperty")
        {
            var ar = nif.ReaderFor(nif.Blocks[alphaRef]);
            ar.I32(); uint aen = ar.U32(); ar.Skip((int)aen * 4); ar.I32();
            alphaFlags = ar.U16();
            threshold = ar.U8();
            hasAlpha = true;
        }

        if (keep is not null)
            return new LodMaterial(textures, f1, f2, clamp, hasAlpha, alphaFlags, threshold, emissive, emissiveMult, keep);

        bool glow = (f2 & 0x40) != 0;
        return new LodMaterial(
            textures,
            0x8000_0300u | (f1 & KeepF1),
            0x0000_0005u | (f2 & KeepF2),
            clamp,
            hasAlpha, alphaFlags, threshold,
            glow ? emissive : Vector3.Zero,
            glow ? emissiveMult : 1f);
    }
}
