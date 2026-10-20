using AnvilLOD.Core.Occlusion;

namespace AnvilLOD.Core.Tests;

public sealed class OcclusionCalculatorTests
{
    private const int Origin = -24;   // south-west cell of the pretend .lod file: blocks of 4 cells start there

    /// <summary>41 x 41 cells (-20..20) of flat ground at <paramref name="ground"/>, plus an optional north-south ridge one cell wide at x = 0.</summary>
    private static OcclusionGrid World(float ground = 0f, float? ridge = null)
    {
        var g = new OcclusionGrid(-20, -20, 41, 41);
        for (int cx = -20; cx <= 20; cx++)
            for (int cy = -20; cy <= 20; cy++)
            {
                var v = new float[33 * 33];
                Array.Fill(v, cx == 0 && ridge is { } r ? r : ground);
                g.SetTerrain(cx, cy, v);
            }
        return g;
    }

    private static (int X, int Y) Block(int cellX, int cellY) => ((cellX - Origin) / 4, (cellY - Origin) / 4);

    [Fact]
    public void Open_ground_is_visible_everywhere_within_the_radius_and_hidden_beyond_it()
    {
        var calc = new OcclusionCalculator(World(), Origin, Origin, new OcclusionSettings(RadiusCells: 12));
        var t = calc.Compute(-10, 0);
        var near = Block(-6, 0); var far = Block(18, 0);
        Assert.True(t.GetLeaf(near.X, near.Y));
        Assert.False(t.GetLeaf(far.X, far.Y));                     // 28 cells away, radius 12
        var own = Block(-10, 0);
        Assert.True(t.GetLeaf(own.X, own.Y));
    }

    [Fact]
    public void A_tall_ridge_hides_what_is_behind_it_but_not_what_is_in_front()
    {
        var calc = new OcclusionCalculator(World(0f, 30000f), Origin, Origin, new OcclusionSettings(Clearance: 1536f));
        var t = calc.Compute(-12, 0);
        var front = Block(-8, 0); var behind = Block(10, 0);
        Assert.True(t.GetLeaf(front.X, front.Y));
        Assert.False(t.GetLeaf(behind.X, behind.Y));
        var ridge = Block(0, 0);
        Assert.True(t.GetLeaf(ridge.X, ridge.Y));                   // the ridge itself stands in the open
    }

    [Fact]
    public void A_low_ridge_hides_nothing_when_it_is_within_the_clearance_and_something_without_it()
    {
        var world = World(0f, 1000f);
        var safe = new OcclusionCalculator(world, Origin, Origin, new OcclusionSettings(Clearance: 1536f)).Compute(-12, 0);
        var strict = new OcclusionCalculator(world, Origin, Origin, new OcclusionSettings(Clearance: 0f)).Compute(-12, 0);
        var behind = Block(10, 0);
        Assert.True(safe.GetLeaf(behind.X, behind.Y));
        Assert.False(strict.GetLeaf(behind.X, behind.Y));
        Assert.True(strict.VisibleLeaves() < safe.VisibleLeaves());
    }

    [Fact]
    public void A_quad_is_visible_whenever_any_of_its_blocks_is()
    {
        var t = new OcclusionCalculator(World(0f, 30000f), Origin, Origin).Compute(-12, 3);
        for (int level = 1; level <= TvdtData.TopLevel; level++)
        {
            int n = TvdtData.LeavesPerSide >> level;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool any = false;
                    foreach (var (dx, dy) in new[] { (0, 0), (0, 1), (1, 0), (1, 1) }) any |= t.GetNode(level - 1, 2 * x + dx, 2 * y + dy);
                    if (any) Assert.True(t.GetNode(level, x, y));      // a quad is never hidden while one of its blocks is seen
                }
        }
        Assert.Equal(3, t.Lead);
    }

    [Fact]
    public void Only_cells_with_data_are_computed()
    {
        var calc = new OcclusionCalculator(World(), Origin, Origin);
        var all = calc.ComputeMany([(0, 0), (5, 5), (100, 100), (-30, 0)]);
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey((0, 0)));
    }

    [Fact]
    public void Max_height_data_is_a_base_and_steps_of_128_rounded_up()
    {
        var g = World(5000f);
        g.RaiseTop(0, 0, 100, 100, 6000f);                          // an object on the first sample of cell (0, 0)
        var (offset, steps) = OcclusionCalculator.BuildMaxHeight(g, 0, 0);
        Assert.Equal(4992f, offset);                                // 39 * 128, the multiple of 128 at or below 5000
        Assert.Equal(1, steps[31 * 32 + 31]);                       // ground at 5000: (5000 - 4992) / 128 rounds up to 1
        Assert.Equal(8, steps[0]);                                  // (6000 - 4992) / 128 = 7.9 -> 8, at x = 0, y = 0
        Assert.Equal(1024, steps.Length);
    }

    [Fact]
    public void The_height_grid_checks_its_input()
    {
        var g = new OcclusionGrid(0, 0, 2, 2);
        Assert.Throws<ArgumentException>(() => g.SetTerrain(0, 0, new float[10]));
        Assert.False(g.HasData(0, 0));
        g.SetTerrain(0, 0, new float[33 * 33]);
        Assert.True(g.HasData(0, 0));
        Assert.False(g.HasData(1, 1));
    }
}
