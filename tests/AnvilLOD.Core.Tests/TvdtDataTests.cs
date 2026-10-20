using AnvilLOD.Core.Occlusion;

namespace AnvilLOD.Core.Tests;

public sealed class TvdtDataTests
{
    // The TVDT xLODGen wrote for Tamriel cell (0, 0) of a single-chunk run; its debug image for the same cell (visible LOD4 blocks) matches the
    // decoded leaves pixel for pixel (185 visible, the cell's own block among them).
    private const string Golden =
        "030000000000000000000000000000000000000000000000000000000000000000000000000000000000800100000000000000018809000000" +
        "00000000000000040000cb063f0020beffaf01d83b0009fc3ff61f2c100000000000000000000004000041051f000000000000000000000000" +
        "0000001000000415000000000000befbf9fffe3f181bbcbffeffff030000fceb6f0000f09fffedff7f0000101c00fe63c70024f057d02e3000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000008" +
        "00000000000000000000000000000000000000000000000000000000000000000000000000000000000006000001c81500002c070000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Nodes_sit_where_the_post_order_quadtree_puts_them()
    {
        Assert.Equal(6, TvdtData.LeafBit(0, 0));
        Assert.Equal(7, TvdtData.LeafBit(0, 1));    // Z order, Y is the low bit
        Assert.Equal(8, TvdtData.LeafBit(1, 0));
        Assert.Equal(10, TvdtData.NodeBit(1, 0, 0)); // after its four children (6..9)... the first L8 node is bit 10
        Assert.Equal(1284, TvdtData.LeafBit(24, 24));
        Assert.Equal(1288, TvdtData.NodeBit(1, 12, 12));
        Assert.Equal(5460, TvdtData.LeafBit(63, 63));
        Assert.Equal(5466, TvdtData.NodeBit(6, 0, 0)); // the root comes last
    }

    [Fact]
    public void Every_node_has_its_own_bit_and_children_come_before_their_parent()
    {
        var seen = new HashSet<int>();
        for (int level = 0; level <= TvdtData.TopLevel; level++)
        {
            int side = TvdtData.LeavesPerSide >> level;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int bit = TvdtData.NodeBit(level, x, y);
                    Assert.True(seen.Add(bit), $"bit {bit} used twice");
                    Assert.InRange(bit, 6, 5466);
                    if (level > 0)
                        foreach (var (dx, dy) in new[] { (0, 0), (0, 1), (1, 0), (1, 1) })
                            Assert.True(TvdtData.NodeBit(level - 1, 2 * x + dx, 2 * y + dy) < bit);
                }
        }
        Assert.Equal(5461, seen.Count);
    }

    [Fact]
    public void A_real_cell_decodes_and_encodes_unchanged()
    {
        var bytes = Convert.FromHexString(Golden);
        var tvdt = new TvdtData(bytes);
        Assert.Equal(185, tvdt.VisibleLeaves());
        Assert.True(tvdt.GetLeaf(24, 24));       // the observer's own block: cell (0, 0) + 96 = 96 / 4
        Assert.Equal(3, tvdt.Lead);
        Assert.Equal(bytes, tvdt.ToBytes());

        var rebuilt = new TvdtData();
        rebuilt.Lead = tvdt.Lead;
        for (int level = 0; level <= TvdtData.TopLevel; level++)
        {
            int side = TvdtData.LeavesPerSide >> level;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                    rebuilt.SetNode(level, x, y, tvdt.GetNode(level, x, y));
        }
        Assert.Equal(bytes, rebuilt.ToBytes());
    }

    [Fact]
    public void Wrong_sizes_and_positions_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new TvdtData(new byte[683]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TvdtData.LeafBit(64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TvdtData.NodeBit(7, 0, 0));
    }
}
