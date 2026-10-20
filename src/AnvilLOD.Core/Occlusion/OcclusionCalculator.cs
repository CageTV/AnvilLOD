using System.Collections.Concurrent;

namespace AnvilLOD.Core.Occlusion;

/// <summary>How the occlusion is worked out.</summary>
/// <param name="RadiusCells">Blocks farther than this from the cell are never visible (the engine draws about 100 cells).</param>
/// <param name="Clearance">Units a hill must stand above the line of sight before it hides what is behind it. Larger = safer: fewer blocks
/// are hidden, none by mistake. Measured against xLODGen's own result on Tamriel (with the default 8 target points and 2 eye points per
/// block, which are what make it fast): 1024 hides about 7% of the blocks xLODGen keeps visible, 2048 about 5%, 3072 about 3%, 4096 about 2%.</param>
/// <param name="SamplesPerCellSide">Sample points per cell side, for the observer and the target (2 = 4 points per cell, xLODGen's quality 2).</param>
/// <param name="EyeAdd">Extra height of the observer, on top of the highest point of the sample.</param>
/// <param name="ParentClearance">The same for the 8- and 16-cell quads. xLODGen works those out apart from the blocks.</param>
/// <param name="ParentTargetPoints">How many of a quad's highest sample points are tried as targets.</param>
/// <param name="BlockTargetPoints">How many of a block's highest sample points are tried as targets (0 = all of them). The highest points are the
/// likeliest to be seen, so a few are enough; fewer points make it faster.</param>
/// <param name="EyePoints">How many of the observer cell's sample points are tried (0 = all of them, the highest first).</param>
public sealed record OcclusionSettings(int RadiusCells = 100, float Clearance = 3072f, int SamplesPerCellSide = 2, float EyeAdd = 0f,
    float ParentClearance = 3072f, int ParentTargetPoints = 16, int BlockTargetPoints = 8, int EyePoints = 2)
{
    public string Fingerprint => $"occ:r{RadiusCells}:c{Clearance:0}:s{SamplesPerCellSide}:e{EyeAdd:0}:p{ParentClearance:0}:{ParentTargetPoints}:{BlockTargetPoints}:{EyePoints}";
}

/// <summary>
/// Works out which LOD blocks can be seen from each cell, from a height grid, and packs it as TVDT data (see <see cref="TvdtData"/>).
/// A block is visible when some sample point of the observer cell has a clear straight line to some sample point of some cell of the
/// block. Eye and target use the highest thing in their sample (terrain or object), the hills in between are the bare terrain. Fitted to,
/// and compared against, xLODGen's own output: see <see cref="OcclusionSettings"/>.
/// </summary>
public sealed class OcclusionCalculator
{
    private readonly OcclusionGrid _grid;
    private readonly int _originX, _originY;       // south-west cell of the worldspace's .lod file
    private readonly OcclusionSettings _settings;
    private readonly int _sub;                       // samples per sub-area side
    private readonly Block?[] _blocks = new Block?[TvdtData.LeavesPerSide * TvdtData.LeavesPerSide];
    private readonly Block?[][] _groups = new Block?[ParentTestLevels + 1][];   // [level] the 8-cell (1) and 16-cell (2) quads, by index y * side + x
    private const int ParentTestLevels = 2;       // levels above this are simply 'any child visible'

    private sealed record Block(float[] X, float[] Y, float[] H, bool Complete, bool AnyData);

    public OcclusionCalculator(OcclusionGrid grid, int lodOriginCellX, int lodOriginCellY, OcclusionSettings? settings = null)
    {
        _grid = grid;
        _originX = lodOriginCellX;
        _originY = lodOriginCellY;
        _settings = settings ?? new OcclusionSettings();
        int n = Math.Clamp(_settings.SamplesPerCellSide, 1, 4);
        _sub = OcclusionGrid.SamplesPerCell / n;
        for (int by = 0; by < TvdtData.LeavesPerSide; by++)
            for (int bx = 0; bx < TvdtData.LeavesPerSide; bx++)
                _blocks[by * TvdtData.LeavesPerSide + bx] = BuildBlock(bx, by);
        for (int level = 1; level <= ParentTestLevels; level++)
        {
            int quads = TvdtData.LeavesPerSide >> level;
            _groups[level] = new Block?[quads * quads];
            for (int y = 0; y < quads; y++)
                for (int x = 0; x < quads; x++) _groups[level][y * quads + x] = BuildGroup(level, x, y);
        }
    }

    private Block BuildBlock(int bx, int by)
    {
        var xs = new List<float>(); var ys = new List<float>(); var hs = new List<float>();
        int cells = 0, withData = 0;
        for (int cj = 0; cj < 4; cj++)
            for (int ci = 0; ci < 4; ci++)
            {
                int cx = _originX + bx * 4 + ci, cy = _originY + by * 4 + cj;
                cells++; withData++;
                foreach (var p in SamplePoints(cx, cy)) { xs.Add(p.x); ys.Add(p.y); hs.Add(p.h); }
            }
        // highest first: those are the likeliest to be seen, so the search for one visible point ends sooner
        var order = Enumerable.Range(0, hs.Count).OrderByDescending(i => hs[i]).Take(_settings.BlockTargetPoints > 0 ? _settings.BlockTargetPoints : int.MaxValue).ToArray();
        return new Block(order.Select(i => xs[i]).ToArray(), order.Select(i => ys[i]).ToArray(), order.Select(i => hs[i]).ToArray(), withData == cells, withData > 0);
    }

    /// <summary>A quad of 8 or 16 cells as one target: its highest sample points, from all of its cells.</summary>
    private Block BuildGroup(int level, int gx, int gy)
    {
        int span = 4 << level;
        var pts = new List<(float x, float y, float h)>();
        int cells = 0, withData = 0;
        for (int cj = 0; cj < span; cj++)
            for (int ci = 0; ci < span; ci++)
            {
                int cx = _originX + gx * span + ci, cy = _originY + gy * span + cj;
                cells++; withData++;
                pts.AddRange(SamplePoints(cx, cy));
            }
        var top = pts.OrderByDescending(p => p.h).Take(Math.Max(1, _settings.ParentTargetPoints)).ToArray();
        return new Block(top.Select(p => p.x).ToArray(), top.Select(p => p.y).ToArray(), top.Select(p => p.h).ToArray(), withData == cells, withData > 0);
    }

    /// <summary>The sample points of a cell: the sample grid position (in samples, relative to the grid) and the highest height in the sub-area.</summary>
    private IEnumerable<(float x, float y, float h)> SamplePoints(int cellX, int cellY)
    {
        int gx = (cellX - _grid.CellX0) * OcclusionGrid.SamplesPerCell, gy = (cellY - _grid.CellY0) * OcclusionGrid.SamplesPerCell;
        int n = OcclusionGrid.SamplesPerCell / _sub;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float best = float.MinValue;
                for (int y = 0; y < _sub; y++)
                    for (int x = 0; x < _sub; x++)
                    {
                        float h = _grid.TopAt(gx + i * _sub + x, gy + j * _sub + y);
                        if (h > best) best = h;
                    }
                yield return (gx + i * _sub + _sub / 2f, gy + j * _sub + _sub / 2f, best);
            }
    }

    /// <summary>The TVDT of one exterior cell. A cell without height data gets none: ask only for cells the grid knows.</summary>
    public TvdtData Compute(int cellX, int cellY)
    {
        var eyeList = SamplePoints(cellX, cellY).Select(p => (p.x, p.y, h: p.h + _settings.EyeAdd));
        var eyes = (_settings.EyePoints > 0 ? eyeList.OrderByDescending(e => e.h).Take(_settings.EyePoints) : eyeList).ToArray();
        var tvdt = new TvdtData { Lead = 3 };
        int side = TvdtData.LeavesPerSide;
        double cx = cellX + 0.5, cy = cellY + 0.5;
        int ownX = (cellX - _originX) / 4, ownY = (cellY - _originY) / 4;
        var leaf = new bool[side, side];
        for (int by = 0; by < side; by++)
            for (int bx = 0; bx < side; bx++)
            {
                double dx = _originX + bx * 4 + 2 - cx, dy = _originY + by * 4 + 2 - cy;
                if (Math.Sqrt(dx * dx + dy * dy) > _settings.RadiusCells) continue;
                var block = _blocks[by * side + bx]!;
                bool visible;
                if (bx == ownX && by == ownY) visible = true;
                else visible = Sees(eyes, block, _settings.Clearance);
                leaf[bx, by] = visible;
            }
        if (ownX is >= 0 and < TvdtData.LeavesPerSide && ownY is >= 0 and < TvdtData.LeavesPerSide) leaf[ownX, ownY] = true;

        // Quads. xLODGen works each level out apart from the blocks, and its LOD8 quads turn out to be more generous than "any block below is
        // visible" (checked on Tamriel: even the 3 x 3 quads around it miss 9% of the quads it shows). So a quad here is visible when any block
        // in it or in the quads touching it is visible, or, for the two lowest levels, when a line of sight to the whole quad is clear;
        // and the higher levels simply follow what is below them. Never less than the blocks below, so nothing visible is ever hidden by a quad.
        var seen = new bool[TvdtData.TopLevel + 1][,];
        seen[0] = leaf;
        for (int level = 1; level <= TvdtData.TopLevel; level++)
        {
            int n = side >> level;
            seen[level] = new bool[n, n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    seen[level][x, y] = seen[level - 1][2 * x, 2 * y] || seen[level - 1][2 * x + 1, 2 * y] || seen[level - 1][2 * x, 2 * y + 1] || seen[level - 1][2 * x + 1, 2 * y + 1];
        }
        for (int by = 0; by < side; by++)
            for (int bx = 0; bx < side; bx++)
                if (leaf[bx, by]) tvdt.SetLeaf(bx, by, true);
        for (int level = 1; level <= TvdtData.TopLevel; level++)
        {
            int n = side >> level;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool any = seen[level][x, y] || tvdt.GetNode(level - 1, 2 * x, 2 * y) || tvdt.GetNode(level - 1, 2 * x + 1, 2 * y) || tvdt.GetNode(level - 1, 2 * x, 2 * y + 1) || tvdt.GetNode(level - 1, 2 * x + 1, 2 * y + 1);   // never less than the nodes below it
                    for (int dy2 = -1; dy2 <= 1 && !any; dy2++)
                        for (int dx2 = -1; dx2 <= 1 && !any; dx2++)
                        {
                            int nx = x + dx2, ny = y + dy2;
                            if ((uint)nx < (uint)n && (uint)ny < (uint)n) any = seen[level][nx, ny];
                        }
                    if (!any)
                    {
                        double ddx = _originX + x * (4 << level) + (2 << level) - cx, ddy = _originY + y * (4 << level) + (2 << level) - cy;
                        double dist = Math.Sqrt(ddx * ddx + ddy * ddy);
                        if (level <= ParentTestLevels)
                        {
                            if (dist <= _settings.RadiusCells * 2.0) any = Sees(eyes, _groups[level][y * n + x]!, _settings.ParentClearance);
                        }
                        else any = dist <= _settings.RadiusCells * 4.0;
                    }
                    if (any) tvdt.SetNode(level, x, y, true);
                }
        }
        return tvdt;
    }

    private bool Sees((float x, float y, float h)[] eyes, Block block, float clearance)
    {
        var terrain = _grid.Terrain;
        int w = _grid.Width, h = _grid.Height;
        float fallback = _grid.DefaultHeight;
        for (int t = 0; t < block.H.Length; t++)
        {
            float tx = block.X[t], ty = block.Y[t], th = block.H[t];
            foreach (var (ex, ey, eh) in eyes)
            {
                float dx = tx - ex, dy = ty - ey;
                float len = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
                if (len < 1f) return true;
                float sx = dx / len, sy = dy / len;
                int steps = (int)len;
                bool clear = true;
                for (int s = 1; s < steps; s++)
                {
                    int ix = (int)(ex + sx * s + 0.5f), iy = (int)(ey + sy * s + 0.5f);
                    float ground = (uint)ix < (uint)w && (uint)iy < (uint)h ? terrain[iy * w + ix] : fallback;
                    if (ground <= OcclusionGrid.Unknown) ground = fallback;
                    if (ground > eh + (th - eh) * (s / len) + clearance) { clear = false; break; }
                }
                if (clear) return true;
            }
        }
        return false;
    }

    /// <summary>Computes every listed cell, using all cores. Cells outside the grid's data are skipped.</summary>
    public IReadOnlyDictionary<(int X, int Y), TvdtData> ComputeMany(IEnumerable<(int X, int Y)> cells, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var list = cells.Where(c => _grid.HasData(c.X, c.Y)).ToList();
        var result = new ConcurrentDictionary<(int X, int Y), TvdtData>();
        int done = 0;
        Parallel.ForEach(list, new ParallelOptions { CancellationToken = ct }, c =>
        {
            result[c] = Compute(c.X, c.Y);
            progress?.Report(Interlocked.Increment(ref done));
        });
        return result;
    }

    /// <summary>
    /// The cell's MHDT: a base height (a multiple of 128 at or below the lowest point) and a 32 x 32 grid of steps of 128 above it, stored
    /// with X as the major index (<c>bytes[x * 32 + y]</c>). Rounded up, so the engine's "player above the cell's highest point" test
    /// never ignores the occlusion data too early.
    /// </summary>
    public static (float Offset, byte[] Steps) BuildMaxHeight(OcclusionGrid grid, int cellX, int cellY)
    {
        int gx = (cellX - grid.CellX0) * OcclusionGrid.SamplesPerCell, gy = (cellY - grid.CellY0) * OcclusionGrid.SamplesPerCell;
        float min = float.MaxValue;
        for (int j = 0; j < 32; j++)
            for (int i = 0; i < 32; i++)
            {
                float h = grid.Top[(gy + j) * grid.Width + gx + i];
                if (h > OcclusionGrid.Unknown && h < min) min = h;
            }
        if (min == float.MaxValue) return (0f, new byte[1024]);
        float offset = MathF.Floor(min / OcclusionGrid.SampleSize) * OcclusionGrid.SampleSize;
        var steps = new byte[1024];
        for (int i = 0; i < 32; i++)
            for (int j = 0; j < 32; j++)
            {
                float h = grid.Top[(gy + j) * grid.Width + gx + i];
                float s = h > OcclusionGrid.Unknown ? MathF.Ceiling((h - offset) / OcclusionGrid.SampleSize) : 0f;
                steps[i * 32 + j] = (byte)Math.Clamp(s, 0f, 255f);
            }
        return (offset, steps);
    }
}
