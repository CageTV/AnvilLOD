using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnvilLOD.Cli;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class GrassInBlocksTests
{
    // grassLod, Grass Optimizations' LOD meshes will be made, "keep grass in the blocks too", expected
    [Theory]
    [InlineData(false, false, false, false)]   // grass LOD off: never
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]     // the feature is not installed (or its meshes are switched off): the grass stays
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]     // the feature is installed: its meshes replace the grass in the blocks
    [InlineData(true, true, true, true)]       // ... unless the user asks for both
    public void Grass_goes_into_the_blocks_only_when(bool grassLod, bool csMeshes, bool keepBoth, bool expected) =>
        Assert.Equal(expected, ScanPipeline.GrassInBlocks(grassLod, csMeshes, keepBoth));
}

public class CliOptionsTests
{
    /// <summary>Version 0.2.72 documented 11 options that the parser rejected as unknown. Every option the help text lists must parse.</summary>
    [Fact]
    public void Every_option_in_the_help_text_is_accepted()
    {
        var usage = (string)typeof(Program).GetField("Usage", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var names = Regex.Matches(usage, @"(?m)^\s+--([a-z0-9-]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(names.Count > 60, $"only {names.Count} options found in the help text: did its layout change?");
        var rejected = names.Where(n => !Accepts(["--" + n]) && !Accepts(["--" + n, "1"])).ToList();
        Assert.Empty(rejected);
    }

    [Fact]
    public void Grass_with_cs_is_a_flag()
    {
        Assert.True(CliArgs.Parse(["--grass-with-cs"]).Has("grass-with-cs"));
        Assert.False(CliArgs.Parse([]).Has("grass-with-cs"));
    }

    private static bool Accepts(string[] args)
    {
        try { CliArgs.Parse(args); return true; }
        catch (CliArgException) { return false; }
    }
}

public class SizeCalibrationTests
{
    private static SizeCalibration RunWithoutGrass()
    {
        var bytes = new Dictionary<string, long>
        {
            ["4|objects"] = 1_300_000_000, ["4|tree3d"] = 1_600_000_000,
            ["8|objects"] = 630_000_000, ["16|objects"] = 290_000_000, ["32|objects"] = 1_000_000_000,
        };
        return new SizeCalibration(100, false, true, false, bytes, bytes.Values.Sum(), 0, SizeEstimator.CalibrationVersion);
    }

    /// <summary>After a run with no grass in the blocks, asking for grass must not be answered with a silent "no extra size".</summary>
    [Fact]
    public void Asking_for_grass_after_a_run_without_it_is_flagged_not_silently_zero()
    {
        var est = SizeEstimator.Estimate(RunWithoutGrass(), 100, true, true, false, false)!;
        Assert.Equal(4_820_000_000L, est.Base);
        Assert.Contains("grass", est.Note);
    }

    [Fact]
    public void Without_grass_asked_for_there_is_no_note()
    {
        var est = SizeEstimator.Estimate(RunWithoutGrass(), 100, false, true, false, false)!;
        Assert.Equal("", est.Note);
    }

    [Fact]
    public void Only_a_calibration_made_by_this_version_is_used()
    {
        Assert.False(SizeEstimator.IsCurrent(null));
        Assert.True(SizeEstimator.IsCurrent(RunWithoutGrass()));
        Assert.False(SizeEstimator.IsCurrent(RunWithoutGrass() with { Version = null }));
    }

    /// <summary>Settings saved by 0.2.72 and before have no Version: their "grass on" meant ticked, not written, so they must not be trusted.</summary>
    [Fact]
    public void A_calibration_saved_by_an_older_version_is_not_current()
    {
        const string old = """{"GrassPercent":100,"GrassOn":true,"Tree3D":true,"Tree3DLod8":false,"BaseBytes":{"4|grass":13488939899,"4|objects":1287907902},"BaseTotal":14776847801,"SeasonTotal":0}""";
        var c = JsonSerializer.Deserialize<SizeCalibration>(old);
        Assert.NotNull(c);
        Assert.False(SizeEstimator.IsCurrent(c));
        var fresh = JsonSerializer.Deserialize<SizeCalibration>(JsonSerializer.Serialize(RunWithoutGrass()));
        Assert.True(SizeEstimator.IsCurrent(fresh));
    }
}
