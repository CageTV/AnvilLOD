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
