using AnvilLOD.Core.Lod;

namespace AnvilLOD.Core.Tests;

public class CustomRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anvillod-rules-" + Guid.NewGuid().ToString("N"));

    public CustomRulesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string InstallRules()
    {
        var rules = Directory.CreateDirectory(Path.Combine(_dir, "Rules")).FullName;
        File.WriteAllText(Path.Combine(rules, "DynDOLOD_SSE_all.ini"), "[Skyrim LODGen]\nLODGen1=roadchunk,Static LOD4,,,,Unchanged,1\n");
        File.WriteAllText(Path.Combine(rules, "DynDOLOD_SSE_high.ini"), """
            [Skyrim LODGen]
            LODGen1=tree,Static LOD4,Billboard,Billboard,Far LOD,Unchanged,17
            LODGen2=\,Static LOD4,Static LOD8,Static LOD16,Far LOD,Unchanged,1
            """);
        File.WriteAllText(Path.Combine(rules, "DynDOLOD_SSE_candles_all.ini"), "[Skyrim LODGen]\nLODGen1=impcandle01off,,,,,Unchanged,0\n");
        File.WriteAllText(Path.Combine(rules, "DynDOLOD_SSE_candles_high.ini"), "[Skyrim LODGen]\nLODGen1=impcandle01,,,,Far LOD,Unchanged,0\n");
        File.WriteAllText(Path.Combine(rules, "DynDOLOD_SSE_fxglow_high.ini"), "[Skyrim LODGen]\nLODGen1=fxglowfillroundbrt,,,,Far LOD,Unchanged,0\n");
        return rules;
    }

    private static LodRules Load(string rules, LodRuleOptions? options = null) =>
        LodRules.Load(rules, new Dictionary<string, Func<string>>(), [], LodPreset.High, "SSE", options);

    [Fact]
    public void Candles_and_fxglow_rules_are_only_loaded_when_asked_for()
    {
        var rules = InstallRules();
        Assert.Equal("\\", Load(rules).Match(null, null, "meshes\\lights\\impcandle01.nif").Mask); // no candle rule: the catch-all
        Assert.DoesNotContain(Load(rules).Files, f => f.Contains("candles", StringComparison.OrdinalIgnoreCase));

        var on = Load(rules, new LodRuleOptions(Candles: true, FxGlow: true));
        Assert.Contains("DynDOLOD_SSE_candles_high.ini", on.Files);
        Assert.Contains("DynDOLOD_SSE_fxglow_high.ini", on.Files);
        Assert.Equal(DynamicGrid.Far, on.Match(null, null, "meshes\\fx\\fxglowfillroundbrt.nif").Grid);
        Assert.Equal("fxglowfillroundbrt", on.Match(null, null, "meshes\\fx\\fxglowfillroundbrt.nif").Mask);
    }

    [Fact]
    public void Lit_candle_masks_come_after_their_switched_off_variants()
    {
        var on = Load(InstallRules(), new LodRuleOptions(Candles: true));
        Assert.Equal("impcandle01off", on.Match(null, null, "meshes\\lights\\impcandle01off.nif").Mask);
        Assert.Equal("impcandle01", on.Match(null, null, "meshes\\lights\\impcandle01.nif").Mask);
    }

    [Fact]
    public void Custom_file_comes_first_and_its_catch_all_replaces_the_presets()
    {
        var rules = InstallRules();
        var custom = Write("mine.ini", """
            [Skyrim LODGen]
            LODGen1=tree,Level0,Billboard4,Billboard4,Billboard6,Far LOD,Unchanged,17,my tree
            LODGen2=\,Level0,Level1,Level2,Level0,Far LOD,Unchanged,1,my catch-all
            """);
        var r = Load(rules, new LodRuleOptions(CustomFile: custom));
        Assert.StartsWith("custom: ", r.Files[0]);

        var tree = r.Match(null, null, "meshes\\landscape\\trees\\tree01.nif");
        Assert.Equal(LodChoice.Lod(0), tree.Lod4);
        Assert.Equal(LodChoiceKind.Billboard, tree.Lod32.Kind);

        // the custom catch-all applies to everything the specific rules don't match (the preset's own would say Level3 nowhere)
        var rock = r.Match(null, null, "meshes\\rock.nif");
        Assert.Equal(LodChoice.Lod(0), rock.Lod32);
        Assert.Equal("custom: mine.ini", rock.SourceFile);
        // a rule from DynDOLOD's files still applies where the custom file says nothing
        Assert.Equal("roadchunk", r.Match(null, null, "meshes\\roadchunk01.nif").Mask);
    }

    [Fact]
    public void Without_a_custom_file_the_presets_catch_all_applies_last()
    {
        var r = Load(InstallRules());
        Assert.Equal("\\", r.Match(null, null, "meshes\\rock.nif").Mask);
        Assert.Equal("roadchunk", r.Match(null, null, "meshes\\roadchunk01.nif").Mask);
    }

    [Fact]
    public void Flags_are_read_as_a_bit_mask()
    {
        var rule = LodRules.ParseRule("windhelmbridge,Level0,Level1,Level2,,Unchanged,7", "x.ini")!;
        Assert.Equal(7, rule.Flags);
        Assert.True(rule.NoGlow);
        Assert.False(LodRules.ParseRule("a,Level0,,,,Unchanged,5", "x.ini")!.NoGlow);
        Assert.True(LodRules.ParseRule("a,Level0,Level1,Level2,Level2,Far LOD,Unchanged,2,desc", "x.ini")!.NoGlow);
    }

    [Fact]
    public void Entries_round_trip_through_the_file_format_and_keep_the_catch_all_last()
    {
        var e = new List<RuleEntry>
        {
            new() { Mask = "\\", Lod4 = "Level0", Lod8 = "Level1", Lod16 = "Level2", Lod32 = "Level0", Grid = "Far LOD", Reference = "Unchanged", Flags = 1, IsCustom = true },
            RuleEntry.Parse("tree,Level0,Billboard4,Billboard4,Billboard6,None,Replace,17", "x")!,
        };
        e[1].IsCustom = true;
        e[1].Description = "Hello, world";
        var text = RuleEntry.FormatFile(e);
        var back = RuleEntry.ParseFile(text, "back", true);
        Assert.Equal(["tree", "\\"], back.Select(b => b.Mask));
        Assert.Equal("Billboard6", back[0].Lod32);
        Assert.Equal("Replace", back[0].Reference);
        Assert.Equal(17, back[0].Flags);
        Assert.Equal("Hello; world", back[0].Description); // a comma would split the column
        Assert.Equal("Far LOD", back[1].Grid);

        // and the generator reads the same thing
        var rules = new LodRules();
        rules.AddFile("back", text);
        Assert.Equal(LodChoiceKind.Billboard, rules.Match(null, null, "meshes\\tree.nif").Lod32.Kind);
        Assert.Equal(LodChoice.Lod(0), rules.Match(null, null, "meshes\\rock.nif").Lod32);
    }

    [Fact]
    public void Default_entries_follow_the_load_order_of_the_files()
    {
        var entries = LodRules.DefaultEntries(InstallRules(), LodPreset.High, new LodRuleOptions(Candles: true, FxGlow: true));
        Assert.Equal(["impcandle01off", "impcandle01", "fxglowfillroundbrt", "roadchunk", "tree", "\\"], entries.Select(x => x.Mask));
        Assert.Equal("DynDOLOD_SSE_fxglow_high.ini", entries[2].Source);
        Assert.Empty(LodRules.DefaultEntries(null, LodPreset.High));
    }
}
