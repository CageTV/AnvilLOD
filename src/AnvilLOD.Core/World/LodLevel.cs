namespace AnvilLOD.Core.World;

/// <summary>
/// Object LOD levels. The value is the block size in cells.
/// The STAT record's MNAM holds four mesh slots (Level0..Level3) that map to these in order.
/// </summary>
public enum LodLevel
{
    Lod4 = 4,
    Lod8 = 8,
    Lod16 = 16,
    Lod32 = 32,
}

public static class LodLevels
{
    public static readonly IReadOnlyList<LodLevel> All = [LodLevel.Lod4, LodLevel.Lod8, LodLevel.Lod16, LodLevel.Lod32];

    /// <summary>MNAM slot index (0..3) for a level.</summary>
    public static int SlotIndex(this LodLevel level) => level switch
    {
        LodLevel.Lod4 => 0,
        LodLevel.Lod8 => 1,
        LodLevel.Lod16 => 2,
        LodLevel.Lod32 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static bool TryParse(int cells, out LodLevel level)
    {
        level = (LodLevel)cells;
        return cells is 4 or 8 or 16 or 32;
    }
}
