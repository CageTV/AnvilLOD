using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class NativeGridObjectsTests
{
    private static DynamicLodReference Grid(string mesh, string world = "Tamriel", bool initiallyDisabled = false, bool glow = false, DynamicGrid grid = DynamicGrid.Far)
    {
        var r = new LodReference("1:Skyrim.esm", "2:Skyrim.esm", null, world, "Skyrim.esm", Vector3.Zero, Vector3.Zero, 1f,
            new LodMeshSet(mesh, null, null, null), LodReferenceFlags.None);
        return new DynamicLodReference(r, "Skyrim.esm", 1, "Skyrim.esm", 0x3C, InitiallyDisabled: initiallyDisabled, Grid: grid, Glow: glow);
    }

    [Fact]
    public void Picks_waterfall_effect_meshes_as_static_and_animated_models_as_movable_static()
    {
        var picks = NativeGridObjects.Select([
            Grid(@"meshes\dyndolod\lod\effects\fxwaterfallbodyslope_68cc9984_dyndolod_lod.nif"),
            Grid(@"meshes\architecture\farmhouse\farmhousewindmill\farmhousewindmillfan.nif"),
            Grid(@"meshes\architecture\farmhouse\lumbermill01waterwheel01.nif", initiallyDisabled: true),
        ]);
        Assert.Equal(3, picks.Count);
        Assert.False(picks[0].Movable);                       // the DynDOLOD base for a waterfall body is a STAT
        Assert.Equal(@"dyndolod\lod\effects\fxwaterfallbodyslope_68cc9984_dyndolod_lod.nif", picks[0].Model);
        Assert.True(picks[1].Movable);                        // a fan animates through sequences: MSTT
        Assert.True(picks[2].Movable);
    }

    [Fact]
    public void Leaves_out_water_planes_fires_glow_and_other_worldspaces()
    {
        var picks = NativeGridObjects.Select([
            Grid(@"meshes\dyndolod\lod\water\water1024_dyndolod_lod.nif"),                          // a water plane stays with the SKSE plugin
            Grid(@"meshes\dyndolod\lod\effects\fxfirewithembers01_dyndolod_lod.nif"),               // fires: a later step
            Grid(@"meshes\lod\whiterun\wrfarmhousewindmillglow_lod_1.nif", glow: true),             // glow windows stay too
            Grid(@"meshes\dyndolod\lod\effects\fxwaterfallthin512x128_dyndolod_lod.nif", world: "MarkarthWorld"),
            Grid(@"meshes\dyndolod\lod\effects\fxwaterfallthin512x128_dyndolod_lod.nif", grid: DynamicGrid.None),
        ]);
        Assert.Empty(picks);
    }

    [Theory]
    [InlineData(@"meshes\dyndolod\lod\effectsxfirewithembers01_dyndolod_lod.nif", NativeGridObjects.Kinds.Fire, true)]
    [InlineData(@"meshes\clutter\woodfires\campfire01burning.nif", NativeGridObjects.Kinds.Fire, true)]
    [InlineData(@"meshes\dyndolod\lod\effectsxlavafall01_dyndolod_lod.nif", NativeGridObjects.Kinds.Lava, false)]
    [InlineData(@"meshes\dyndolod\lod\water	undrastreamtransition01_dyndolod_lod.nif", NativeGridObjects.Kinds.Stream, false)]
    [InlineData(@"meshes\effectsxrapids.nif", NativeGridObjects.Kinds.Stream, true)]
    public void Effects_are_picked_by_kind_and_only_when_asked_for(string mesh, NativeGridObjects.Kinds kind, bool movable)
    {
        Assert.Empty(NativeGridObjects.Select([Grid(mesh, grid: DynamicGrid.Near)]));                           // waterworks only: none of these
        Assert.Empty(NativeGridObjects.Select([Grid(mesh, grid: DynamicGrid.Near)], NativeGridObjects.Kinds.All & ~kind));
        var pick = Assert.Single(NativeGridObjects.Select([Grid(mesh, grid: DynamicGrid.Near)], NativeGridObjects.Kinds.Effects));
        Assert.Equal(movable, pick.Movable);
        Assert.True(pick.Near);
    }

    [Theory]
    [InlineData(@"meshes\clutter\common\candlelanternwithcandle01.nif")]
    [InlineData(@"meshes\dlc02\clutter\dlc2darkelflantern06.nif")]
    [InlineData(@"meshes\clutteruinsuinsfloorcandlelampsmon.nif")]
    public void Candles_and_lanterns_are_never_picked_nobody_sees_them_from_a_distance(string mesh)
        => Assert.Empty(NativeGridObjects.Select([Grid(mesh, grid: DynamicGrid.Near)], NativeGridObjects.Kinds.All));

    [Fact]
    public void Water_planes_stay_with_the_plugin_when_the_fake_water_option_is_on_but_other_streams_do_not()
    {
        var plane = Grid(@"meshes\dyndolod\lod\water\tundrastreamtransition01_dyndolod_lod.nif", grid: DynamicGrid.Near);
        var rapids = Grid(@"meshes\effects\fxrapids.nif", grid: DynamicGrid.Near);
        Assert.Single(NativeGridObjects.Select([plane, rapids], NativeGridObjects.Kinds.Effects, waterStandIns: true));
        Assert.Equal(2, NativeGridObjects.Select([plane, rapids], NativeGridObjects.Kinds.Effects).Count);
    }

    [Fact]
    public void Water_planes_are_recognised_for_the_lift_that_stops_the_flicker()
    {
        Assert.True(NativeGridObjects.IsWaterPlane(@"dyndolod\lod\water\tundrastreamtransition01_dyndolod_lod.nif"));
        Assert.True(NativeGridObjects.IsWaterPlane(@"effects\fxcreekflatlong01.nif"));
        Assert.False(NativeGridObjects.IsWaterPlane(@"dyndolod\lod\effects\fxwaterfallthin512x128_dyndolod_lod.nif"));
        Assert.InRange(NativeGridObjects.WaterDrop, 2f, 40f);
    }

    [Fact]
    public void Effects_are_packed_into_as_few_esl_plugins_as_fit_counting_base_records()
    {
        // 3,360 references over 100 models: two full plugins, not three (a 1,600 cap would have made a nearly empty third).
        var picks = Enumerable.Range(0, 3360)
            .Select(i => NativeGridObjects.Select([Grid($@"meshes\clutter\woodfires\campfire{i % 100}.nif", grid: DynamicGrid.Near)], NativeGridObjects.Kinds.Effects)[0])
            .ToList();
        var chunks = UndersidePluginWriter.PackEffects(picks).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(picks.Count, chunks.Sum(c => c.Count));
        Assert.All(chunks, c => Assert.True(c.Count + c.Select(p => (p.Model, p.Movable)).Distinct().Count() <= UndersidePluginWriter.EslBudget));
        Assert.Empty(UndersidePluginWriter.PackEffects([]));
    }

    [Fact]
    public void Large_range_packs_up_to_4088_records_per_plugin()
    {
        var picks = Enumerable.Range(0, 6000)
            .Select(i => NativeGridObjects.Select([Grid($@"meshes\clutter\woodfires\campfire{i % 100}.nif", grid: DynamicGrid.Near)], NativeGridObjects.Kinds.Effects)[0])
            .ToList();
        Assert.Equal(2, UndersidePluginWriter.PackEffects(picks, large: true).Count());
        Assert.Equal(4, UndersidePluginWriter.PackEffects(picks, large: false).Count());   // 6,000 refs + 100 bases per plugin: 1,940 per 2,040
    }

    [Fact]
    public void A_large_range_esl_round_trips_with_header_171_and_low_form_ids()
    {
        var mod = new Mutagen.Bethesda.Skyrim.SkyrimMod(Mutagen.Bethesda.Plugins.ModKey.FromNameAndExtension("T.esp"), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        mod.ModHeader.Stats.Version = 1.71f;
        mod.ModHeader.Stats.NextFormID = 1;
        for (int i = 0; i < 3000; i++)
            mod.Statics.Add(new Mutagen.Bethesda.Skyrim.Static(mod) { EditorID = "S" + i });
        var path = Path.Combine(Path.GetTempPath(), "AnvilLOD_esl_" + Guid.NewGuid().ToString("N") + ".esp");
        try
        {
            mod.WriteToBinary(path, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters { ModKey = Mutagen.Bethesda.Plugins.Binary.Parameters.ModKeyOption.CorrectToPath, LowerRangeDisallowedHandler = new Mutagen.Bethesda.Plugins.Binary.Parameters.NoCheckIfLowerRangeDisallowed() });
            using var back = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(path, Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            Assert.True(back.IsSmallMaster);
            Assert.Equal(1.71f, back.ModHeader.Stats.Version, 2);
            Assert.Equal(3000, back.Statics.Count);
            Assert.Equal(1u, back.Statics.Min(x => x.FormKey.ID));
            Assert.True(back.Statics.Max(x => x.FormKey.ID) < 0x1000);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_game_version_comes_from_the_skse_runtime_dll_when_the_exe_reports_nothing_useful()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AnvilLOD_skse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(SksePluginInstaller.VersionFromSkseFiles(dir));
            File.WriteAllBytes(Path.Combine(dir, "skse64_1_5_97.dll"), []);
            File.WriteAllBytes(Path.Combine(dir, "skse64_loader.exe"), []);
            Assert.Equal(new Version(1, 5, 97, 0), SksePluginInstaller.VersionFromSkseFiles(dir));
            File.WriteAllBytes(Path.Combine(dir, "skse64_1_6_1170.dll"), []);
            Assert.Equal(SkseDllChoice.UpTo1170, SksePluginInstaller.ForVersion(SksePluginInstaller.VersionFromSkseFiles(dir)!));
        }
        finally { Directory.Delete(dir, true); }
    }
}
