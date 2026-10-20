using AnvilLOD.Core.Lod;

namespace AnvilLOD.Core.Tests;

public sealed class OcclusionLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvil-occ-" + Guid.NewGuid().ToString("N"));

    public OcclusionLauncherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private string Touch(string relative, string text = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Arguments_are_separate_and_have_no_trailing_slash()
    {
        var args = OcclusionLauncher.Arguments(@"C:\Games\Skyrim Special Edition\Data\", @"C:\Temp\run 1\");
        Assert.Equal(["-sse", @"-D:C:\Games\Skyrim Special Edition\Data", @"-o:C:\Temp\run 1"], args);
    }

    [Fact]
    public void The_tool_folder_is_guessed_next_to_the_dyndolod_folder_or_in_the_instance_tools_folder()
    {
        Touch(@"tools\DynDOLOD\DynDOLODx64.exe");
        Touch(@"tools\xLODGen\xLODGenx64.exe");
        var dyn = Path.Combine(_root, "tools", "DynDOLOD");
        Assert.Equal(dyn, OcclusionLauncher.GuessFolder(OcclusionTool.DynDolod, dyn, null));
        Assert.Equal(Path.Combine(_root, "tools", "xLODGen"), OcclusionLauncher.GuessFolder(OcclusionTool.XLodGen, dyn + @"\", null));
        Assert.Equal(Path.Combine(_root, "tools", "xLODGen"), OcclusionLauncher.GuessFolder(OcclusionTool.XLodGen, null, _root));
        Assert.Null(OcclusionLauncher.GuessFolder(OcclusionTool.XLodGen, null, null));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(16, 1)]
    [InlineData(32, 1)]
    [InlineData(48, 2)]
    [InlineData(64, 2)]
    [InlineData(96, 4)]
    [InlineData(256, 4)]
    public void Threads_follow_the_installed_ram(int gb, int expected) =>
        Assert.Equal(expected, OcclusionLauncher.SuggestedThreads(gb * 1024L * 1024 * 1024 - 1024L * 1024 * 200));

    [Fact]
    public void FindExe_needs_the_program_in_the_folder()
    {
        Touch(@"a\xLODGenx64.exe");
        Assert.NotNull(OcclusionLauncher.FindExe(OcclusionTool.XLodGen, Path.Combine(_root, "a")));
        Assert.Null(OcclusionLauncher.FindExe(OcclusionTool.DynDolod, Path.Combine(_root, "a")));
        Assert.Null(OcclusionLauncher.FindExe(OcclusionTool.XLodGen, "  "));
    }

    [Fact]
    public void The_result_is_found_in_the_run_folder_or_below_it()
    {
        Assert.Null(OcclusionLauncher.FindResult(Path.Combine(_root, "none")));
        var run = Path.Combine(_root, "run");
        Directory.CreateDirectory(run);
        Assert.Null(OcclusionLauncher.FindResult(run));
        var deeper = Touch(@"run\sub\Occlusion.esp");
        Assert.Equal(new OcclusionFound(deeper, false), OcclusionLauncher.FindResult(run));
        var direct = Touch(@"run\Occlusion.esp");
        Assert.Equal(new OcclusionFound(direct, false), OcclusionLauncher.FindResult(run));
    }

    [Fact]
    public void A_plugin_saved_to_overwrite_counts_only_when_it_is_newer_than_the_start()
    {
        var run = Path.Combine(_root, "run2");
        Directory.CreateDirectory(run);
        var overwrite = Path.Combine(_root, "overwrite");
        var old = Touch(@"overwrite\Occlusion.esp");
        File.SetLastWriteTime(old, new DateTime(2026, 10, 1));
        var started = new DateTime(2026, 10, 19, 20, 0, 0);
        Assert.Null(OcclusionLauncher.FindResult(run, [overwrite, null, " "], started));
        File.SetLastWriteTime(old, started.AddMinutes(4));
        Assert.Equal(new OcclusionFound(old, true), OcclusionLauncher.FindResult(run, [overwrite], started));
        // the run folder wins when it has one
        var inRun = Touch(@"run2\Occlusion.esp");
        Assert.Equal(new OcclusionFound(inRun, false), OcclusionLauncher.FindResult(run, [overwrite], started));
    }

    [Fact]
    public void Import_can_move_a_plugin_out_of_overwrite()
    {
        var src = Touch(@"overwrite2\Occlusion.esp", "moved");
        var output = Path.Combine(_root, "out2");
        OcclusionLauncher.ImportResult(src, output, move: true);
        Assert.False(File.Exists(src));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(output, "Occlusion.esp")));
    }

    [Fact]
    public void Import_copies_the_plugin_and_keeps_an_older_one_as_a_backup()
    {
        var src = Touch(@"run\Occlusion.esp", "new");
        var output = Path.Combine(_root, "out");
        var first = OcclusionLauncher.ImportResult(src, output, new DateTime(2026, 10, 19, 20, 0, 0));
        Assert.Null(first.BackupOfOld);
        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "Occlusion.esp")));
        Assert.True(File.Exists(src));   // a copy: the run's file stays

        File.WriteAllText(src, "newer");
        var second = OcclusionLauncher.ImportResult(src, output, new DateTime(2026, 10, 19, 21, 0, 0));
        Assert.Equal(Path.Combine(output, "Occlusion.esp.bak_20261019-210000"), second.BackupOfOld);
        Assert.Equal("new", File.ReadAllText(second.BackupOfOld!));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(output, "Occlusion.esp")));
        Assert.Equal(5, second.Bytes);
    }

    [Fact]
    public void Import_without_a_result_says_so()
    {
        Assert.Throws<FileNotFoundException>(() => OcclusionLauncher.ImportResult(Path.Combine(_root, "missing.esp"), _root));
    }

    [Fact]
    public void The_ini_summary_lists_only_the_occlusion_settings()
    {
        Touch(@"DynDOLOD\Edit Scripts\DynDOLOD\DynDOLOD_SSE.ini",
            "; OcclusionHeight=99\nOcclusionHeight=0\nOcclusionRadius=100\nOcclusionMode=-TVDT -Flat +Border\nOcclusionMaxThreadsObjectLOD=4\n" +
            "Occlusion_ArnimaDagonRealm=0\nOcclusionMode_Tamriel=-Flat +Border\nTreeLODMaxTilt=30\n");
        var lines = OcclusionLauncher.ReadIniSummary(Path.Combine(_root, "DynDOLOD"));
        Assert.Equal(["OcclusionHeight=0", "OcclusionRadius=100", "OcclusionMode=-TVDT -Flat +Border", "Occlusion_ArnimaDagonRealm=0", "OcclusionMode_Tamriel=-Flat +Border"], lines);
        Assert.Empty(OcclusionLauncher.ReadIniSummary(Path.Combine(_root, "nowhere")));
    }
}
