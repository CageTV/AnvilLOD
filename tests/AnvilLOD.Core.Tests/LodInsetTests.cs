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
    public void Bto_inset_applies_at_lod4_and_lod8_but_not_lod16_and_lod8_sits_deeper()
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
        Assert.Equal(988f, ZAt(LodLevel.Lod8), 1);   // LOD8 is pulled twice as far, so LOD8, LOD4 and the full model never share a surface
        Assert.Equal(1000f, ZAt(LodLevel.Lod16), 1);
    }

    [Fact]
    public void Other_objects_get_the_object_inset_only_when_it_is_on()
    {
        Assert.Equal(3f, LodInset.For("lod\\whiterun\\whiterunwall_lod_0.nif", 0f, 0f, 0f, objects: 3f));
        Assert.Equal(0f, LodInset.For("lod\\whiterun\\whiterunwall_lod_0.nif", 0f, 0f, 0f, objects: 0f));
        Assert.Equal(6f, LodInset.For("lod\\ice\\glacierslab01_lod_0.nif", 6f, 0f, 0f, objects: 3f));   // a category's own inset still wins
    }

    [Fact]
    public void Only_generic_objects_skip_flat_pieces()
    {
        Assert.True(LodInset.IsGenericObject(@"lod\whiterun\whiterunwall_lod_0.nif"));
        Assert.False(LodInset.IsGenericObject(@"lod\ice\glacierslab01_lod_0.nif"));
        Assert.False(LodInset.IsGenericObject(@"dlc01\lod\mountains\dlc1mountainridge02cave_lod_1.nif"));
    }

    [Fact]
    public void A_flat_piece_is_recognised_and_a_tall_wall_is_not()
    {
        var road = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 100, 2), new Vector3(100, 100, 2) };
        var wall = new[] { new Vector3(0, 0, 0), new Vector3(0, 100, 0), new Vector3(0, 0, 60), new Vector3(0, 100, 60) };
        Assert.True(LodInset.IsFlat(road, 4f));
        Assert.False(LodInset.IsFlat(wall, 4f));
    }

    [Fact]
    public void Flatness_counts_the_reference_scale()
    {
        var slab = new[] { new Vector3(0, 0, 0), new Vector3(100, 0, 0), new Vector3(0, 100, 10) };
        Assert.True(LodInset.IsFlat(slab, 8f));               // 10 < 32
        Assert.False(LodInset.IsFlat(slab, 8f, scale: 4f));   // 40 >= 32: scaled up it is no longer a flat piece
    }

    private static LodMeshPart Part(Vector3[] positions, Vector3 normal, ushort[]? triangles = null) => new()
    {
        Material = new LodMaterial(["textures\\a.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f),
        Positions = positions,
        UVs = positions.Select(_ => Vector2.Zero).ToArray(),
        Normals = positions.Select(_ => normal).ToArray(),
        Tangents = positions.Select(_ => Vector3.UnitX).ToArray(),
        Bitangents = positions.Select(_ => Vector3.UnitY).ToArray(),
        Triangles = triangles ?? [0, 1, 2],
    };

    // A four-sided pyramid: every face looks up, but in four different directions (a rock pile's shape).
    private static readonly Vector3[] Pyramid = [new(-100, -100, 0), new(100, -100, 0), new(100, 100, 0), new(-100, 100, 0), new(0, 0, 90)];
    private static readonly ushort[] PyramidFaces = [0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4];

    [Fact]
    public void A_ramp_lying_on_the_ground_is_a_ground_sheet_and_a_lump_or_a_roof_is_not()
    {
        // A 900-unit road ramp climbing 150 units, as modelled: too tall for IsFlat, but it faces up evenly from ground level.
        Vector3[] ramp = [new(-450, -100, -75), new(450, -100, 75), new(450, 100, 75), new(-450, 100, -75)];
        ushort[] quad = [0, 1, 2, 0, 2, 3];
        Assert.False(LodInset.IsFlat(ramp, 8f));
        Assert.True(LodInset.IsGroundSheet(ramp, quad, 8f));

        Assert.False(LodInset.IsGroundSheet(Pyramid, PyramidFaces, 8f));                 // faces up, but every way: a lump, not a sheet
        Vector3[] roof = ramp.Select(p => p + new Vector3(0, 0, 400)).ToArray();
        Assert.False(LodInset.IsGroundSheet(roof, quad, 8f));                            // a sheet, but high above the ground
    }

    [Fact]
    public void A_ramp_is_not_pulled_and_a_rock_pile_still_is()
    {
        var slope = new Vector3(0, 5f * MathF.PI / 180f, 0);
        var ramp = Part([new(-450, -100, -75), new(450, -100, 75), new(450, 100, 75), new(-450, 100, -75)], Vector3.UnitZ, [0, 1, 2, 0, 2, 3]);
        Assert.Equal(BuildPositions(ramp, slope, 0f), BuildPositions(ramp, slope, 8f));

        var pile = Part(Pyramid, Vector3.UnitZ, PyramidFaces);
        Assert.NotEqual(BuildPositions(pile, slope, 0f), BuildPositions(pile, slope, 8f));
    }

    private static Vector3[] BuildPositions(LodMeshPart part, Vector3 rotation, float objectInset)
    {
        const string path = "lod\\roads\\road_lod_0.nif";
        var mesh = new LodMesh { Path = "m", Parts = [part] };
        var r = new LodReference("1:A.esp", "2:A.esp", null, "T", "A.esp", new Vector3(2000, 2000, 1000), rotation, 1f,
            new LodMeshSet(path, path, path, path), LodReferenceFlags.None);
        var res = BtoBuilder.Build(new QuadKey("T", LodLevel.Lod4, 0, 0), [r], _ => mesh,
            inset: p => LodInset.For(p, 0f, 0f, 0f, objects: objectInset));
        return NifGeometryReader.Read("o", res.Bytes).Parts[0].Positions;
    }

    [Fact]
    public void A_road_on_a_slope_is_still_flat_and_is_not_pulled_under_the_terrain()
    {
        // A 900-unit road piece, 4 units high as modelled, laid on a 10 degree slope: its world height range is about 160 units.
        var road = Part([new(-450, -100, 0), new(450, -100, 4), new(0, 100, 2)], Vector3.UnitZ);
        var slope = new Vector3(0, 10f * MathF.PI / 180f, 0);
        Assert.Equal(BuildPositions(road, slope, 0f), BuildPositions(road, slope, 8f));
    }

    [Fact]
    public void A_wall_on_a_slope_is_still_pulled_in()
    {
        var wall = Part([new(0, -100, 0), new(0, 100, 0), new(0, 0, 200)], Vector3.UnitX);
        var slope = new Vector3(0, 10f * MathF.PI / 180f, 0);
        var plain = BuildPositions(wall, slope, 0f);
        var pulled = BuildPositions(wall, slope, 8f);
        Assert.NotEqual(plain, pulled);
        Assert.Equal(8f, Vector3.Distance(plain[0], pulled[0]), 1);
    }

    [Fact]
    public void A_tall_wall_is_pulled_in()
    {
        var wall = new[] { new Vector3(0, 0, 0), new Vector3(0, 100, 0), new Vector3(0, 0, 60), new Vector3(0, 100, 60) };
        var nrm = new[] { Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX };
        LodInset.Apply(wall, nrm, 3f);
        Assert.All(wall, p => Assert.Equal(-3f, p.X, 3));
    }
}
