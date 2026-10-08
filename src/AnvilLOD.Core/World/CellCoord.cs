namespace AnvilLOD.Core.World;

/// <summary>Exterior cell coordinate (one cell = 4096 world units).</summary>
public readonly record struct CellCoord(int X, int Y)
{
    public const float CellSize = 4096f;

    /// <summary>
    /// Cell containing a world position. Uses floor division so negative coordinates
    /// land in the correct cell (e.g. x = -1 is cell -1, not cell 0).
    /// </summary>
    public static CellCoord FromWorld(float x, float y) =>
        new((int)MathF.Floor(x / CellSize), (int)MathF.Floor(y / CellSize));

    public override string ToString() => $"{X},{Y}";
}
