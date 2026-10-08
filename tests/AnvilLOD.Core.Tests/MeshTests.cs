using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Core.Tests;

public class MeshTests
{
    [Fact]
    public void Positive_z_rotation_turns_clockwise_seen_from_above()
    {
        var m = Transforms.Reference(Vector3.Zero, new Vector3(0, 0, MathF.PI / 2), 1f);
        var v = Vector3.Transform(Vector3.UnitX, m);
        Assert.True(Vector3.Distance(v, -Vector3.UnitY) < 1e-5f);
    }

    [Fact]
    public void Reference_applies_scale_then_translation()
    {
        var m = Transforms.Reference(new Vector3(100, 200, 300), Vector3.Zero, 2f);
        var v = Vector3.Transform(new Vector3(1, 2, 3), m);
        Assert.True(Vector3.Distance(v, new Vector3(102, 204, 306)) < 1e-4f);
    }

    [Fact]
    public void Bto_round_trips_through_reader_in_world_space()
    {
        var part = Triangle(new LodMaterial(["textures\\a.dds", "textures\\a_n.dds", "", "", "", "", "", "", ""],
            0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f), colors: null);
        var mesh = new LodMesh { Path = "m", Parts = [part] };
        var refPos = new Vector3(4 * 4096 + 1000, -40 * 4096 + 2000, 500);
        var rot = new Vector3(0.3f, -0.2f, 1.1f);
        var r = new LodReference("1:A.esp", "2:A.esp", null, "Tamriel", "A.esp", refPos, rot, 1.5f,
            new LodMeshSet("m", null, null, null), LodReferenceFlags.None);

        var res = BtoBuilder.Build(new QuadKey("Tamriel", LodLevel.Lod4, 4, -40), [r], _ => mesh);
        Assert.Equal(1, res.Shapes);
        Assert.Equal(1, res.Triangles);

        var back = NifGeometryReader.Read("out", res.Bytes);
        Assert.Single(back.Parts);
        var m = Transforms.Reference(refPos, rot, 1.5f);
        for (int i = 0; i < 3; i++)
        {
            var expected = Vector3.Transform(part.Positions[i], m);
            Assert.True(Vector3.Distance(expected, back.Parts[0].Positions[i]) < 0.01f, $"vertex {i}");
        }
        Assert.Equal("textures\\a.dds", back.Parts[0].Material.Textures[0]);
    }

    [Fact]
    public void Same_material_merges_and_colors_are_kept()
    {
        var mat = new LodMaterial(["textures\\a.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f);
        var a = new LodMesh { Path = "a", Parts = [Triangle(mat, colors: null)] };
        var b = new LodMesh { Path = "b", Parts = [Triangle(mat, colors: [0xFF0000FF, 0xFF00FF00, 0xFFFF0000])] };
        LodReference R(string mesh, float x) => new($"{mesh}:A.esp", "2:A.esp", null, "T", "A.esp",
            new Vector3(x, 10, 0), Vector3.Zero, 1f, new LodMeshSet(mesh, null, null, null), LodReferenceFlags.None);

        var res = BtoBuilder.Build(new QuadKey("T", LodLevel.Lod4, 0, 0), [R("a", 100), R("b", 300)], p => p == "a" ? a : b);
        Assert.Equal(1, res.Shapes);
        Assert.Equal(2, res.Triangles);
        var back = NifGeometryReader.Read("out", res.Bytes);
        Assert.NotNull(back.Parts[0].Colors);
        Assert.Equal(0xFFFFFFFFu, back.Parts[0].Colors![0]);   // white for the mesh without colors
        Assert.Equal(0xFF0000FFu, back.Parts[0].Colors![3]);
    }

    [Fact]
    public void Empty_block_is_a_valid_nif()
    {
        var res = BtoBuilder.Build(new QuadKey("T", LodLevel.Lod32, 0, 0), [], _ => null);
        Assert.Equal(0, res.Shapes);
        var nif = NifFile.Read(res.Bytes);
        Assert.Single(nif.Blocks);
        Assert.Equal("NiNode", nif.Blocks[0].Type);
    }

    private static LodMeshPart Triangle(LodMaterial mat, uint[]? colors) => new()
    {
        Material = mat,
        Positions = [new(0, 0, 0), new(100, 0, 0), new(0, 100, 50)],
        UVs = [new(0, 0), new(1, 0), new(0, 1)],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
        Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
        Colors = colors,
        Triangles = [0, 1, 2],
    };
}
