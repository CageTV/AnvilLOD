using System.Buffers.Binary;
using System.Text;
using AnvilLOD.Core.Textures;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class TextureFixerTests
{
    private static byte[] Dx10Header(int dxgi, int width = 256, int height = 128, int mips = 9)
    {
        var h = new byte[148];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x20534444);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(16), width);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), mips);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 0x4);
        Encoding.ASCII.GetBytes("DX10").CopyTo(h, 84);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(128), dxgi);
        return h;
    }

    [Theory]
    [InlineData(98)]    // BC7
    [InlineData(99)]    // BC7 sRGB
    [InlineData(71)]    // BC1
    [InlineData(77)]    // BC3
    [InlineData(83)]    // BC5
    [InlineData(28)]    // R8G8B8A8
    public void LoadableFormatsAreNotReported(int dxgi) =>
        Assert.Null(TextureFixer.Inspect("root", "textures\\a.dds", "mod", Dx10Header(dxgi)));

    [Theory]
    [InlineData(95)]
    [InlineData(96)]
    public void Bc6hIsReportedAndFixable(int dxgi)
    {
        var bad = TextureFixer.Inspect("root", "textures\\a.dds", "mod", Dx10Header(dxgi, 1024, 512, 11));
        Assert.NotNull(bad);
        Assert.True(bad!.Fixable);
        Assert.Equal((1024, 512, 11), (bad.Width, bad.Height, bad.Mips));
        Assert.Contains("BC6H", bad.Format);
    }

    [Fact]
    public void OtherUnknownFormatsAreReportedButNotConverted()
    {
        var bad = TextureFixer.Inspect("root", "a.dds", "mod", Dx10Header(61 + 100));
        Assert.NotNull(bad);
        Assert.False(bad!.Fixable);
    }

    [Fact]
    public void LegacyDxtHeaderIsFine()
    {
        var h = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), 0x4);
        Encoding.ASCII.GetBytes("DXT5").CopyTo(h, 84);
        Assert.Null(TextureFixer.Inspect("root", "a.dds", "mod", h));
    }

    [Fact]
    public void NotADdsFileIsReported()
    {
        var bad = TextureFixer.Inspect("root", "a.dds", "mod", new byte[200]);
        Assert.NotNull(bad);
        Assert.False(bad!.Fixable);
    }

    [Fact]
    public void ScanFindsOnlyTheBadFilesAndNamesTheMod()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil-texfix-" + Guid.NewGuid().ToString("N"), "My Mod");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "textures", "x"));
            File.WriteAllBytes(Path.Combine(dir, "textures", "x", "good.dds"), Dx10Header(98));
            File.WriteAllBytes(Path.Combine(dir, "textures", "x", "bad.dds"), Dx10Header(95));
            var found = TextureFixer.Scan([dir]);
            var one = Assert.Single(found);
            Assert.Equal("My Mod", one.Mod);
            Assert.Equal(Path.Combine("textures", "x", "bad.dds"), one.Relative);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch (IOException) { } }
    }

    [Fact]
    public void DryRunWritesNothingAndInPlaceNeedsABackupFolder()
    {
        var item = new BadTexture("root", "textures\\a.dds", "mod", 95, "BC6H", 4, 4, 1, true);
        var r = Assert.Single(TextureFixer.Fix([item], "nonexistent-texconv.exe", null, "backup", dryRun: true));
        Assert.Equal("Would convert", r.Status);
        Assert.Throws<ArgumentException>(() => TextureFixer.Fix([item], "x.exe", null, null, dryRun: false));
    }
}
