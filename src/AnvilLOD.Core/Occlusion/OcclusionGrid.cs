namespace AnvilLOD.Core.Occlusion;

/// <summary>
/// Heights of a worldspace sampled every 128 units (32 x 32 samples per cell), the resolution xLODGen's occlusion works at. Two layers:
/// <see cref="Terrain"/> is the bare landscape and is what blocks the view; <see cref="Top"/> is the highest thing in the sample (landscape or
/// object LOD, trees included) and is what is looked from and looked at. Samples outside the known area hold <see cref="Unknown"/>.
/// </summary>
public sealed class OcclusionGrid
{
    public const int SamplesPerCell = 32;
    public const float SampleSize = 128f;
    public const float Unknown = -19000f;

    public int CellX0 { get; }
    public int CellY0 { get; }
    public int CellsX { get; }
    public int CellsY { get; }
    public int Width => CellsX * SamplesPerCell;
    public int Height => CellsY * SamplesPerCell;

    /// <summary>Row = sample Y counted from the south edge, column = sample X from the west edge.</summary>
    public float[] Terrain { get; }
    public float[] Top { get; }

    /// <summary>The height of everything the grid has no data for (open sea beyond the landscape): flat, at the worldspace's default water level.</summary>
    public float DefaultHeight { get; }

    public OcclusionGrid(int cellX0, int cellY0, int cellsX, int cellsY, float defaultHeight = -14000f)
    {
        DefaultHeight = defaultHeight;
        CellX0 = cellX0; CellY0 = cellY0; CellsX = cellsX; CellsY = cellsY;
        Terrain = new float[Width * Height];
        Top = new float[Width * Height];
        Array.Fill(Terrain, Unknown);
        Array.Fill(Top, Unknown);
    }

    public bool Contains(int cellX, int cellY) => cellX >= CellX0 && cellY >= CellY0 && cellX < CellX0 + CellsX && cellY < CellY0 + CellsY;

    /// <summary>True when the cell's samples have been filled in.</summary>
    public bool HasData(int cellX, int cellY) =>
        Contains(cellX, cellY) && Terrain[((cellY - CellY0) * SamplesPerCell + SamplesPerCell / 2) * Width + (cellX - CellX0) * SamplesPerCell + SamplesPerCell / 2] > Unknown;

    /// <summary>Fills a cell from its 33 x 33 terrain vertices (row-major, row = Y): each sample is the highest of the four vertices around it.</summary>
    public void SetTerrain(int cellX, int cellY, ReadOnlySpan<float> vertices33)
    {
        if (vertices33.Length != 33 * 33) throw new ArgumentException("A cell has 33 x 33 height vertices.", nameof(vertices33));
        if (!Contains(cellX, cellY)) return;
        int gx = (cellX - CellX0) * SamplesPerCell, gy = (cellY - CellY0) * SamplesPerCell;
        for (int j = 0; j < SamplesPerCell; j++)
            for (int i = 0; i < SamplesPerCell; i++)
            {
                float a = vertices33[j * 33 + i], b = vertices33[j * 33 + i + 1], c = vertices33[(j + 1) * 33 + i], d = vertices33[(j + 1) * 33 + i + 1];
                float h = MathF.Max(MathF.Max(a, b), MathF.Max(c, d));
                int at = (gy + j) * Width + gx + i;
                Terrain[at] = h;
                if (Top[at] < h) Top[at] = h;
            }
    }

    /// <summary>The terrain height at a sample (grid coordinates), the default height where there is no data.</summary>
    public float TerrainAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return DefaultHeight;
        float h = Terrain[y * Width + x];
        return h > Unknown ? h : DefaultHeight;
    }

    public float TopAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return DefaultHeight;
        float h = Top[y * Width + x];
        return h > Unknown ? h : DefaultHeight;
    }

    /// <summary>Raises <see cref="Top"/> to at least <paramref name="height"/> over a world-space rectangle (an object's footprint).</summary>
    public void RaiseTop(float minX, float minY, float maxX, float maxY, float height)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(minX / SampleSize) - CellX0 * SamplesPerCell);
        int x1 = Math.Min(Width - 1, (int)MathF.Floor(maxX / SampleSize) - CellX0 * SamplesPerCell);
        int y0 = Math.Max(0, (int)MathF.Floor(minY / SampleSize) - CellY0 * SamplesPerCell);
        int y1 = Math.Min(Height - 1, (int)MathF.Floor(maxY / SampleSize) - CellY0 * SamplesPerCell);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int at = y * Width + x;
                if (Terrain[at] > Unknown && Top[at] < height) Top[at] = height;
            }
    }
}
