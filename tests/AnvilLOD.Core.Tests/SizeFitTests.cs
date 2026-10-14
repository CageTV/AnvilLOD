using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class SizeFitTests
{
    private const long Gb = 1073741824L;
    private static readonly int[] Choices = [4, 8, 15, 25, 40, 60, 100];

    /// <summary>A last run at 100% grass with 3D trees at LOD4 and LOD8: about 14.7 GB.</summary>
    private static SizeCalibration Run(long seasons = 0)
    {
        var bytes = new Dictionary<string, long>
        {
            ["4|objects"] = 700_000_000, ["4|grass"] = 7_400_000_000, ["4|tree3d"] = 570_000_000,
            ["8|objects"] = 320_000_000, ["8|grass"] = 3_800_000_000, ["8|tree3d"] = 570_000_000,
            ["16|objects"] = 150_000_000, ["32|objects"] = 630_000_000,
        };
        return new SizeCalibration(100, true, true, true, bytes, bytes.Values.Sum(), seasons);
    }

    [Fact]
    public void A_target_bigger_than_everything_changes_nothing()
    {
        var fit = SizeEstimator.FitToTarget(Run(), 40 * Gb, Choices, 100, true, true, true, false);
        Assert.True(fit.Reached);
        Assert.Equal((100, true, true, true), (fit.Percent, fit.GrassOn, fit.Tree3D, fit.Lod8));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void The_fit_is_under_the_target_and_smaller_than_the_full_run(int gb)
    {
        var fit = SizeEstimator.FitToTarget(Run(), gb * Gb, Choices, 100, true, true, true, false);
        Assert.True(fit.Reached);
        Assert.True(fit.Total <= gb * Gb);
        var full = SizeEstimator.FitToTarget(Run(), 99 * Gb, Choices, 100, true, true, true, false);
        Assert.True(fit.Total < full.Total);
    }

    [Fact]
    public void A_bigger_target_never_gives_a_poorer_result()
    {
        int last = -1;
        foreach (var gb in new[] { 2, 3, 4, 6, 8, 12, 16, 24 })
        {
            var fit = SizeEstimator.FitToTarget(Run(), gb * Gb, Choices, 100, true, true, true, false);
            int richness = (fit.GrassOn ? Array.IndexOf(Choices, fit.Percent) + 1 : 0) * 100 + (fit.Tree3D ? 10 : 0) + (fit.Lod8 ? 1 : 0);
            Assert.True(richness >= last, $"{gb} GB gave a poorer result than a smaller target");
            last = richness;
        }
    }

    [Fact]
    public void A_target_nothing_fits_returns_the_leanest_choice_and_says_so()
    {
        var fit = SizeEstimator.FitToTarget(Run(), Gb / 10, Choices, 100, true, true, true, false);
        Assert.False(fit.Reached);
        Assert.False(fit.GrassOn);
        Assert.False(fit.Tree3D);
    }

    [Fact]
    public void What_the_user_has_off_is_never_turned_on_and_grass_is_never_raised()
    {
        var off = SizeEstimator.FitToTarget(Run(), 40 * Gb, Choices, 25, false, false, false, false);
        Assert.False(off.GrassOn);
        Assert.False(off.Tree3D);
        var low = SizeEstimator.FitToTarget(Run(), 40 * Gb, Choices, 15, true, true, false, false);
        Assert.Equal(15, low.Percent);
        Assert.False(low.Lod8);
    }

    [Fact]
    public void Seasons_count_in_the_total_and_are_left_on()
    {
        var withSeasons = SizeEstimator.FitToTarget(Run(seasons: 6 * Gb), 10 * Gb, Choices, 100, true, true, true, true);
        var without = SizeEstimator.FitToTarget(Run(seasons: 6 * Gb), 10 * Gb, Choices, 100, true, true, true, false);
        Assert.True(withSeasons.Estimate.Seasons > 0);
        Assert.True(withSeasons.Total <= 10 * Gb);
        // the same target leaves less room for the base when the seasonal copies take their share
        Assert.True(withSeasons.Estimate.Base <= without.Estimate.Base);
    }
}
