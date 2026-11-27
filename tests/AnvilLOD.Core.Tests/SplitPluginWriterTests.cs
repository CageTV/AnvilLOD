namespace AnvilLOD.Core.Tests;

using AnvilLOD.Plugins;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

/// <summary>
/// Occlusion.esp overrides a CELL for every exterior cell it has data for, so its masters are the plugins that win those cells.
/// A big load order hands them out to more plugins than one file may list (254), which used to end the run inside Mutagen with
/// "Occlusion.esp has too many masters on masters list". The output is now split over Occlusion.esp, Occlusion_1.esp, ...
/// </summary>
public class SplitPluginWriterTests
{
    private static (string Folder, string Path) Temp()
    {
        var folder = Path.Combine(Path.GetTempPath(), "AnvilLOD_split_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return (folder, Path.Combine(folder, "Occlusion.esp"));
    }

    /// <summary>One Static per master, so the plugin needs exactly <paramref name="count"/> masters.</summary>
    private static SkyrimMod ManyMasters(int count, List<ModKey> order)
    {
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Occlusion.esp"), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        mod.ModHeader.Stats.NextFormID = 0x800;
        order.Add(ModKey.FromNameAndExtension("Skyrim.esm"));
        for (int i = 0; i < count; i++)
        {
            var master = ModKey.FromNameAndExtension($"Master{i}.esp");
            order.Add(master);
            mod.Statics.Add(new Static(new FormKey(master, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "S" + i });
        }
        return mod;
    }

    private static int Masters(string path)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return back.ModHeader.MasterReferences.Count;
    }

    private static int Statics(string path)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return back.Statics.Count;
    }

    [Fact]
    public void A_plugin_that_fits_is_written_as_one_file()
    {
        var (folder, path) = Temp();
        var order = new List<ModKey>();
        try
        {
            var files = SplitPluginWriter.Write(ManyMasters(20, order), path, order);
            Assert.Single(files);
            Assert.Equal(path, files[0]);
            Assert.Equal(20, Masters(path));
            using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            Assert.Equal(0x800u, back.ModHeader.Stats.NextFormID);   // the caller's header is kept, not recomputed to 0
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_light_plugin_holds_254_masters_and_255_is_where_it_splits()
    {
        var order = new List<ModKey>();
        var (folder, path) = Temp();
        try
        {
            Assert.Single(SplitPluginWriter.Write(ManyMasters(SplitPluginWriter.MaxMasters, order), path, order));
            Assert.Equal(SplitPluginWriter.MaxMasters, Masters(path));   // 254: the last slot before 0xFE
        }
        finally { Directory.Delete(folder, true); }

        var more = new List<ModKey>();
        var (folder2, path2) = Temp();
        try
        {
            var files = SplitPluginWriter.Write(ManyMasters(SplitPluginWriter.MaxMasters + 1, more), path2, more);   // 255 do not fit
            Assert.True(files.Count > 1, "255 masters cannot fit in one plugin");
            foreach (var f in files) Assert.InRange(Masters(f), 1, SplitPluginWriter.MaxMasters);
        }
        finally { Directory.Delete(folder2, true); }
    }

    [Fact]
    public void Too_many_masters_are_split_over_plugins_that_each_stay_under_the_limit()
    {
        var (folder, path) = Temp();
        var order = new List<ModKey>();
        try
        {
            var files = SplitPluginWriter.Write(ManyMasters(600, order), path, order);
            Assert.True(files.Count > 1, "600 masters cannot fit in one plugin");
            foreach (var f in files) Assert.InRange(Masters(f), 1, SplitPluginWriter.MaxMasters);
            Assert.Equal(600, files.Sum(Statics));   // nothing lost or duplicated
            Assert.Equal(files.Count, 1 + SplitPluginWriter.Fragments(folder, "Occlusion.esp").Count());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void The_split_files_are_light_plugins_like_the_plugin_they_came_from()
    {
        var (folder, path) = Temp();
        var order = new List<ModKey>();
        try
        {
            foreach (var f in SplitPluginWriter.Write(ManyMasters(600, order), path, order))
            {
                using var back = SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE);
                Assert.True(back.IsSmallMaster, f + " must be ESL-flagged: Occlusion.esp takes no plugin slot");
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Fragments_of_an_earlier_run_do_not_survive_a_run_that_fits()
    {
        var (folder, path) = Temp();
        var order = new List<ModKey>();
        try
        {
            Assert.True(SplitPluginWriter.Write(ManyMasters(600, order), path, order).Count > 1);
            foreach (var f in SplitPluginWriter.Fragments(folder, "Occlusion.esp"))
                File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-5));   // the files an earlier run left
            var files = SplitPluginWriter.Write(ManyMasters(20, order), path, order);
            Assert.Single(files);
            Assert.Empty(SplitPluginWriter.Fragments(folder, "Occlusion.esp"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Only_the_names_this_writer_makes_are_removed_and_listed()
    {
        var (folder, path) = Temp();
        var order = new List<ModKey>();
        var old = DateTime.UtcNow.AddMinutes(-5);
        try
        {
            // A leftover of an earlier, bigger run, and two files that are not split files at all.
            foreach (var name in new[] { "Occlusion_2.esp", "Occlusion_10.esp", "Occlusion_notes.esp", "Occlusion_2.esp.bak" })
            {
                File.WriteAllBytes(Path.Combine(folder, name), []);
                File.SetLastWriteTimeUtc(Path.Combine(folder, name), old);
            }
            Assert.Equal(["Occlusion_2.esp", "Occlusion_10.esp"],
                SplitPluginWriter.Fragments(folder, "Occlusion.esp").Select(Path.GetFileName));   // numbered, not name-sorted
            var files = SplitPluginWriter.Write(ManyMasters(20, order), path, order);
            Assert.Single(files);
            Assert.False(File.Exists(Path.Combine(folder, "Occlusion_2.esp")));
            Assert.False(File.Exists(Path.Combine(folder, "Occlusion_10.esp")));
            Assert.True(File.Exists(Path.Combine(folder, "Occlusion_notes.esp")));
            Assert.True(File.Exists(Path.Combine(folder, "Occlusion_2.esp.bak")));
        }
        finally { Directory.Delete(folder, true); }
    }
}
