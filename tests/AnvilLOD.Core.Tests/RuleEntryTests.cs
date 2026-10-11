using AnvilLOD.Core.Lod;

namespace AnvilLOD.Core.Tests;

public class RuleEntryTests
{
    [Fact]
    public void The_rules_editor_reads_a_seven_column_line_the_way_the_generator_does_with_no_LOD32()
    {
        const string line = "roadchunk,Static LOD4,Static LOD8,Static LOD16,,Unchanged,1";
        var entry = RuleEntry.Parse(line, "a.ini")!;
        Assert.Equal("", entry.Lod32);                                   // shown as None, and saved as None
        Assert.Equal(LodChoice.None, LodRules.ParseRule(line, "a.ini")!.Lod32);
    }

    [Theory]
    [InlineData("Meshes\\_pgpatcher_dups\\2\\effects\\fxcreekflatlong01.nif", "meshes\\dyndolod\\lod\\effects\\fxcreekflatlong01_dyndolod_lod.nif")]
    [InlineData("Meshes\\effects\\fxcreekflatlong01.nif", "meshes\\dyndolod\\lod\\effects\\fxcreekflatlong01_dyndolod_lod.nif")]
    public void A_grid_object_on_a_PGPatcher_duplicate_still_gets_its_dynamic_LOD_model(string model, string expected)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { expected, "meshes\\_pgpatcher_dups\\2\\effects\\fxcreekflatlong01.nif" };
        Assert.Equal(expected, LodMeshResolver.GridMesh(model, DynamicGrid.Near, have.Contains));
        // "Far Full" always uses the full model, the duplicate included
        Assert.Equal(LodMeshIndex.Normalize(model), LodMeshResolver.GridMesh(model, DynamicGrid.FarFull, p => p == LodMeshIndex.Normalize(model)));
    }

    [Fact]
    public void A_nine_column_line_keeps_its_LOD32()
    {
        var entry = RuleEntry.Parse("\\road,Level0,Level1,None,Level3,FarLOD,Unchanged,1,FOLIP - High", "b.ini")!;
        Assert.Equal("Level3", entry.Lod32);
    }

    [Fact]
    public void Saving_a_seven_column_rule_from_the_editor_does_not_invent_a_LOD32()
    {
        var entry = RuleEntry.Parse("rtfarmhouse,Static LOD4,Static LOD8,Static LOD8,,Unchanged,1", "a.ini", isCustom: true)!;
        var text = RuleEntry.FormatFile([entry]);
        var back = LodRules.ParseRule(text.Split('\n').First(l => l.StartsWith("LODGen1=", StringComparison.Ordinal))["LODGen1=".Length..].TrimEnd('\r'), "saved.ini")!;
        Assert.Equal(LodChoice.None, back.Lod32);
        Assert.Equal(LodChoice.Lod(1), back.Lod16);
    }
}
