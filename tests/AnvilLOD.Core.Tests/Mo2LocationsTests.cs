using AnvilLOD.Plugins.Mo2;

namespace AnvilLOD.Core.Tests;

public class Mo2LocationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvillod-mo2-" + Guid.NewGuid().ToString("N"));

    public Mo2LocationsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Dir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private string Instance(string iniBody)
    {
        var inst = Dir("instance");
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"), iniBody);
        return inst;
    }

    [Fact]
    public void Without_typed_folders_everything_comes_from_the_ini()
    {
        var game = Dir("game"); Dir("game\\Data");
        var inst = Instance($"[General]\ngamePath={game.Replace("\\", "\\\\")}\nselected_profile=Default\n");
        var i = Mo2Instance.Open(inst);
        Assert.Equal(game, i.GamePath);
        Assert.Equal(Path.Combine(inst, "mods"), i.ModsFolder);
        Assert.Equal(Path.Combine(inst, "profiles"), i.ProfilesFolder);
        Assert.Equal("Default", i.SelectedProfile);
    }

    [Fact]
    public void Typed_folders_win_over_the_ini_and_empty_ones_keep_the_auto_detect()
    {
        var wrongGame = Dir("old-game"); Dir("old-game\\Data");
        var game = Dir("other-drive\\game"); Dir("other-drive\\game\\Data");
        var mods = Dir("other-drive\\mods");
        var inst = Instance($"[General]\ngamePath={wrongGame.Replace("\\", "\\\\")}\n");

        var i = Mo2Instance.Open(inst, new Mo2Locations(GamePath: game, ModsFolder: mods, ProfilesFolder: "  "));
        Assert.Equal(game, i.GamePath);
        Assert.Equal(mods, i.ModsFolder);
        Assert.Equal(Path.Combine(inst, "profiles"), i.ProfilesFolder);   // left empty: auto
    }

    [Fact]
    public void The_game_folder_can_be_typed_as_its_Data_folder_and_with_quotes()
    {
        var game = Dir("g"); var data = Dir("g\\Data");
        var inst = Instance("[General]\ngamePath=nowhere\n");
        Assert.Equal(game, Mo2Instance.Open(inst, new Mo2Locations(GamePath: data)).GamePath);
        Assert.Equal(game, Mo2Instance.Open(inst, new Mo2Locations(GamePath: "\"" + game + "\"")).GamePath);
    }

    [Fact]
    public void ModOrganizer_ini_is_not_needed_when_game_mods_and_profiles_are_typed()
    {
        var game = Dir("g"); Dir("g\\Data");
        var inst = Dir("empty-instance");
        var typed = new Mo2Locations(game, Dir("m"), Dir("p"));
        Assert.True(typed.CanStandAlone);
        var i = Mo2Instance.Open(inst, typed);
        Assert.Equal(game, i.GamePath);

        var e = Assert.Throws<FileNotFoundException>(() => Mo2Instance.Open(inst, new Mo2Locations(GamePath: game)));
        Assert.Contains("Locations", e.Message);
        Assert.Throws<FileNotFoundException>(() => Mo2Instance.Open(inst));
    }

    [Fact]
    public void A_game_folder_without_Data_is_reported_clearly()
    {
        var inst = Instance("[General]\ngamePath=" + Path.Combine(_root, "missing").Replace("\\", "\\\\") + "\n");
        var e = Assert.Throws<DirectoryNotFoundException>(() => Mo2Instance.Open(inst));
        Assert.Contains("Locations", e.Message);
    }

    [Fact]
    public void IsEmpty_ignores_blank_entries()
    {
        Assert.True(new Mo2Locations().IsEmpty);
        Assert.True(new Mo2Locations(" ", "", null).IsEmpty);
        Assert.False(new Mo2Locations(OverwriteFolder: "x").IsEmpty);
    }
}
