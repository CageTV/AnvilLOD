namespace AnvilLOD.Core.Occlusion;

/// <summary>
/// The TVDT field of an exterior CELL (684 bytes), decoded from xLODGen's output (verified bit for bit against its own debug images):
/// 6 lead bits, then a full quadtree of 5,461 bits stored in post-order (the children of a node, then the node), then 5 zero bits.
/// The 64 x 64 leaves are the LOD4 blocks (4 x 4 cells) of the worldspace, counted from the south-west cell of its .lod file; a set bit means
/// the block can be seen from this cell. Children come in Z order with the Y bit lowest: child = (xBit &lt;&lt; 1) | yBit.
/// Bits are numbered from the least significant bit of byte 0. Level 0 = the 64 x 64 leaves (4 cells), 1 = 32 x 32 (8 cells), ... 6 = the root.
/// </summary>
public sealed class TvdtData
{
    public const int ByteCount = 684;
    public const int LeadBits = 6;
    public const int LeavesPerSide = 64;
    public const int TopLevel = 6;

    private static readonly int[] SubtreeSize = [1, 5, 21, 85, 341, 1365, 5461];   // by height: (4^(h+1) - 1) / 3

    private readonly byte[] _bytes;

    public TvdtData() => _bytes = new byte[ByteCount];

    public TvdtData(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw new ArgumentException($"A TVDT is {ByteCount} bytes, got {bytes.Length}.", nameof(bytes));
        _bytes = bytes.ToArray();
    }

    public byte[] ToBytes() => (byte[])_bytes.Clone();

    public bool GetBit(int index) => (_bytes[index >> 3] >> (index & 7) & 1) != 0;

    public void SetBit(int index, bool value)
    {
        if (value) _bytes[index >> 3] |= (byte)(1 << (index & 7));
        else _bytes[index >> 3] &= (byte)~(1 << (index & 7));
    }

    /// <summary>Bit number of the node at <paramref name="level"/> (0 = leaf) and grid position (x, y) at that level.</summary>
    public static int NodeBit(int level, int x, int y)
    {
        if (level is < 0 or > TopLevel) throw new ArgumentOutOfRangeException(nameof(level));
        int side = LeavesPerSide >> level;
        if ((uint)x >= (uint)side || (uint)y >= (uint)side) throw new ArgumentOutOfRangeException($"({x},{y}) is outside the {side} x {side} grid of level {level}.");
        int bx = x << level, by = y << level, position = 0;
        for (int h = TopLevel; h > level; h--)
        {
            int k = h - 1;
            int child = (((bx >> k) & 1) << 1) | ((by >> k) & 1);
            position += child * SubtreeSize[h - 1];
        }
        return LeadBits + position + SubtreeSize[level] - 1;
    }

    public static int LeafBit(int blockX, int blockY) => NodeBit(0, blockX, blockY);

    public bool GetNode(int level, int x, int y) => GetBit(NodeBit(level, x, y));

    public void SetNode(int level, int x, int y, bool value) => SetBit(NodeBit(level, x, y), value);

    public bool GetLeaf(int blockX, int blockY) => GetBit(LeafBit(blockX, blockY));

    public void SetLeaf(int blockX, int blockY, bool value) => SetBit(LeafBit(blockX, blockY), value);

    /// <summary>The 6 lead bits as a number (bits 0 and 1 are set in every cell seen; 2 and 3 vary: their meaning is not known yet).</summary>
    public int Lead
    {
        get
        {
            int v = 0;
            for (int i = 0; i < LeadBits; i++) if (GetBit(i)) v |= 1 << i;
            return v;
        }
        set
        {
            for (int i = 0; i < LeadBits; i++) SetBit(i, (value >> i & 1) != 0);
        }
    }

    /// <summary>Number of visible LOD4 blocks.</summary>
    public int VisibleLeaves()
    {
        int n = 0;
        for (int y = 0; y < LeavesPerSide; y++) for (int x = 0; x < LeavesPerSide; x++) if (GetLeaf(x, y)) n++;
        return n;
    }
}
