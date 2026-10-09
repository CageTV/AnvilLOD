using System.Numerics;

namespace AnvilLOD.Core.World;

/// <summary>
/// Builds the "terrain underside" for one LOD32 block: a low-resolution copy of the terrain whose faces point
/// downward. Volumetric lighting mods (DVLaSS, EVLaS, Community Shaders' sky sync) use it to stop sun rays and shadows
/// leaking through the landscape; DynDOLOD makes the same mesh (<c>Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c>) and
/// places it 500 units below the world.
/// <para>
/// Every vertex is the lowest LAND height within one block step of it, so the mesh can never rise above the terrain
/// between vertices (it only ever sits at or below it) and can't poke through. Neighbouring blocks read the same LAND
/// vertices at their shared edge, so the seams close without any stitching.
/// </para>
/// </summary>
public static class UndersideMesher
{
    /// <summary>Cells per LOD32 block.</summary>
    public const int BlockCells = 32;

    /// <summary>LAND vertices per block side (a cell is 32 vertices, 128 units apart).</summary>
    public const int BlockVertices = BlockCells * (TerrainHeights.Grid - 1);

    /// <summary>LAND vertex steps per underside quad. Smaller is finer and heavier; must divide 1024.</summary>
    public const int DefaultStep = 16;

    public static bool IsValidStep(int step) => step is 4 or 8 or 16 or 32 or 64 or 128;

    /// <param name="Positions">World-space vertices.</param>
    /// <param name="Indices">Triangles, wound so the faces point down.</param>
    public sealed record Shape(int BlockX, int BlockY, Vector3[] Positions, ushort[] Indices)
    {
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>The underside of the LOD32 block whose south-west cell is (blockX, blockY), or null where it has no terrain.</summary>
    public static Shape? Build(TerrainHeights terrain, string worldspace, int blockX, int blockY, int step = DefaultStep)
    {
        if (!IsValidStep(step)) throw new ArgumentOutOfRangeException(nameof(step), "Step must be 4, 8, 16, 32, 64 or 128.");
        int n = BlockVertices / step;      // quads per side
        int stride = n + 2;                // blocks run from -1 to n so that border vertices see their outside neighbours
        int gx0 = blockX * (TerrainHeights.Grid - 1), gy0 = blockY * (TerrainHeights.Grid - 1);

        // Lowest LAND height inside each block (edges included), NaN where there is none.
        var low = new float[stride * stride];
        for (int bj = -1; bj <= n; bj++)
            for (int bi = -1; bi <= n; bi++)
            {
                float m = float.NaN;
                for (int y = 0; y <= step; y++)
                    for (int x = 0; x <= step; x++)
                        if (terrain.VertexAt(worldspace, gx0 + bi * step + x, gy0 + bj * step + y) is { } h && !(h >= m))
                            m = h;
                low[(bj + 1) * stride + bi + 1] = m;
            }

        float Low(int bi, int bj) => low[(bj + 1) * stride + bi + 1];

        // A vertex takes the lowest of the (up to) four blocks around it.
        int vstride = n + 1;
        var z = new float[vstride * vstride];
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float m = float.NaN;
                foreach (var v in (ReadOnlySpan<float>)[Low(i - 1, j - 1), Low(i, j - 1), Low(i - 1, j), Low(i, j)])
                    if (!float.IsNaN(v) && !(v >= m)) m = v;
                z[j * vstride + i] = m;
            }

        var remap = new int[z.Length];
        Array.Fill(remap, -1);
        var positions = new List<Vector3>();
        var indices = new List<ushort>();
        float unit = step * (CellCoord.CellSize / (TerrainHeights.Grid - 1));
        float ox = blockX * CellCoord.CellSize, oy = blockY * CellCoord.CellSize;

        int Use(int i, int j)
        {
            int k = j * vstride + i;
            if (remap[k] < 0)
            {
                remap[k] = positions.Count;
                positions.Add(new Vector3(ox + i * unit, oy + j * unit, z[k]));
            }
            return remap[k];
        }

        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                if (float.IsNaN(Low(i, j))) continue;   // no terrain under this square
                int a = Use(i, j), b = Use(i + 1, j), c = Use(i + 1, j + 1), d = Use(i, j + 1);
                indices.Add((ushort)a); indices.Add((ushort)c); indices.Add((ushort)b);
                indices.Add((ushort)a); indices.Add((ushort)d); indices.Add((ushort)c);
            }

        return indices.Count == 0 ? null : new Shape(blockX, blockY, [.. positions], [.. indices]);
    }
}
