using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class SksePluginInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anvillod-skse-" + Guid.NewGuid().ToString("N"));

    public SksePluginInstallerTests()
    {
        // a tool folder with the three bundled builds, each DLL holding its own folder name
        foreach (var f in new[] { SksePluginInstaller.FolderUpTo1170, SksePluginInstaller.FolderNewer, SksePluginInstaller.FolderVr })
        {
            var d = Directory.CreateDirectory(Path.Combine(_dir, "tool", "SKSE", f)).FullName;
            File.WriteAllText(Path.Combine(d, "AnvilLOD.dll"), f);
        }
        Directory.CreateDirectory(Path.Combine(_dir, "game", "Data"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Tool => Path.Combine(_dir, "tool");
    private string Data => Path.Combine(_dir, "game", "Data");
    private string Output => Path.Combine(_dir, "out");
    private string Installed => File.ReadAllText(Path.Combine(Output, "SKSE", "Plugins", "AnvilLOD.dll"));

    [Theory]
    [InlineData(1, 4, 15, 0, SkseDllChoice.Vr)]          // Skyrim VR
    [InlineData(1, 5, 97, 0, SkseDllChoice.UpTo1170)]
    [InlineData(1, 6, 1170, 0, SkseDllChoice.UpTo1170)]
    [InlineData(1, 6, 1179, 0, SkseDllChoice.Newer)]
    [InlineData(1, 7, 29, 0, SkseDllChoice.Newer)]
    public void The_game_version_picks_the_build(int a, int b, int c, int d, SkseDllChoice expected) =>
        Assert.Equal(expected, SksePluginInstaller.ForVersion(new Version(a, b, c, d)));

    [Fact]
    public void Choosing_VR_copies_the_VR_build()
    {
        var r = SksePluginInstaller.Install(Output, Data, SkseDllChoice.Vr, Tool);
        Assert.True(r.Installed, r.Message);
        Assert.Equal(SkseDllChoice.Vr, r.Used);
        Assert.Equal(SksePluginInstaller.FolderVr, Installed);
    }

    [Fact]
    public void Every_choice_the_app_offers_maps_to_its_own_build()
    {
        // The app's drop-down stores the SkseDllChoice numbers; none of them may be mapped to another build (VR once ended up as "installed separately").
        Assert.Equal(["Auto", "UpTo1170", "Newer", "None", "Vr"], Enum.GetValues<SkseDllChoice>().OrderBy(v => (int)v).Select(v => v.ToString()));
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<SkseDllChoice>().Select(v => (int)v).OrderBy(v => v));
        foreach (var (choice, folder) in new[] { (SkseDllChoice.UpTo1170, SksePluginInstaller.FolderUpTo1170), (SkseDllChoice.Newer, SksePluginInstaller.FolderNewer), (SkseDllChoice.Vr, SksePluginInstaller.FolderVr) })
        {
            Assert.True(SksePluginInstaller.Install(Output, Data, choice, Tool).Installed);
            Assert.Equal(folder, Installed);
        }
    }

    [Fact]
    public void Installed_separately_leaves_nothing_in_the_output()
    {
        SksePluginInstaller.Install(Output, Data, SkseDllChoice.Vr, Tool);
        var r = SksePluginInstaller.Install(Output, Data, SkseDllChoice.None, Tool);
        Assert.False(r.Installed);
        Assert.False(File.Exists(Path.Combine(Output, "SKSE", "Plugins", "AnvilLOD.dll")));
    }

    [Fact]
    public void Auto_without_a_readable_game_exe_says_so_and_copies_nothing()
    {
        var r = SksePluginInstaller.Install(Output, Data, SkseDllChoice.Auto, Tool);
        Assert.False(r.Installed);
        Assert.Contains("SkyrimVR.exe", r.Message);
    }
}
