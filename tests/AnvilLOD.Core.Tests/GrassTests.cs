using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class GrassTests
{
    [Theory]
    [InlineData("Tamrielx0005y-005.cgid", "Tamriel", 5, -5, null)]
    [InlineData("000MilandrielDungeonx-001y-002.cgid", "000MilandrielDungeon", -1, -2, null)]
    [InlineData("Tamrielx0034y0009.WIN.cgid", "Tamriel", 34, 9, "WIN")]
    public void Cache_file_names_parse(string name, string ws, int x, int y, string? season)
    {
        Assert.True(GrassCache.TryParseFileName(name, out var w, out var cx, out var cy, out var s));
        Assert.Equal(ws, w); Assert.Equal(x, cx); Assert.Equal(y, cy); Assert.Equal(season, s);
    }

    [Fact]
    public void Cache_with_no_types_reads_empty()
        => Assert.Equal(0, GrassCache.Read(new byte[4]).Count);
}
