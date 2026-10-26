using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Meshes;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Bc;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Core.Tests;

public class PbrLodTests
{
    private sealed class Files(params string[] paths) : IAssetSource
    {
        private readonly HashSet<string> _paths = new(paths, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string p) => _paths.Contains(p);
        public bool TryOpen(string p, out Stream s) { s = Stream.Null; return _paths.Contains(p); }
        public string? Fingerprint(string p) => _paths.Contains(p) ? "x" : null;
    }

    [Fact]
    public void A_lod_texture_with_a_TexGen_twin_uses_the_twin_and_its_normal_map()
    {
        var pbr = new PbrLodTextures(new Files("textures\\lod\\ash01pbr_lod.dds", "textures\\lod\\ash01pbr_lod_n.dds"));
        Assert.Equal("textures\\lod\\ash01pbr_lod.dds", pbr.MapDiffuse("Textures\\LOD\\ash01lod.dds"));
        Assert.Equal("textures\\lod\\ash01pbr_lod_n.dds", pbr.MapNormal("Textures\\LOD\\ash01lod.dds", "textures\\lod\\ash01lod_n.dds"));
        Assert.Equal(1, pbr.TwinsUsed);
        Assert.Empty(pbr.Conversions);
    }

    [Fact]
    public void A_full_texture_with_a_PBR_version_gets_a_converted_copy_in_our_own_folder()
    {
        var pbr = new PbrLodTextures(new Files("textures\\pbr\\landscape\\mountains\\mountainslab01.dds"));
        var mapped = pbr.MapDiffuse("textures/landscape/mountains/MountainSlab01.dds");
        Assert.Equal("textures\\anvillod\\pbr\\landscape\\mountains\\mountainslab01.dds", mapped);
        var c = Assert.Single(pbr.Conversions);
        Assert.Equal("textures\\pbr\\landscape\\mountains\\mountainslab01.dds", c.Value);
        Assert.Null(pbr.MapNormal("textures\\landscape\\mountains\\mountainslab01.dds", "textures\\landscape\\mountains\\mountainslab01_n.dds"));
    }

    [Theory]
    [InlineData("textures\\landscape\\rocks01.dds")]          // no PBR version
    [InlineData("textures\\pbr\\landscape\\missing.dds")]    // PBR path that exists nowhere
    [InlineData("textures\\pbr\\lod\\alod.dds")]              // stitched LOD texture, already made for the LOD shader
    [InlineData("textures\\anvillod\\pbr\\landscape\\mountains\\mountainslab01.dds")] // our own converted copy
    [InlineData("textures\\anvillod\\grass\\x.dds")]
    [InlineData("textures\\terrain\\tamriel\\trees\\tamrieltreelod.dds")]
    [InlineData("meshes\\foo.nif")]
    [InlineData("")]
    public void Everything_else_is_left_alone(string path)
    {
        var pbr = new PbrLodTextures(new Files("textures\\pbr\\landscape\\mountains\\mountainslab01.dds"));
        Assert.Null(pbr.MapDiffuse(path));
        Assert.Empty(pbr.Conversions);
    }

    [Fact]
    public void A_path_that_already_names_the_PBR_albedo_is_converted_to_the_same_copy_as_its_plain_twin()
    {
        var pbr = new PbrLodTextures(new Files("textures\\pbr\\skyland\\rock01.dds", "textures\\pbr\\lod\\alod.dds"));
        const string expected = "textures\\anvillod\\pbr\\skyland\\rock01.dds";
        Assert.Equal(expected, pbr.MapDiffuse("Textures/PBR/Skyland/Rock01.dds"));
        Assert.Equal(expected, pbr.MapDiffuse("textures\\skyland\\rock01.dds"));   // plain spelling lands on the same copy
        var c = Assert.Single(pbr.Conversions);
        Assert.Equal("textures\\pbr\\skyland\\rock01.dds", c.Value);
        Assert.Null(pbr.MapNormal("textures\\pbr\\skyland\\rock01.dds", "textures\\pbr\\skyland\\rock01_n.dds"));   // its normal map stays
    }

    [Fact]
    public void A_twin_wins_over_a_conversion()
    {
        var pbr = new PbrLodTextures(new Files("textures\\lod\\apbr_lod.dds", "textures\\pbr\\lod\\alod.dds"));
        Assert.Equal("textures\\lod\\apbr_lod.dds", pbr.MapDiffuse("textures\\lod\\alod.dds"));
        Assert.Empty(pbr.Conversions);
    }

    [Fact]
    public void Conversion_darkens_shrinks_and_writes_a_BC7_with_mips()
    {
        const int size = 256;
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 200; rgba[i * 4 + 1] = 150; rgba[i * 4 + 2] = 100; rgba[i * 4 + 3] = 255;
        }
        var blocks = Bc7Encoder.EncodeImage(rgba, size, size);
        using var src = new MemoryStream();
        DdsFile.WriteBc7(src, size, size, [blocks]);

        var dds = PbrLodConverter.Convert(src.ToArray(), maxSize: 128, brightness: 1f);
        var info = DdsFile.ReadInfo(dds);
        Assert.Equal(128, info.Width);
        Assert.Equal(128, info.Height);
        Assert.True(info.MipCount > 4);

        var (outRgba, _, _, _) = DdsFile.DecodeTopMip(dds);
        Assert.InRange(outRgba[0], 100, 130);   // 200 -> about 255 * (200/255)^1.15 * 0.65
        Assert.True(outRgba[0] > outRgba[1] && outRgba[1] > outRgba[2]);
        Assert.InRange(outRgba[3], 250, 255);

        var brighter = PbrLodConverter.Convert(src.ToArray(), maxSize: 128, brightness: 1.5f);
        Assert.True(DdsFile.DecodeTopMip(brighter).Rgba[0] > outRgba[0]);
    }

    [Fact]
    public void A_sparse_leaf_card_keeps_its_alpha_test_coverage_down_the_mip_chain()
    {
        // Two opaque texels in every 4x4 block (12% of the card, alpha varying like a real leaf), like the needle cards of the PBR tree
        // mods: a plain box filter averages that to under the alpha test within a few levels, so a distant crown vanished and left the trunk.
        const int size = 128;
        var rgba = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                rgba[(y * size + x) * 4 + 3] = (byte)(x % 4 is 1 or 2 && y % 4 == 1 ? 140 + (x * 37 + y * 91) % 116 : 0);

        float Share(byte[] img) { int n = 0; for (int i = 3; i < img.Length; i += 4) if (img[i] >= PbrLodConverter.AlphaThreshold) n++; return n / (img.Length / 4f); }

        var chain = PbrLodConverter.MipChain(rgba, size, size);
        float top = Share(chain[0].Rgba);
        Assert.InRange(top, 0.12f, 0.13f);
        foreach (var (img, w, _) in chain.Skip(1).Where(m => m.W >= 16))
            Assert.InRange(Share(img), top * 0.5f, top * 2f);

        var opaque = new byte[size * size * 4];
        for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
        Assert.All(PbrLodConverter.MipChain(opaque, size, size), m => Assert.Equal(1f, Share(m.Rgba)));
    }

    [Fact]
    public void A_missing_pbr_texture_goes_to_its_plain_twin_or_to_a_path_outside_the_pbr_folder()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"textures\landscape\trees\oak.dds", @"textures\pbr\landscape\rock.dds" };
        bool Exists(string p) => present.Contains(p);
        Assert.Equal(@"textures\landscape\trees\oak.dds", LodGenerator.RedirectMissingPbr(@"textures\pbr\landscape\trees\oak.dds", Exists));
        Assert.Equal(@"textures\anvillod\missing\myingredients\mushroom_n.dds", LodGenerator.RedirectMissingPbr(@"textures\pbr\myingredients\mushroom_n.dds", Exists));
        Assert.Equal(@"textures\pbr\landscape\rock.dds", LodGenerator.RedirectMissingPbr(@"textures\pbr\landscape\rock.dds", Exists));   // exists: untouched
        Assert.Equal(@"textures\furniture\x.dds", LodGenerator.RedirectMissingPbr(@"textures\furniture\x.dds", Exists));                   // not a PBR path: untouched
    }
}
