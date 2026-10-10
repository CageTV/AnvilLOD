using AnvilLOD.Plugins;
using AnvilLOD.Plugins.Mo2;

namespace AnvilLOD.Core.Tests;

public class OutputFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvillod-out-" + Guid.NewGuid().ToString("N"));

    public OutputFolderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Folder(string name) => Path.Combine(_root, name);

    private string Make(string name, params string[] files)
    {
        var dir = Directory.CreateDirectory(Folder(name)).FullName;
        foreach (var f in files)
        {
            var path = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
        return dir;
    }

    [Fact]
    public void A_missing_folder_is_created()
    {
        var r = OutputFolder.Prepare(Folder("new/AnvilLOD Output"), clear: true);
        Assert.True(r.Created);
        Assert.False(r.Cleared);
        Assert.True(Directory.Exists(Folder("new/AnvilLOD Output")));
        Assert.Null(r.Warning);
    }

    [Fact]
    public void An_anvillod_folder_is_emptied_except_meta_ini()
    {
        var dir = Make("old", "AnvilLOD.manifest.json", "AnvilLOD.log", "meta.ini", "meshes/terrain/tamriel/x.bto", "SKSE/Plugins/AnvilLOD.dll", "AnvilLOD.esp");
        File.SetAttributes(Path.Combine(dir, "AnvilLOD.esp"), FileAttributes.ReadOnly);

        var r = OutputFolder.Prepare(dir, clear: true);

        Assert.True(r.Cleared);
        Assert.Null(r.Warning);
        Assert.Equal(5, r.Deleted);
        Assert.Equal(["meta.ini"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void An_empty_folder_or_one_with_only_meta_ini_counts_as_empty()
    {
        Assert.True(OutputFolder.Prepare(Make("a"), clear: true).Cleared);
        var b = Make("b", "meta.ini");
        Assert.True(OutputFolder.Prepare(b, clear: true).Cleared);
        Assert.True(File.Exists(Path.Combine(b, "meta.ini")));
    }

    [Fact]
    public void A_folder_with_someone_elses_files_is_left_alone()
    {
        var dir = Make("other", "readme.txt", "plugin.esp");
        var r = OutputFolder.Prepare(dir, clear: true);
        Assert.False(r.Cleared);
        Assert.NotNull(r.Warning);
        Assert.True(File.Exists(Path.Combine(dir, "plugin.esp")));
        Assert.True(File.Exists(Path.Combine(dir, "readme.txt")));
    }

    [Theory]
    [InlineData("ModOrganizer.ini")]
    [InlineData("SkyrimSE.exe")]
    [InlineData("Skyrim.esm")]
    [InlineData("mods/x.txt")]
    [InlineData("Data/x.txt")]
    public void Game_and_mod_manager_folders_are_never_emptied_even_with_an_anvillod_file_in_them(string marker)
    {
        var dir = Make("game", "AnvilLOD.manifest.json", marker, "keep.txt");
        var r = OutputFolder.Prepare(dir, clear: true);
        Assert.False(r.Cleared);
        Assert.Contains("game, mod manager or mods folder", r.Warning);
        Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
    }

    [Fact]
    public void A_drive_root_is_refused()
    {
        Assert.NotNull(OutputFolder.WhyNotToClear(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void Without_clear_nothing_is_deleted()
    {
        var dir = Make("keepit", "AnvilLOD.manifest.json", "meshes/a.nif");
        var r = OutputFolder.Prepare(dir, clear: false);
        Assert.False(r.Cleared);
        Assert.True(File.Exists(Path.Combine(dir, "meshes", "a.nif")));
    }

    [Fact]
    public void The_default_folder_is_AnvilLOD_Output_in_the_mods_folder_or_documents()
    {
        var typed = new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: _root, Mo2Locations: new Mo2Locations(ModsFolder: Folder("my mods")));
        Assert.Equal(Path.Combine(Folder("my mods"), "AnvilLOD Output"), OutputFolder.DefaultFolder(typed));

        var data = OutputFolder.DefaultFolder(new GameContextOptions(Folder("Data"), null));
        Assert.Equal("AnvilLOD Output", Path.GetFileName(data));
    }
}
