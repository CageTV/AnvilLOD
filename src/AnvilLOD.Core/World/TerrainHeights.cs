using System.Collections.Concurrent;

namespace AnvilLOD.Core.World;

/// <summary>
/// Full-detail terrain heights per exterior cell, decoded from LAND/VHGT (33×33 vertices, 128 units apart).
/// Used to drop LOD triangles that are buried under the ground.
/// </summary>
public sealed class TerrainHeights
{
    public const int Grid = 33;
    public const float Spacing = CellCoord.CellSize / (Grid - 1); // 128

    private readonly ConcurrentDictionary<(string Ws, int X, int Y), float[]> _cells = new();

    public int CellCount => _cells.Count;

    /// <summary>The cells of one worldspace that have terrain.</summary>
    public IEnumerable<CellCoord> CellsOf(string worldspace)
    {
        var ws = worldspace.ToLowerInvariant();
        return _cells.Keys.Where(k => k.Ws == ws).Select(k => new CellCoord(k.X, k.Y));
    }

    /// <summary>
    /// Decodes VHGT: a float offset followed by 33×33 signed deltas. The first value of each row is relative
    /// to the first value of the previous row; the others are relative to their left neighbour. Units of 8.
    /// </summary>
    public static float[] Decode(float offset, Func<int, int, sbyte> delta)
    {
        var h = new float[Grid * Grid];
        float row = offset;
        for (int y = 0; y < Grid; y++)
        {
            row += delta(0, y);
            float col = row;
            h[y * Grid] = col * 8f;
            for (int x = 1; x < Grid; x++)
            {
                col += delta(x, y);
                h[y * Grid + x] = col * 8f;
            }
        }
        return h;
    }

    public void Set(string worldspace, int cellX, int cellY, float[] heights) =>
        _cells[(worldspace.ToLowerInvariant(), cellX, cellY)] = heights;

    private readonly ConcurrentDictionary<(string Ws, int X, int Y), float> _water = new();

    public int WaterCellCount => _water.Count;

    /// <summary>The water surface height of an exterior cell that has water (its own XCLW, else the worldspace's default).</summary>
    public void SetWater(string worldspace, int cellX, int cellY, float height) =>
        _water[(worldspace.ToLowerInvariant(), cellX, cellY)] = height;

    /// <summary>Water surface height at a world position, or null where the cell has no water or is unknown.</summary>
    public float? WaterAt(string worldspace, float x, float y)
    {
        var cell = CellCoord.FromWorld(x, y);
        return _water.TryGetValue((worldspace.ToLowerInvariant(), cell.X, cell.Y), out var h) ? h : null;
    }

    /// <summary>A stable digest of every water height of one worldspace, for the block hash.</summary>
    public string WaterFingerprint(string worldspace)
    {
        var ws = worldspace.ToLowerInvariant();
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buf = new byte[12];
        foreach (var kv in _water.Where(k => k.Key.Ws == ws).OrderBy(k => k.Key.X).ThenBy(k => k.Key.Y))
        {
            BitConverter.TryWriteBytes(buf.AsSpan(0, 4), kv.Key.X);
            BitConverter.TryWriteBytes(buf.AsSpan(4, 4), kv.Key.Y);
            BitConverter.TryWriteBytes(buf.AsSpan(8, 4), kv.Value);
            hash.AppendData(buf);
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 8);
    }

    /// <summary>
    /// Height of one LAND vertex by global vertex index (cell * 32 + local; one step = 128 units), or null where the
    /// cell has no terrain. A cell's last row/column is the next cell's first, so only 32 of the 33 are read.
    /// </summary>
    public float? VertexAt(string worldspace, int gx, int gy)
    {
        int cx = LodGrid.FloorDiv(gx, Grid - 1), cy = LodGrid.FloorDiv(gy, Grid - 1);
        if (!_cells.TryGetValue((worldspace.ToLowerInvariant(), cx, cy), out var h)) return null;
        return h[(gy - cy * (Grid - 1)) * Grid + (gx - cx * (Grid - 1))];
    }

    /// <summary>Terrain height at a world position (bilinear between the 4 surrounding vertices), or null if unknown.</summary>
    public float? HeightAt(string worldspace, float x, float y)
    {
        var cell = CellCoord.FromWorld(x, y);
        if (!_cells.TryGetValue((worldspace.ToLowerInvariant(), cell.X, cell.Y), out var h)) return null;
        float lx = (x - cell.X * CellCoord.CellSize) / Spacing;
        float ly = (y - cell.Y * CellCoord.CellSize) / Spacing;
        int ix = Math.Clamp((int)lx, 0, Grid - 2);
        int iy = Math.Clamp((int)ly, 0, Grid - 2);
        float fx = Math.Clamp(lx - ix, 0, 1), fy = Math.Clamp(ly - iy, 0, 1);
        float h00 = h[iy * Grid + ix], h10 = h[iy * Grid + ix + 1];
        float h01 = h[(iy + 1) * Grid + ix], h11 = h[(iy + 1) * Grid + ix + 1];
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    /// <summary>
    /// How far below the terrain a triangle must be before it is dropped. Farther LOD levels sit on coarser
    /// terrain LOD, so they need a bigger safety margin.
    /// </summary>
    public static float MarginFor(LodLevel level) => level switch
    {
        LodLevel.Lod4 => 64f,
        LodLevel.Lod8 => 128f,
        LodLevel.Lod16 => 256f,
        _ => 512f,
    };
}
