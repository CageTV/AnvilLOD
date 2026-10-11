using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class GlowWindowTests
{
    [Fact]
    public void Finds_the_glow_mesh_of_a_model_and_the_grid_from_its_lowest_level()
    {
        var idx = LodMeshIndex.Build([
            @"meshes\lod\farmhouse\farmhouse05_lod_0.nif",
            @"meshes\lod\farmhouse\farmhouse05glow_lod_0.nif",       // near grid
            @"meshes\lod\solitude\sfarmhouse03glow_lod_1.nif",       // far grid
            @"meshes\lod\castle\castleexttower01glow_lod_1.nif",
            @"meshes\lod\castle\castleexttower01glow_lod_2.nif",     // lowest level wins
            @"meshes\lod\museum\museumglow_lod_2.nif",               // never fades
        ]);
        Assert.Equal((@"meshes\lod\farmhouse\farmhouse05glow_lod_0.nif", 0), idx.GlowFor(@"Meshes\Architecture\Farmhouse\Farmhouse05.nif"));
        Assert.Equal((@"meshes\lod\solitude\sfarmhouse03glow_lod_1.nif", 1), idx.GlowFor(@"meshes\architecture\solitude\farms\sfarmhouse03.nif"));
        Assert.Equal((@"meshes\lod\castle\castleexttower01glow_lod_1.nif", 1), idx.GlowFor(@"meshes\x\castleexttower01.nif"));
        Assert.Equal((@"meshes\lod\museum\museumglow_lod_2.nif", 2), idx.GlowFor(@"meshes\x\museum.nif"));
        Assert.Null(idx.GlowFor(@"meshes\x\nothing.nif"));
        // The glow files are not the model's own LOD meshes.
        Assert.Equal(@"meshes\lod\farmhouse\farmhouse05_lod_0.nif", idx.Find(@"meshes\x\farmhouse05.nif")![0]);
    }

    [Fact]
    public void Glow_windows_set_the_glow_flag_in_the_dyn_file()
    {
        var r = new LodReference("1:A.esp", "2:A.esp", null, "Tamriel", "A.esp", Vector3.Zero, Vector3.Zero, 1f,
            new LodMeshSet("m.nif", null, null, null), LodReferenceFlags.None);
        var glow = new DynamicLodReference(r, "A.esp", 1, "Skyrim.esm", 0x3C, Grid: DynamicGrid.Near, Glow: true);
        Assert.Equal(4u | 8u | 32u, DynamicLodWriter.Flags(glow));                  // grid object + near grid + glow
        Assert.Equal(4u | 32u, DynamicLodWriter.Flags(glow with { Grid = DynamicGrid.Far }));
        Assert.Equal(4u | 16u | 32u, DynamicLodWriter.Flags(glow with { Grid = DynamicGrid.NeverFade }));
        Assert.Equal(4u | 8u, DynamicLodWriter.Flags(glow with { Glow = false }));
    }
}
