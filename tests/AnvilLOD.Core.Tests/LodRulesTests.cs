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
        Assert.Equal(LodChoice.None, r7.Lod32); // the 7-column format has no LOD32 column: DynDOLOD gives such objects no LOD32
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
    public void Lod_4_is_the_LOD32_file_and_an_explicit_lod_3_beats_it()
    {
        // FOLIP's road chunks: roadchunkl01_lod_0.nif for LOD4 and roadchunkl01_lod_4.nif for the far levels.
        var roads = LodMeshIndex.Build([@"meshes\lod\roads\roadchunkl01_lod_0.nif", @"meshes\lod\roads\map\roadchunkl01_lod_4.nif"]);
        var r = roads.Find(@"meshes\landscape\roads\roadchunkl01.nif")!;
        Assert.Equal(@"meshes\lod\roads\roadchunkl01_lod_0.nif", r[0]);
        Assert.Equal(@"meshes\lod\roads\map\roadchunkl01_lod_4.nif", r[3]);
        Assert.Equal(@"meshes\lod\roads\map\roadchunkl01_lod_4.nif", r[1]); // missing lower levels fall back to the next higher one

        var both = LodMeshIndex.Build([@"meshes\lod\a_lod_3.nif", @"meshes\lod\a_lod_4.nif"]).Find(@"meshes\x\a.nif")!;
        Assert.Equal(@"meshes\lod\a_lod_3.nif", both[3]);
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

public class GridObjectTests
{
    [Fact]
    public void Grid_column_is_read_in_both_formats()
    {
        var water = LodRules.ParseRule("water1024.nif,,,,Near LOD,Unchanged,0", "high.ini")!;
        Assert.Equal(DynamicGrid.Near, water.Grid);
        Assert.True(water.IsGridObject);

        var fan = LodRules.ParseRule("farmhousewindmillfan,,,,Far LOD,Replace,0", "high.ini")!;
        Assert.Equal(DynamicGrid.Far, fan.Grid);
        Assert.True(fan.IsGridObject);

        var nine = LodRules.ParseRule("giantcampfire,None,None,None,None,Far Full,Unchanged,0,desc", "x.ini")!;
        Assert.Equal(DynamicGrid.FarFull, nine.Grid);
        Assert.True(nine.IsGridObject);

        // Objects with static LOD keep it; the grid column only matters for dynamic-only objects.
        var catchAll = LodRules.ParseRule("\\,Static LOD4,Static LOD8,Static LOD16,Far LOD,Unchanged,1", "high.ini")!;
        Assert.Equal(DynamicGrid.Far, catchAll.Grid);
        Assert.False(catchAll.IsGridObject);

        var none = LodRules.ParseRule("clutter,,,,,Unchanged,0", "high.ini")!;
        Assert.False(none.IsGridObject);
    }

    [Fact]
    public void Grid_mesh_prefers_dyndolod_lod_mesh()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "meshes\\effects\\fxwaterfallbody01.nif",
            "meshes\\dyndolod\\lod\\effects\\fxwaterfallbody01_dyndolod_lod.nif",
            "meshes\\clutter\\windmill.nif",
        };
        Assert.Equal("meshes\\dyndolod\\lod\\effects\\fxwaterfallbody01_dyndolod_lod.nif",
            LodMeshResolver.GridMesh("Effects\\FXWaterfallBody01.nif", DynamicGrid.Near, files.Contains));
        Assert.Equal("meshes\\effects\\fxwaterfallbody01.nif",
            LodMeshResolver.GridMesh("effects\\fxwaterfallbody01.nif", DynamicGrid.FarFull, files.Contains));
        Assert.Equal("meshes\\clutter\\windmill.nif", LodMeshResolver.GridMesh("meshes\\clutter\\windmill.nif", DynamicGrid.Far, files.Contains));
        Assert.Null(LodMeshResolver.GridMesh("meshes\\missing.nif", DynamicGrid.Far, files.Contains));
    }
}

public class SeasonTests
{
    [Fact]
    public void Parses_seasons_of_skyrim_swap_ini()
    {
        var ini = "﻿[Trees]\n0x826~GildergreenEmbiggened.esp|0x8CA~GildergreenEmbiggened.esp\n[Statics]\n;comment\nRockCliff01|RockCliff01Snow\n0xFE001801~Mod.esl|0x02C10E4~Northern Roads.esp\n";
        var sw = SeasonSwaps.Parse(ini);
        Assert.Equal(3, sw.Count);
        Assert.Equal(("Trees", "GildergreenEmbiggened.esp", 0x826u, 0x8CAu), (sw[0].Section, sw[0].Base.Plugin, sw[0].Base.LocalId, sw[0].Swap.LocalId));
        Assert.Equal(("RockCliff01", "RockCliff01Snow"), (sw[1].Base.EditorId, sw[1].Swap.EditorId));
        Assert.Equal((0x801u, 0x2C10E4u), (sw[2].Base.LocalId, sw[2].Swap.LocalId));
        Assert.Equal("WIN", SeasonSwaps.SeasonOf("Floral Sky_WIN.ini"));
        Assert.Null(SeasonSwaps.SeasonOf("Snow_SNOW.ini"));
        Assert.Equal("meshes\\terrain\\tamriel\\objects\\tamriel.4.0.0.WIN.bto",
            SeasonSwaps.SeasonalPath("meshes\\terrain\\tamriel\\objects\\tamriel.4.0.0.bto", "WIN"));
    }

    [Fact]
    public void Brightness_scales_vertex_colour_rgb_only()
    {
        Assert.Equal(0xFF808080u, BtoBuilder.Scale(0xFFFFFFFFu, 0.5f));
        Assert.Equal(0x80020305u, BtoBuilder.Scale(0x80102030u, 0.1f));
        Assert.Equal(0x12345678u, BtoBuilder.Scale(0x12345678u, 1f));
    }
}
