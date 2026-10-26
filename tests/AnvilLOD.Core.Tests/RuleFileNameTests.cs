using AnvilLOD.Core.Lod;
using AnvilLOD.Plugins.Authoring;

namespace AnvilLOD.Core.Tests;

public class RuleFileNameTests
{
    [Theory]
    [InlineData("DynDOLOD_SSE_folipesp_high.ini", true)]
    [InlineData("DynDOLOD_SSE_aclearmapofskyrimesp_low.ini", true)]
    [InlineData("DynDOLOD_SSE_tmddirtroadsesp.ini", true)]
    [InlineData("DynDOLOD_SSE_ussep_all.ini", false)]        // "ussep" is not a plugin key (no esp/esm/esl at its end)
    [InlineData("DynDOLOD_SSE_skyrimesm.ini", true)]
    [InlineData("DynDOLOD_SSE_high.ini", true)]
    [InlineData("DynDOLOD_SSE_all.ini", true)]
    [InlineData("DynDOLOD_SSE_candles_high.ini", true)]
    [InlineData("DynDOLOD_SSE_fxglow_medium.ini", true)]
    [InlineData("DynDOLOD_SSE_TMD Dirt Roads LOD.ini", false)] // named after the mod: never read
    [InlineData("DynDOLOD_SSE_AnvilLOD LOD Meshes.ini", false)]
    public void Only_names_a_plugin_or_a_shared_file_can_be_read(string name, bool readable)
        => Assert.Equal(readable, LodRules.IsReadableRuleFileName(name));

    [Fact]
    public void The_mesh_maker_names_the_rule_file_for_the_plugin()
    {
        Assert.Equal("DynDOLOD_SSE_tmddirtroadsesp.ini", LodMeshMaker.RuleFileName("TMD Dirt Roads LOD", "TMDDirtRoads.esp"));
        Assert.True(LodRules.IsReadableRuleFileName(LodMeshMaker.RuleFileName("TMD Dirt Roads LOD", "TMDDirtRoads.esp")));
        Assert.Equal("DynDOLOD_SSE_TMD Dirt Roads LOD.ini", LodMeshMaker.RuleFileName("TMD Dirt Roads LOD", null));
    }

    [Fact]
    public void A_plugin_named_rule_file_beats_folips_broad_road_mask()
    {
        // FOLIP's "\road" mask (no LOD16) also matches TheMilkDrinker\TMDDirtRoads\road3way01.nif; the road mod's own file has to win.
        var files = new Dictionary<string, Func<string>>
        {
            ["DynDOLOD_SSE_folipesp_high.ini"] = () => "[Skyrim LODGen]\nLODGen5=\\road,Level0,Level1,None,Level3,FarLOD,Unchanged,1,FOLIP\n",
            ["DynDOLOD_SSE_tmddirtroadsesp.ini"] = () => "[Skyrim LODGen]\nLODGen1=themilkdrinker\\tmddirtroads\\road3way01.nif,Level0,Level1,Level2,Level2,FarLOD,Unchanged,0,Maker\n",
        };
        var rules = LodRules.Load(null, files, ["Skyrim.esm", "FOLIP.esp", "TMDDirtRoads.esp"], LodPreset.High);
        var rule = rules.Match(null, null, @"meshes\TheMilkDrinker\TMDDirtRoads\road3way01.nif");
        Assert.Equal(LodChoiceKind.Level, rule.Lod16.Kind);
        Assert.Equal(2, rule.Lod16.Level);

        // Named after the mod instead, the file is never selected and FOLIP's rule applies: no LOD16 (what the TMD roads got).
        files.Remove("DynDOLOD_SSE_tmddirtroadsesp.ini");
        files["DynDOLOD_SSE_TMD Dirt Roads LOD.ini"] = () => "[Skyrim LODGen]\nLODGen1=themilkdrinker\\tmddirtroads\\road3way01.nif,Level0,Level1,Level2,Level2,FarLOD,Unchanged,0,Maker\n";
        var broken = LodRules.Load(null, files, ["Skyrim.esm", "FOLIP.esp", "TMDDirtRoads.esp"], LodPreset.High);
        Assert.Equal(LodChoiceKind.None, broken.Match(null, null, @"meshes\TheMilkDrinker\TMDDirtRoads\road3way01.nif").Lod16.Kind);
    }
}
