using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Textures.Bc;

namespace AnvilLOD.Core.Tests;

public class TreeTests
{
    [Theory]
    [InlineData(0.899f, 5.384f)]   // values seen in DynDOLOD output
    [InlineData(0f, 6.2832f)]
    [InlineData(5.283f, 1.0002f)]
    [InlineData(-0.5f, 0.5f)]
    public void Rotation_is_stored_as_two_pi_minus_z(float rz, float expected)
        => Assert.Equal(expected, TreeLodFiles.EngineRotation(rz), 3);

    [Fact]
    public void List_entries_are_32_bytes()
    {
        var bytes = TreeLodFiles.WriteList([new(0, 452.7f, 616.5f, 0.8f, 0.75f, 0.85f, 0.8125f), new(1, 1, 1, 0, 0, 1, 1)]);
        Assert.Equal(4 + 2 * 32, bytes.Length);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(452.7f, BitConverter.ToSingle(bytes, 8));
    }

    [Fact]
    public void Block_groups_instances_by_type()
    {
        var bytes = TreeLodFiles.WriteBlock([
            new(3, new Vector3(1, 2, 3), 0, 1, 0x2E014C82),
            new(1, new Vector3(4, 5, 6), 0, 0.7f, 0x000C5E24),
            new(3, new Vector3(7, 8, 9), 0, 1, 0x000C5E23),
        ]);
        Assert.Equal(4 + 2 * 8 + 3 * 32, bytes.Length);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 0));   // types
        Assert.Equal(1, BitConverter.ToInt32(bytes, 4));   // first type index
        Assert.Equal(1, BitConverter.ToInt32(bytes, 8));   // its count
        Assert.Equal(0x000C5E24u, BitConverter.ToUInt32(bytes, 12 + 20));
    }

    [Fact]
    public void Billboard_txt_parses_width_height_and_shift()
    {
        var bb = TreeBillboard.Parse("x.dds", "[LOD]\r\nWidth=452.718445\r\nHeight=616.467651\r\nShiftZ=-258.783051\r\nScale=1.000000\r\nWidth_1=407.5\r\nHeight_1=600\r\n");
        Assert.NotNull(bb);
        Assert.Equal(452.718445f, bb!.Width, 3);
        Assert.Equal(-258.783051f, bb.ShiftZ, 3);
        Assert.Equal(407.5f, TreeBillboard.Parse("x.dds", "Width=1\nHeight=1\nWidth_1=407.5\nHeight_1=600", useFirstView: true)!.Width);
    }

    [Fact]
    public void Bc7_roundtrip_is_close()
    {
        var rgba = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            rgba[i * 4] = (byte)(i * 16); rgba[i * 4 + 1] = (byte)(255 - i * 10); rgba[i * 4 + 2] = 40; rgba[i * 4 + 3] = (byte)(i < 8 ? 0 : 255);
        }
        var block = new byte[16];
        Bc7Encoder.EncodeBlock(rgba, block);
        var back = new byte[64];
        Bc7Decoder.DecodeBlock(block, back);
        for (int i = 0; i < 64; i++)
        {
            bool hiddenColour = (i & 3) != 3 && rgba[(i & ~3) + 3] == 0; // colour under alpha 0 doesn't matter
            if (!hiddenColour) Assert.InRange(Math.Abs(rgba[i] - back[i]), 0, 24);
        }
    }

    [Fact]
    public void Bc7_solid_block_is_exact()
    {
        var rgba = new byte[64];
        for (int i = 0; i < 16; i++) { rgba[i * 4] = 10; rgba[i * 4 + 1] = 200; rgba[i * 4 + 2] = 77; rgba[i * 4 + 3] = 255; }
        var block = new byte[16];
        Bc7Encoder.EncodeBlock(rgba, block);
        var back = new byte[64];
        Bc7Decoder.DecodeBlock(block, back);
        for (int i = 0; i < 64; i++) Assert.InRange(Math.Abs(rgba[i] - back[i]), 0, 1);
    }
}
