using System.Collections.Concurrent;

namespace AnvilLOD.Core.World;

/// <summary>
/// Groups references into LOD blocks. A reference goes into a level's block only if it
/// has a mesh for that level. Thread-safe, so the scanner can feed it in parallel.
/// </summary>
public sealed class QuadBucketer
{
    private readonly IReadOnlyDictionary<string, LodGrid> _grids;
    private readonly ConcurrentDictionary<QuadKey, ConcurrentBag<LodReference>> _quads = new();
    private long _outsideGrid;
    private long _noGrid;

    public QuadBucketer(IReadOnlyDictionary<string, LodGrid> gridsByWorldspace)
    {
        _grids = gridsByWorldspace;
    }

    public long SkippedOutsideGrid => Interlocked.Read(ref _outsideGrid);
    public long SkippedNoLodSettings => Interlocked.Read(ref _noGrid);

    public void Add(LodReference r)
    {
        if (!_grids.TryGetValue(r.Worldspace, out var grid))
        {
            Interlocked.Increment(ref _noGrid);
            return;
        }

        var cell = r.Cell;
        if (!grid.IsInsideGrid(cell))
        {
            Interlocked.Increment(ref _outsideGrid);
            return;
        }

        foreach (var level in LodLevels.All)
        {
            if (!grid.Settings.Supports(level)) continue;
            if (string.IsNullOrEmpty(r.Meshes.For(level))) continue;
            _quads.GetOrAdd(grid.QuadFor(cell, level), _ => new()).Add(r);
        }
    }

    /// <summary>Snapshot, with each quad's references sorted by FormKey so output and hashes are deterministic.</summary>
    public IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> Build() =>
        _quads.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<LodReference>)kv.Value.OrderBy(r => r.FormKey, StringComparer.Ordinal).ToList());
}
