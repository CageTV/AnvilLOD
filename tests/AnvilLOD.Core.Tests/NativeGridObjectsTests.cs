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
    [InlineData(@"meshes\clutter\common\candlelanternwithcandle01.nif", NativeGridObjects.Kinds.Candle, true)]
    [InlineData(@"meshes\clutter
uins
uinsfloorcandlelampsmon.nif", NativeGridObjects.Kinds.Candle, true)]
    [InlineData(@"meshes\dlc02\clutter\dlc2darkelflantern06.nif", NativeGridObjects.Kinds.Candle, true)]
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

    [Fact]
    public void Water_planes_stay_with_the_plugin_when_the_fake_water_option_is_on_but_other_streams_do_not()
    {
        var plane = Grid(@"meshes\dyndolod\lod\water\tundrastreamtransition01_dyndolod_lod.nif", grid: DynamicGrid.Near);
        var rapids = Grid(@"meshes\effects\fxrapids.nif", grid: DynamicGrid.Near);
        Assert.Single(NativeGridObjects.Select([plane, rapids], NativeGridObjects.Kinds.Effects, waterStandIns: true));
        Assert.Equal(2, NativeGridObjects.Select([plane, rapids], NativeGridObjects.Kinds.Effects).Count);
    }
}
