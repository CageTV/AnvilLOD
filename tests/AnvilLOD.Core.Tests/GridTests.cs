using System.Numerics;
using AnvilLOD.Core.Incremental;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class GridTests
{
    private static readonly LodSettings Tamrielish = new(-64, -64, 128, 4, 32);

    [Theory]
    [InlineData(0f, 0f, 0, 0)]
    [InlineData(4095f, 4095f, 0, 0)]
    [InlineData(4096f, 0f, 1, 0)]
    [InlineData(-1f, -1f, -1, -1)]
    [InlineData(-4096f, -4097f, -1, -2)]
    public void Cell_from_world_uses_floor(float x, float y, int cx, int cy)
        => Assert.Equal(new CellCoord(cx, cy), CellCoord.FromWorld(x, y));

    [Theory]
    [InlineData(7, -4, -2)]
    [InlineData(-7, 4, -2)]
    [InlineData(-8, 4, -2)]
    [InlineData(-9, 4, -3)]
    [InlineData(0, 4, 0)]
    public void FloorDiv_rounds_toward_negative_infinity(int a, int b, int expected)
        => Assert.Equal(expected, LodGrid.FloorDiv(a, b));

    [Theory]
    [InlineData(0, 0, LodLevel.Lod4, 0, 0)]
    [InlineData(3, 3, LodLevel.Lod4, 0, 0)]
    [InlineData(4, 3, LodLevel.Lod4, 4, 0)]
    [InlineData(-1, -1, LodLevel.Lod4, -4, -4)]
    [InlineData(-1, -1, LodLevel.Lod32, -32, -32)]
    [InlineData(31, -33, LodLevel.Lod32, 0, -64)]
    public void Block_origin_aligned_from_sw_corner(int cx, int cy, LodLevel lvl, int ox, int oy)
    {
        var grid = new LodGrid("Tamriel", Tamrielish);
        Assert.Equal(new CellCoord(ox, oy), grid.BlockOrigin(new CellCoord(cx, cy), lvl));
    }

    [Fact]
    public void Block_origin_respects_odd_sw_origin()
    {
        // SW origin not a multiple of the level: blocks align to the origin, not to zero.
        var grid = new LodGrid("Test", new LodSettings(-30, -30, 64, 4, 32));
        Assert.Equal(new CellCoord(-30, -30), grid.BlockOrigin(new CellCoord(-27, -27), LodLevel.Lod4));
        Assert.Equal(new CellCoord(-26, -30), grid.BlockOrigin(new CellCoord(-26, -27), LodLevel.Lod4));
        Assert.Equal(new CellCoord(2, 2), grid.BlockOrigin(new CellCoord(5, 5), LodLevel.Lod32));
    }

    [Fact]
    public void Quad_file_naming_matches_engine_convention()
    {
        var q = new QuadKey("Tamriel", LodLevel.Lod8, -32, 16);
        Assert.Equal("Tamriel.8.-32.16.bto", q.FileName);
        Assert.Equal(@"meshes\terrain\Tamriel\objects\Tamriel.8.-32.16.bto", q.RelativePath);
    }

    [Fact]
    public void LodSettings_roundtrip()
    {
        var bytes = Tamrielish.ToBytes();
        Assert.Equal(16, bytes.Length);
        Assert.Equal(Tamrielish, LodSettings.Parse(bytes));
    }

    [Fact]
    public void Inside_grid_bounds()
    {
        var g = new LodGrid("T", Tamrielish);
        Assert.True(g.IsInsideGrid(new CellCoord(-64, -64)));
        Assert.True(g.IsInsideGrid(new CellCoord(63, 63)));
        Assert.False(g.IsInsideGrid(new CellCoord(64, 0)));
        Assert.False(g.IsInsideGrid(new CellCoord(-65, 0)));
    }

    [Fact]
    public void Bucketer_only_adds_levels_with_meshes()
    {
        var b = new QuadBucketer(new Dictionary<string, LodGrid> { ["T"] = new("T", Tamrielish) });
        b.Add(Ref("1", new LodMeshSet(@"meshes\lod\a_lod_0.nif", @"meshes\lod\a_lod_1.nif", null, null)));
        var quads = b.Build();
        Assert.Equal(2, quads.Count);
        Assert.Contains(quads.Keys, q => q.Level == LodLevel.Lod4);
        Assert.Contains(quads.Keys, q => q.Level == LodLevel.Lod8);
    }

    [Fact]
    public void Hash_is_stable_and_detects_changes()
    {
        var q = new QuadKey("T", LodLevel.Lod4, 0, 0);
        var meshes = new LodMeshSet(@"meshes\lod\a_lod_0.nif", null, null, null);
        var a = new[] { Ref("1", meshes), Ref("2", meshes) };
        string Fp(string _) => "L:100:1";

        var h1 = QuadHasher.Hash(q, a, Fp, "s");
        Assert.Equal(h1, QuadHasher.Hash(q, a, Fp, "s"));
        Assert.NotEqual(h1, QuadHasher.Hash(q, a, Fp, "other-settings"));
        Assert.NotEqual(h1, QuadHasher.Hash(q, a, _ => "L:101:1", "s"));       // mesh file changed
        Assert.NotEqual(h1, QuadHasher.Hash(q, [a[0] with { Scale = 2f }, a[1]], Fp, "s"));
    }

    [Fact]
    public void Manifest_plan_splits_rebuild_unchanged_stale()
    {
        var keep = new QuadKey("T", LodLevel.Lod4, 0, 0);
        var change = new QuadKey("T", LodLevel.Lod4, 4, 0);
        var added = new QuadKey("T", LodLevel.Lod4, 8, 0);
        var m = new BuildManifest();
        m.Quads[keep.RelativePath] = "A";
        m.Quads[change.RelativePath] = "B";
        m.Quads[@"meshes\terrain\T\objects\T.4.12.0.bto"] = "C";

        var plan = m.Plan(new Dictionary<QuadKey, string> { [keep] = "A", [change] = "B2", [added] = "D" });
        Assert.Single(plan.Unchanged);
        Assert.Equal(keep, plan.Unchanged[0]);
        Assert.Equal(2, plan.Rebuild.Count);
        Assert.Single(plan.StaleFiles);
    }

    private static LodReference Ref(string id, LodMeshSet meshes) => new(
        id, "base", null, "T", "Test.esp", new Vector3(100, 100, 0), Vector3.Zero, 1f, meshes, LodReferenceFlags.None);
}
