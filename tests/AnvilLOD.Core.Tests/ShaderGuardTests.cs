using System.Numerics;
using AnvilLOD.Meshes;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class ShaderGuardTests
{
    private static LodMaterial Material(uint f1, uint f2, params string[] textures)
        => new(textures, f1, f2, 0, false, 0, 0, Vector3.Zero, 1f);

    [Fact]
    public void TextureSlots_AreAlwaysNine()
    {
        Assert.Equal(9, ShaderGuard.TextureSlots(["a.dds"]).Length);
        Assert.Equal(9, ShaderGuard.TextureSlots([]).Length);
        Assert.Equal("a.dds", ShaderGuard.TextureSlots(["a.dds"])[0]);
        Assert.All(ShaderGuard.TextureSlots(["a.dds"]).Skip(1), s => Assert.Equal("", s));
    }

    [Fact]
    public void ShaderFlags_ArePassedThrough_LikeDynDolodsTreeLod()
    {
        // DynDOLOD's tree LOD sets Parallax (1<<11) and Facegen_Detail_Map (1<<10) with empty slots and loads fine in the game.
        var m = Material(0x8000_0F29u, 0x0020_4045u, "d.dds", "n.dds");
        var (f1, f2) = ShaderGuard.Safe(m, colors: false);
        Assert.Equal(0x8000_0F29u, f1);
        Assert.Equal(0x0020_4045u, f2);
    }

    [Fact]
    public void VertexColors_FollowTheShape()
    {
        Assert.NotEqual(0u, ShaderGuard.Safe(Material(0, 0, "d.dds"), colors: true).Flags2 & (1u << 5));
        Assert.Equal(0u, ShaderGuard.Safe(Material(0, 0, "d.dds"), colors: false).Flags2 & (1u << 5));
    }

    private static byte[] Dds(string fourCc, uint width = 256, uint height = 256, uint dxgi = 0, uint arraySize = 1, uint bits = 0)
    {
        var b = new byte[148];
        "DDS "u8.CopyTo(b);
        BitConverter.GetBytes(124u).CopyTo(b, 4);
        BitConverter.GetBytes(height).CopyTo(b, 12);
        BitConverter.GetBytes(width).CopyTo(b, 16);
        if (fourCc.Length == 4)
        {
            BitConverter.GetBytes(0x4u).CopyTo(b, 80);
            System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(b, 84);
        }
        else BitConverter.GetBytes(bits).CopyTo(b, 88);
        BitConverter.GetBytes(dxgi).CopyTo(b, 128);
        BitConverter.GetBytes(arraySize).CopyTo(b, 140);
        return b;
    }

    [Fact]
    public void Dds_ValidFormatsPass()
    {
        Assert.Null(DdsHeader.Problem(Dds("DXT5"), 100000));
        Assert.Null(DdsHeader.Problem(Dds("DX10", dxgi: 98), 100000));     // BC7
        Assert.Null(DdsHeader.Problem(Dds("", bits: 32), 100000));        // uncompressed RGBA
    }

    [Fact]
    public void Dds_BrokenFilesAreReported()
    {
        Assert.NotNull(DdsHeader.Problem(new byte[20], 20));
        Assert.NotNull(DdsHeader.Problem(Dds("DXT5", width: 0), 100000));
        Assert.NotNull(DdsHeader.Problem(Dds("ABCD"), 100000));
        Assert.NotNull(DdsHeader.Problem(Dds("DX10", dxgi: 2), 100000));
        Assert.NotNull(DdsHeader.Problem(Dds("DX10", dxgi: 98, arraySize: 6), 100000));
        Assert.NotNull(DdsHeader.Problem(Dds("", bits: 12), 100000));
        var bad = Dds("DXT5"); bad[0] = (byte)'X';
        Assert.NotNull(DdsHeader.Problem(bad, 100000));
    }

    [Fact]
    public void StubTexture_IsAValidDds_AndTheColourFollowsTheName()
    {
        var dds = AnvilLOD.Plugins.StubTextures.Build(70, 98, 110);
        Assert.Null(DdsHeader.Problem(dds, dds.Length));
        Assert.Equal(128 + 64, dds.Length);
        Assert.Equal((byte)110, dds[128]);   // B of the first pixel
        Assert.Equal(AnvilLOD.Plugins.StubTextures.ColorFor(@"textures\lod\creeks\mxcreek_blue.dds"), AnvilLOD.Plugins.StubTextures.ColorFor(@"textures\x\water01.dds"));
        Assert.NotEqual(AnvilLOD.Plugins.StubTextures.ColorFor(@"textures\x\snowrock.dds"), AnvilLOD.Plugins.StubTextures.ColorFor(@"textures\x\dirt.dds"));
    }

    [Fact]
    public void StubTexture_IsWrittenAtThePath_AndANormalMapIsFlat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil_stub_" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.NotNull(AnvilLOD.Plugins.StubTextures.Write(dir, @"textures\lod\creeks\mxcreek_blue.dds"));
            Assert.Null(AnvilLOD.Plugins.StubTextures.Write(dir, @"textures\lod\creeks\mxcreek_blue_n.dds"));
            var n = File.ReadAllBytes(Path.Combine(dir, "textures", "lod", "creeks", "mxcreek_blue_n.dds"));
            Assert.Equal(new byte[] { 255, 128, 128, 255 }, n.AsSpan(128, 4).ToArray());   // BGRA of (128,128,255)
            Assert.True(File.Exists(Path.Combine(dir, "textures", "lod", "creeks", "mxcreek_blue.dds")));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
