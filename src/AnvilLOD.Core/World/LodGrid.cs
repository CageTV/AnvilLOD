namespace AnvilLOD.Core.World;

/// <summary>
/// Identifies one object-LOD block file: <c>meshes\terrain\&lt;ws&gt;\objects\&lt;ws&gt;.&lt;level&gt;.&lt;x&gt;.&lt;y&gt;.bto</c>.
/// X/Y are the south-west cell of the block.
/// </summary>
public readonly record struct QuadKey(string Worldspace, LodLevel Level, int X, int Y)
{
    public string FileName => $"{Worldspace}.{(int)Level}.{X}.{Y}.bto";

    public string RelativePath =>
        GamePath.Join("meshes", "terrain", Worldspace, "objects", FileName);

    public override string ToString() => FileName;
}

/// <summary>Maps cells to LOD blocks for one worldspace, using its .lod settings.</summary>
public sealed class LodGrid
{
    public string Worldspace { get; }
    public LodSettings Settings { get; }

    public LodGrid(string worldspace, LodSettings settings)
    {
        Worldspace = worldspace;
        Settings = settings;
    }

    /// <summary>
    /// South-west cell of the block at <paramref name="level"/> that contains <paramref name="cell"/>.
    /// Blocks are aligned to multiples of the level size, measured from the grid's SW origin.
    /// NOTE: verify against CK/xLODGen output for a non-zero-origin worldspace early on.
    /// </summary>
    public CellCoord BlockOrigin(CellCoord cell, LodLevel level)
    {
        int size = (int)level;
        var sw = Settings.SouthWest;
        return new CellCoord(
            sw.X + FloorDiv(cell.X - sw.X, size) * size,
            sw.Y + FloorDiv(cell.Y - sw.Y, size) * size);
    }

    public QuadKey QuadFor(CellCoord cell, LodLevel level)
    {
        var o = BlockOrigin(cell, level);
        return new QuadKey(Worldspace, level, o.X, o.Y);
    }

    public bool IsInsideGrid(CellCoord cell)
    {
        var sw = Settings.SouthWest;
        return cell.X >= sw.X && cell.Y >= sw.Y
            && cell.X < sw.X + Settings.Stride && cell.Y < sw.Y + Settings.Stride;
    }

    internal static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) ^ (b < 0))) q--;
        return q;
    }
}
