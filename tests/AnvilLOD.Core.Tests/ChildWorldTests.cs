using AnvilLOD.Core.Lod;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class ChildWorldTests
{
    private const string Whiterun = """
        ; parent world
        [Tamriel]
        ScanChild=1
        ScanParent=1
        NoCellsWithNAVM=0
        NoScanIfPluginExists=Open Cities Skyrim.esp,SR Exterior Cities.esp
        IgnoreChildEDIDLow=treepine, rock, door
        IgnoreChildEDIDHigh=rock, door
        Bounds=4,-3,7,1
        """;

    [Fact]
    public void Parses_parent_and_preset_ignore_list()
    {
        var c = ChildWorldCopies.Parse("WhiterunWorld", Whiterun, new HashSet<string>(["Skyrim.esm"]), LodPreset.High, "x.ini");
        Assert.NotNull(c);
        Assert.Equal("Tamriel", c!.Parent);
        Assert.False(c.NoCellsWithNavmesh);
        Assert.True(c.IgnoresEditorId("WRDoorCastle01"));
        Assert.False(c.IgnoresEditorId("TreePineForest01"));   // only ignored on Low
        Assert.False(c.IgnoresEditorId("WRCastleMainBuilding01"));
    }

    [Fact]
    public void Blocking_plugin_or_scan_off_disables_copy()
    {
        Assert.Null(ChildWorldCopies.Parse("WhiterunWorld", Whiterun,
            new HashSet<string>(["Open Cities Skyrim.esp"], StringComparer.OrdinalIgnoreCase), LodPreset.High, "x.ini"));
        Assert.Null(ChildWorldCopies.Parse("WindhelmPitWorldspace", "[Tamriel]\nScanChild=0\n", new HashSet<string>(), LodPreset.High, "x.ini"));
    }

    [Fact]
    public void Matches_need_both_plugins()
    {
        var c = new ChildWorldCopies();
        var loaded = new HashSet<string>(["Skyrim.esm", "JKs Skyrim.esp"], StringComparer.OrdinalIgnoreCase);
        c.AddMatches("jks skyrim.esp;00001353;jks skyrim.esp;00004797;desc\nlegacyofthedragonborn.esm;0013D98A;legacyofthedragonborn.esm;0013DD41;x", loaded);
        Assert.True(c.IsMatched(LodRules.FormIdKey("JKs Skyrim.esp", 0x1353)));
        Assert.False(c.IsMatched(LodRules.FormIdKey("LegacyoftheDragonborn.esm", 0x13D98A)));
    }

    [Fact]
    public void Explicit_level_beats_generic_lod_name_in_same_folder()
    {
        var idx = LodMeshIndex.Build([@"meshes\lod\whiterun\wrcastle_lod.nif", @"meshes\lod\whiterun\wrcastle_lod_2.nif"]);
        var f = idx.Find(@"meshes\architecture\wrcastle.nif")!;
        Assert.Equal(@"meshes\lod\whiterun\wrcastle_lod.nif", f[0]);
        Assert.Equal(@"meshes\lod\whiterun\wrcastle_lod_2.nif", f[2]);
    }

    [Fact]
    public void Mesh_lookup_redirects_named_lod()
    {
        var map = LodMeshResolver.ParseMeshLookup("// comment\nmeshes\\mbi\\3amtrees\\3am_juniperpine1.nif=meshes\\mbi\\3amtrees\\3am_juniperpine1dark.nif\n");
        var idx = LodMeshIndex.Build([@"meshes\lod\3am_juniperpine1dark_lod_0.nif"]);
        var res = new LodMeshResolver(idx, new LodRules(), _ => true, map)
            .Resolve(null, null, @"meshes\mbi\3amtrees\3am_juniperpine1.nif", null);
        Assert.Equal(@"meshes\lod\3am_juniperpine1dark_lod_0.nif", res.Meshes.Lod4);
    }

    [Fact]
    public void Birds_rule_file_with_section_header_and_duplicate_numbers()
    {
        var rules = new LodRules();
        rules.AddFile("DynDOLOD_SSE_BIRDSesl.ini", "[Skyrim LODGen]\nLODGen1=Critters\\Seagull\\SKY_SeagullFlapX.nif,,,,Far LOD,Replace,0\nLODGen7=a.nif,,,,Far LOD,Replace,0\nLODGen7=DLC01\\Critters\\DLC1SkullHawkNoNest.nif,,,,Far LOD,Replace,0\n");
        Assert.Equal(3, rules.Count);
        var r = rules.Match(null, null, @"meshes\critters\seagull\sky_seagullflapx.nif");
        Assert.Equal(LodChoiceKind.None, r.Lod4.Kind);
        Assert.Equal("DynDOLOD_SSE_BIRDSesl.ini", r.SourceFile);
    }

    [Fact]
    public void FormId_rules_ignore_load_order_prefix()
    {
        var rules = new LodRules();
        rules.AddFile("DynDOLOD_markarthentranceoverhaulesp.ini",
            "[Skyrim LODGen]\nLODGen347=Markarth Entrance Overhaul.esp;0600187D,Full model,Static LOD8,Static LOD16,,Unchanged,0\nLODGen1=Light.esp;FE01A801,,,,,Delete,0\n");
        Assert.True(rules.HasFormIdRule(LodRules.FormIdKey("Markarth Entrance Overhaul.esp", 0x187D)));
        Assert.True(rules.HasFormIdRule(LodRules.FormIdKey("Light.esp", 0x801)));
    }

    [Fact]
    public void Settings_section_ignore_worlds()
    {
        var rules = new LodRules();
        rules.AddFile("DynDOLOD_SSE_riftendocksoverhaulesp.ini", "[Skyrim Settings]\nIgnoreWorlds=RiftenWorld\n", "Riften Docks Overhaul.esp");
        rules.AddModWorldIgnore("// x\nJKs Skyrim.esp=MarkarthWorld,SolitudeWorld\n");
        Assert.True(rules.IgnoresWorld("riften docks overhaul.esp", "RiftenWorld"));
        Assert.False(rules.IgnoresWorld("Riften Docks Overhaul.esp", "Tamriel"));
        Assert.True(rules.IgnoresWorld("JKs Skyrim.esp", "SolitudeWorld"));
        Assert.Equal(0, rules.Count);
    }
}
