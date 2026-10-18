using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class Tree3DTests
{
    [Fact]
    public void Crc32_is_the_standard_zlib_checksum()
    {
        Assert.Equal(0xCBF43926u, Tree3DModels.Crc32(Encoding.ASCII.GetBytes("123456789")));
        Assert.Equal(0u, Tree3DModels.Crc32([]));
    }

    [Theory]
    [InlineData("Meshes\\Landscape\\Trees\\ReachTree01.nif", "reachtree01")]
    [InlineData("meshes/landscape/trees/treepineforest01.nif", "treepineforest01")]
    [InlineData("TreeAspen01.NIF", "treeaspen01")]
    public void Stem_is_the_lower_case_file_name_without_folder_or_extension(string model, string stem)
        => Assert.Equal(stem, Tree3DModels.Stem(model));

    [Fact]
    public void Model_names_follow_DynDOLODs_scheme()
    {
        // DynDOLOD ships reachtree01_6FF04C1Fpassthru_lod.nif; Exists() in the real index is case-insensitive.
        Assert.Equal("meshes\\dyndolod\\lod\\trees\\reachtree01_6ff04c1fpassthru_lod.nif",
            Tree3DModels.CrcPath("Meshes\\Landscape\\Trees\\ReachTree01.nif", 0x6FF04C1Fu));
        Assert.Equal("meshes\\dyndolod\\lod\\trees\\reachtree01passthru_lod.nif",
            Tree3DModels.PlainPath("Meshes\\Landscape\\Trees\\ReachTree01.nif"));
    }

    [Fact]
    public void Crc_match_wins_and_the_plain_name_is_only_used_when_asked()
    {
        const string model = "Meshes\\Landscape\\Trees\\ReachTree01.nif";
        var crc = Tree3DModels.CrcPath(model, 0x1234ABCDu);
        var plain = Tree3DModels.PlainPath(model);

        // both present: the checksum match
        var both = Tree3DModels.Resolve(model, 0x1234ABCDu, plainNameFallback: true, p => p == crc || p == plain);
        Assert.Equal(Tree3DModels.MatchKind.Crc32, both!.Kind);
        Assert.Equal(crc, both.Path);

        // only the plain name: ignored unless the option is on
        Assert.Null(Tree3DModels.Resolve(model, 0x1234ABCDu, plainNameFallback: false, p => p == plain));
        var byName = Tree3DModels.Resolve(model, 0x1234ABCDu, plainNameFallback: true, p => p == plain);
        Assert.Equal(Tree3DModels.MatchKind.PlainName, byName!.Kind);
        Assert.Equal(plain, byName.Path);

        // a model for another version of the mesh (different checksum) is not a match
        var other = Tree3DModels.CrcPath(model, 0x0BADF00Du);
        Assert.Null(Tree3DModels.Resolve(model, 0x1234ABCDu, plainNameFallback: false, p => p == other));
    }

    [Fact]
    public void Settings_fingerprint_changes_with_every_option_and_is_empty_when_off()
    {
        Assert.Equal("", Tree3DSettings.Off.Fingerprint);
        var l4 = new Tree3DSettings(true).Fingerprint;
        var l48 = new Tree3DSettings(true, Lod8: true).Fingerprint;
        var name = new Tree3DSettings(true, PlainNameFallback: true).Fingerprint;
        Assert.Equal(3, new[] { l4, l48, name }.Distinct().Count());
        Assert.NotEqual("", l4);
    }

    [Fact]
    public void Tree_reference_defaults_keep_existing_behaviour()
    {
        var t = new TreeReference("1:A.esp", "Tamriel", new(0, 0, 0), 0f, 1f, 1u, new TreeBillboard("x.dds", 1, 1, 0));
        Assert.Null(t.Model3D);
        Assert.False(t.Model3DByName);
        Assert.False(t.ObjectLod);
    }
}

public class ParallaxGenDiffTests
{
    private const string Json = """
        {
          "meshes\\landscape\\trees\\reachtree01.nif": { "crc32original": 1879198751, "crc32patched": 305419896 },
          "Meshes/Landscape/Trees/Aspen01.nif": { "crc32original": 7, "crc32patched": 9 },
          "meshes\\broken.nif": { "crc32original": "x" }
        }
        """;

    private static Dictionary<string, ParallaxGenDiff.Entry> Load(string json) => ParallaxGenDiff.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), out _);

    [Fact]
    public void Entries_are_read_by_normalised_path_and_broken_ones_are_left_out()
    {
        var map = Load(Json);
        Assert.Equal(2, map.Count);
        Assert.Equal(new ParallaxGenDiff.Entry(1879198751u, 305419896u), map[@"meshes\landscape\trees\reachtree01.nif"]);
        Assert.True(map.ContainsKey(@"meshes\landscape\trees\aspen01.nif"));   // forward slashes and case are normalised
    }

    [Fact]
    public void A_file_that_is_not_json_gives_an_empty_map_and_a_reason()
    {
        var map = ParallaxGenDiff.Parse(new MemoryStream("not json"u8.ToArray()), out var problem);
        Assert.Empty(map);
        Assert.NotNull(problem);
    }

    [Fact]
    public void The_original_checksum_is_tried_first_only_for_the_exact_patched_file()
    {
        var map = Load(Json);
        const string mesh = @"Meshes\Landscape\Trees\ReachTree01.nif";
        Assert.Equal([1879198751u, 305419896u], ParallaxGenDiff.LodCrcs(map, mesh, 305419896u));
        // A different file under that name (a later mod update that PGPatcher has not run on yet): its own checksum only.
        Assert.Equal([123u], ParallaxGenDiff.LodCrcs(map, mesh, 123u));
        Assert.Equal([5u], ParallaxGenDiff.LodCrcs(map, @"Meshes\Other.nif", 5u));
        Assert.Equal([5u], ParallaxGenDiff.LodCrcs(null, mesh, 5u));
    }

    [Fact]
    public void Resolve_tries_every_checksum_before_the_plain_name()
    {
        const string model = @"Meshes\Landscape\Trees\ReachTree01.nif";
        var original = Tree3DModels.CrcPath(model, 0x1111u);
        var patched = Tree3DModels.CrcPath(model, 0x2222u);
        var plain = Tree3DModels.PlainPath(model);

        var m = Tree3DModels.Resolve(model, [0x1111u, 0x2222u], true, p => p == original || p == patched || p == plain);
        Assert.Equal(original, m!.Path);   // a pack made for the unpatched tree wins
        m = Tree3DModels.Resolve(model, [0x1111u, 0x2222u], true, p => p == patched || p == plain);
        Assert.Equal(patched, m!.Path);    // the generator that names by the installed mesh still matches
        m = Tree3DModels.Resolve(model, [0x1111u, 0x2222u], true, p => p == plain);
        Assert.Equal(Tree3DModels.MatchKind.PlainName, m!.Kind);
        Assert.Null(Tree3DModels.Resolve(model, [0x1111u, 0x2222u], false, p => p == plain));
    }
}
