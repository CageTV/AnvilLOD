using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Core.Tests;

public class OpaqueLodTests
{
    /// <summary>A 8x8 DXT5 file (four blocks, no mips) whose alpha endpoints are the given values.</summary>
    private static byte[] Dxt5(byte a0, byte a1)
    {
        var h = new byte[128];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(h, 0);
        BitConverter.GetBytes(124).CopyTo(h, 4);
        BitConverter.GetBytes(8).CopyTo(h, 12);   // height
        BitConverter.GetBytes(8).CopyTo(h, 16);   // width
        BitConverter.GetBytes(1).CopyTo(h, 28);   // mips
        BitConverter.GetBytes(32).CopyTo(h, 76);  // pixel format size
        BitConverter.GetBytes(0x4).CopyTo(h, 80); // FOURCC
        Encoding.ASCII.GetBytes("DXT5").CopyTo(h, 84);
        var blocks = new byte[4 * 16];
        for (int b = 0; b < 4; b++) { blocks[b * 16] = a0; blocks[b * 16 + 1] = a1; blocks[b * 16 + 8] = 0xFF; blocks[b * 16 + 9] = 0xFF; }
        return [.. h, .. blocks];
    }

    private sealed class Files(params string[] paths) : IAssetSource
    {
        private readonly HashSet<string> _paths = new(paths, StringComparer.OrdinalIgnoreCase);
        public bool Exists(string p) => _paths.Contains(p);
        public bool TryOpen(string p, out Stream s) { s = new MemoryStream(Dxt5(40, 200)); return _paths.Contains(p); }
        public string? Fingerprint(string p) => _paths.Contains(p) ? "x" : null;
    }

    [Fact]
    public void A_dxt5_texture_with_a_gloss_style_alpha_is_translucent_and_the_copy_is_not()
    {
        var dds = Dxt5(40, 200);
        Assert.True(DdsAlpha.IsTranslucent(dds));
        var copy = DdsAlpha.MakeOpaque(dds, 1024);
        Assert.False(DdsAlpha.IsTranslucent(copy));
        Assert.Equal(dds.Length, copy.Length);
        var (rgba, _, _, _) = DdsFile.DecodeTopMip(copy);
        for (int i = 3; i < rgba.Length; i += 4) Assert.Equal(255, rgba[i]);
        // colours are untouched
        var (before, _, _, _) = DdsFile.DecodeTopMip(dds);
        for (int i = 0; i < rgba.Length; i += 4) { Assert.Equal(before[i], rgba[i]); Assert.Equal(before[i + 1], rgba[i + 1]); Assert.Equal(before[i + 2], rgba[i + 2]); }
    }

    [Fact]
    public void A_dxt5_texture_that_is_opaque_already_is_left_alone()
    {
        Assert.False(DdsAlpha.IsTranslucent(Dxt5(255, 255)));
    }

    [Fact]
    public void A_translucent_texture_maps_to_a_copy_in_our_own_folder()
    {
        var opaque = new OpaqueLodTextures(new Files("textures\\dungeons\\ships\\shipwoodside01.dds"), DdsAlpha.IsTranslucent);
        Assert.Equal("textures\\anvillod\\opaque\\dungeons\\ships\\shipwoodside01.dds", opaque.MapDiffuse("Textures/Dungeons/Ships/ShipWoodSide01.dds"));
        var c = Assert.Single(opaque.Conversions);
        Assert.Equal("textures\\dungeons\\ships\\shipwoodside01.dds", c.Value);
    }

    [Theory]
    [InlineData("textures\\anvillod\\pbr\\x.dds")]
    [InlineData("textures\\terrain\\tamriel\\trees\\tamrieltreelod.dds")]
    [InlineData("textures\\lod\\ash01lod.dds")]
    [InlineData("meshes\\foo.nif")]
    [InlineData("")]
    public void Atlases_our_own_files_and_non_textures_are_left_alone(string path)
    {
        var opaque = new OpaqueLodTextures(new Files(path), DdsAlpha.IsTranslucent);
        Assert.Null(opaque.MapDiffuse(path));
        Assert.Empty(opaque.Conversions);
    }
}
