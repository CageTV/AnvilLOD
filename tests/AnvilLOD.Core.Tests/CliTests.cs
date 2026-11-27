namespace AnvilLOD.Core.Tests;

using AnvilLOD.Cli;

/// <summary>
/// The command line is what modlist build scripts and the launcher drive, so its exit codes are part of the contract:
/// 0 done, 1 ran but the output is incomplete, 2 did not run. A modlist builder that reads 0 as "ship it" must never
/// get 0 from a run that failed.
/// </summary>
public class CliTests
{
    [Fact]
    public void No_arguments_shows_the_usage_and_exits_1() => Assert.Equal(1, Program.Main([]));

    [Fact]
    public void An_unknown_option_exits_2() => Assert.Equal(2, Program.Main(["scan", "--not-an-option"]));

    [Fact]
    public void An_option_that_needs_a_companion_is_refused_instead_of_ignored()
        => Assert.Equal(2, Program.Main(["generate", "--underside-detail", "16"]));

    [Fact]
    public void A_data_folder_without_Skyrim_esm_exits_2_instead_of_crashing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "anvillod-no-such-data-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(2, Program.Main(["scan", "--data", missing]));
    }
}
