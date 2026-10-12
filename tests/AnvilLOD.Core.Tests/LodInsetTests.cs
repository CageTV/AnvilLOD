using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Core.Tests;

public class LodInsetTests
{
    [Theory]
    [InlineData("lod\\ice\\glacierslab01_lod_0.nif", 6f)]
    [InlineData("dlc02\\lod\\glaciers\\dlc2iceberglarge_lod_1.nif", 6f)]
    [InlineData("dlc02\\lod\\icecastle\\dlc2castleglacier01_lod_0.nif", 6f)]
    [InlineData("Lod/Ice/IceFloes01_lod_0.nif", 6f)]
    [InlineData("lod\\mountains\\elfxmountaincliff02_lod_0h.nif", 0f)]
    [InlineData("lod\\whiterun\\whiterunwall_lod_0.nif", 0f)]
    public void Ice_paths_get_the_ice_inset_and_nothing_else_does(string path, float expected)
        => Assert.Equal(expected, LodInset.For(path, ice: 6f, mountains: 0f));

    [Fact]
    public void Mountain_paths_get_the_mountain_inset_only_when_it_is_on()
    {
        Assert.Equal(8f, LodInset.For("dlc01\\lod\\mountains\\dlc1mountainridge02cave_lod_1.nif", 6f, 8f));
        Assert.Equal(0f, LodInset.For("dlc01\\lod\\mountains\\dlc1mountainridge02cave_lod_1.nif", 6f, 0f));
    }

    [Fact]
    public void Welded_vertices_move_together_so_no_crack_opens()
    {
        // Two faces of a wedge share an edge but carry split normals (a hard edge).
        var s = MathF.Sqrt(0.5f);
        Vector3[] pos = [new(0, 0, 0), new(0, 100, 0), new(0, 0, 0), new(0, 100, 0)];
        Vector3[] nrm = [new(-s, 0, s), new(-s, 0, s), new(s, 0, s), new(s, 0, s)];
        LodInset.Apply(pos, nrm, 10f);
        Assert.Equal(pos[0], pos[2]);
        Assert.Equal(pos[1], pos[3]);
        Assert.True(pos[0].Z < 0f, "an upward-ish surface moves down, inside the solid");
    }

    [Fact]
    public void Surfaces_move_against_their_normal_by_the_distance()
    {
        Vector3[] pos = [new(0, 0, 100), new(10, 0, 100), new(0, 10, 100)];
        Vector3[] nrm = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        LodInset.Apply(pos, nrm, 6f);
        Assert.All(pos, p => Assert.Equal(94f, p.Z, 3));
    }

    [Fact]
    public void Downward_faces_and_zero_distance_stay_put()
    {
        Vector3[] pos = [new(0, 0, 0), new(10, 0, 0), new(0, 10, 0)];
        Vector3[] nrm = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ];
        var before = pos.ToArray();
        LodInset.Apply(pos, nrm, 6f);
        Assert.Equal(before, pos);

        Vector3[] up = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        LodInset.Apply(pos, up, 0f);
        Assert.Equal(before, pos);
    }

    [Fact]
    public void Bto_inset_applies_at_lod4_and_lod8_but_not_lod16()
    {
        var part = new LodMeshPart
        {
            Material = new LodMaterial(["textures\\a.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f),
            Positions = [new(0, 0, 0), new(100, 0, 0), new(0, 100, 0)],
            UVs = [new(0, 0), new(1, 0), new(0, 1)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Triangles = [0, 1, 2],
        };
        var mesh = new LodMesh { Path = "m", Parts = [part] };
        var r = new LodReference("1:A.esp", "2:A.esp", null, "T", "A.esp", new Vector3(500, 500, 1000), Vector3.Zero, 1f,
            new LodMeshSet("lod\\ice\\x_lod_0.nif", "lod\\ice\\x_lod_0.nif", "lod\\ice\\x_lod_0.nif", "lod\\ice\\x_lod_0.nif"), LodReferenceFlags.None);
        float Inset(string path) => LodInset.For(path, 6f, 0f);

        float ZAt(LodLevel level)
        {
            var res = BtoBuilder.Build(new QuadKey("T", level, 0, 0), [r], _ => mesh, inset: Inset);
            return NifGeometryReader.Read("o", res.Bytes).Parts[0].Positions[0].Z;
        }

        Assert.Equal(994f, ZAt(LodLevel.Lod4), 1);
        Assert.Equal(994f, ZAt(LodLevel.Lod8), 1);
        Assert.Equal(1000f, ZAt(LodLevel.Lod16), 1);
    }
}
