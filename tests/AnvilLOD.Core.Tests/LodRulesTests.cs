using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

public class LodRulesTests
{
    [Fact]
    public void Parses_seven_and_nine_column_rules()
    {
        var r7 = LodRules.ParseRule("roadchunk,Static LOD4,Static LOD8,Static LOD16,,Unchanged,1", "a.ini")!;
        Assert.Equal(LodChoice.Lod(0), r7.Lod4);
        Assert.Equal(LodChoice.Lod(2), r7.Lod32); // LOD32 follows LOD16 in the 7-column format
        var r9 = LodRules.ParseRule("\\road,Level0,Level1,None,Level3,FarLOD,Unchanged,1,FOLIP - High", "b.ini")!;
        Assert.Equal(LodChoice.None, r9.Lod16);
        Assert.Equal(LodChoice.Lod(3), r9.Lod32);
        var full = LodRules.ParseRule("dlc2dwecluttercolmlarge01,Full model,,,Far LOD,Unchanged,0", "c.ini")!;
        Assert.Equal(LodChoiceKind.FullModel, full.Lod4.Kind);
        Assert.Equal(LodChoiceKind.None, full.Lod8.Kind);
    }

    [Fact]
    public void FormId_rules_win_over_mesh_masks_and_first_mesh_rule_wins()
    {
        var rules = new LodRules();
        rules.AddFile("x.ini", """
            [Skyrim LODGen]
            LODGen2=\,Static LOD4,Static LOD8,Static LOD16,Far LOD,Unchanged,1
            LODGen1=roadchunk,Static LOD4,,,,Unchanged,1
            LODGen3=skyrim.esm;00071C5E,None,None,None,,Unchanged,0
            """);
        Assert.Equal(LodChoiceKind.None, rules.Match(null, "skyrim.esm;71c5e", "meshes\\roadchunk01.nif").Lod4.Kind);
        var road = rules.Match(null, "skyrim.esm;1234", "Meshes\\Architecture\\RoadChunk01.nif");
        Assert.Equal("roadchunk", road.Mask);   // LODGen1 sorts before LODGen2
        Assert.Equal(LodChoiceKind.None, road.Lod8.Kind);
        Assert.Equal("\\", rules.Match(null, null, "meshes\\rock.nif").Mask);
        Assert.Equal("skyrim.esm;71c5e", LodRules.FormIdKey("Skyrim.esm", 0x0071C5E));
    }

    [Fact]
    public void Plugin_keys_match_dyndolod_file_names()
    {
        Assert.Equal("mpxp00seluaiiesp", LodRules.PluginKey("MPXP0.0 [SELUA-II].esp"));
        Assert.Equal("cceejsse001hsteadesm", LodRules.PluginKey("cceejsse001-hstead.esm"));
    }

    [Fact]
    public void Named_lod_index_respects_folder_order_and_level_fallback()
    {
        var idx = LodMeshIndex.Build([
            @"meshes\architecture\lod\house_lod_1.nif",
            @"meshes\lod\other\house_lod_1.nif",       // meshes\lod beats a plain meshes subfolder
            @"meshes\lod\other\house_lod_2.nif",
            @"meshes\rocks\rock_lod.nif",                // generic name = levels 0-2
            @"meshes\rocks\rock01.nif",
        ]);
        var house = idx.Find(@"Meshes\Architecture\House.nif")!;
        Assert.Equal(@"meshes\lod\other\house_lod_1.nif", house[0]); // level 0 falls back to level 1
        Assert.Equal(@"meshes\lod\other\house_lod_1.nif", house[1]);
        Assert.Equal(@"meshes\lod\other\house_lod_2.nif", house[2]);
        Assert.Null(house[3]);
        Assert.Equal(@"meshes\rocks\rock_lod.nif", idx.Find(@"meshes\x\rock.nif")![2]);
        Assert.Null(idx.Find(@"meshes\x\nothing.nif"));
    }

    [Fact]
    public void Resolver_prefers_mnam_and_fills_from_named()
    {
        var idx = LodMeshIndex.Build([@"meshes\lod\house_lod_0.nif", @"meshes\lod\house_lod_1.nif"]);
        var res = new LodMeshResolver(idx, new LodRules(), _ => true)
            .Resolve(null, null, @"meshes\house.nif", [@"meshes\lod\vanilla_house_lod_0.nif", null, null, null]);
        Assert.Equal(@"meshes\lod\vanilla_house_lod_0.nif", res.Meshes.Lod4);
        Assert.Equal(@"meshes\lod\house_lod_1.nif", res.Meshes.Lod8);
        Assert.True(res.UsedMnam && res.UsedNamedLod);
    }

    [Fact]
    public void Terrain_decode_and_sampling()
    {
        // offset 10, every delta 1: row y starts at 10 + (y+1), each step right adds 1
        var h = TerrainHeights.Decode(10, (_, _) => 1);
        Assert.Equal((10 + 1) * 8f, h[0]);
        Assert.Equal((10 + 1 + 32) * 8f, h[32]);
        Assert.Equal((10 + 2) * 8f, h[33]);
        var t = new TerrainHeights();
        t.Set("Tamriel", 0, 0, h);
        Assert.Equal((10 + 1) * 8f, t.HeightAt("tamriel", 0, 0)!.Value, 3);
        Assert.Equal((11 + 0.5f) * 8f, t.HeightAt("Tamriel", 64, 0)!.Value, 3); // halfway to the next vertex
        Assert.Null(t.HeightAt("Tamriel", -10, 0));
    }

    [Fact]
    public void Buried_triangles_are_dropped_and_vertices_compacted()
    {
        var mat = new LodMaterial(["textures\\a.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f);
        var part = new LodMeshPart
        {
            Material = mat,
            Positions = [new(0, 0, -500), new(100, 0, -500), new(0, 100, -500), new(0, 0, 500), new(100, 0, 500), new(0, 100, 500)],
            UVs = new Vector2[6], Normals = Enumerable.Repeat(Vector3.UnitZ, 6).ToArray(),
            Tangents = Enumerable.Repeat(Vector3.UnitX, 6).ToArray(), Bitangents = Enumerable.Repeat(Vector3.UnitY, 6).ToArray(),
            Triangles = [0, 1, 2, 3, 4, 5],
        };
        var mesh = new LodMesh { Path = "m", Parts = [part] };
        var r = new LodReference("1", "2", null, "T", "A.esp", new Vector3(1000, 1000, 0), Vector3.Zero, 1f,
            new LodMeshSet("m", null, null, null), LodReferenceFlags.None);
        var res = BtoBuilder.Build(new QuadKey("T", LodLevel.Lod4, 0, 0), [r], _ => mesh,
            new BtoBuilder.TerrainTest((_, _) => 0f, 64f));
        Assert.Equal(1, res.Triangles);
        Assert.Equal(3, res.Vertices);
        Assert.Equal(1, res.TrianglesCulled);
    }
}
