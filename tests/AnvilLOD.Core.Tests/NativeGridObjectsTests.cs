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
}
