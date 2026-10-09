using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
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
    [InlineData("textures\\pbr\\landscape\\mountains\\mountainslab01.dds")] // already PBR
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
}
