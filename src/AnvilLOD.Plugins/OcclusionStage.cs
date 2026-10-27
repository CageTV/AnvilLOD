using System.Diagnostics;
using AnvilLOD.Core.Occlusion;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AnvilLOD.Plugins;

/// <summary>
/// Makes <c>Occlusion.esp</c> itself: for every big worldspace, a height grid (landscape, water, and the tops of the objects and trees that
/// are in LOD) goes to <see cref="OcclusionCalculator"/>, and the result is written as the TVDT and MHDT of the worldspace's exterior cells
/// (CELL overrides only, ESL-flagged, load it last). It does what xLODGen's and DynDOLOD's occlusion run does, from data AnvilLOD already has.
/// </summary>
public static class OcclusionStage
{
    public const string FileName = "Occlusion.esp";

    public sealed record Result(string? Path, int Worldspaces, int Cells, int Objects, int Trees, TimeSpan Elapsed, IReadOnlyList<string> Skipped);

    /// <summary>Worldspaces whose LOD grid is smaller than this (cells per side) are left alone, like xLODGen does.</summary>
    public const int MinStride = 128;

    public static Result Run(GameContext game, TerrainHeights terrain, IReadOnlyDictionary<string, LodGrid> grids, IReadOnlyList<LodReference> refs,
        IReadOnlyList<TreeReference>? trees, IAssetSource assets, string folder, OcclusionSettings settings, IProgress<string>? progress,
        Action<string> warn, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var skipped = new List<string>();
        var children = UndersidePluginWriter.ChildWorldspaces(game);
        var worldspaces = game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides()
            .Where(w => w.EditorID is not null)
            .ToDictionary(w => w.EditorID!, w => w, StringComparer.OrdinalIgnoreCase);

        var mod = new SkyrimMod(ModKey.FromNameAndExtension(FileName), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        mod.ModHeader.Stats.NextFormID = 0x800;
        var boundsCache = new Dictionary<string, (float Radius, float Top)?>(StringComparer.OrdinalIgnoreCase);
        int worlds = 0, cellsTotal = 0, objectsTotal = 0, treesTotal = 0;

        foreach (var (ws, grid) in grids.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (grid.Settings.Stride < MinStride || UndersideStage.IgnoredWorlds.Contains(ws) || children.Contains(ws)) { skipped.Add(ws); continue; }
            var landCells = terrain.CellsOf(ws).ToList();
            if (landCells.Count < 64) { skipped.Add(ws); continue; }
            if (!worldspaces.TryGetValue(ws, out var wrld)) { skipped.Add(ws); continue; }

            int x0 = landCells.Min(c => c.X), x1 = landCells.Max(c => c.X), y0 = landCells.Min(c => c.Y), y1 = landCells.Max(c => c.Y);
            float seaLevel = wrld.LandDefaults?.DefaultWaterHeight is { } wh && float.IsFinite(wh) ? wh : -14000f;
            var heights = new OcclusionGrid(x0, y0, x1 - x0 + 1, y1 - y0 + 1, seaLevel);
            foreach (var c in landCells)
            {
                if (terrain.TryGetCell(ws, c.X, c.Y, out var v)) heights.SetTerrain(c.X, c.Y, v);
                if (terrain.WaterAt(ws, c.X * 4096f + 2048f, c.Y * 4096f + 2048f) is { } water)
                    heights.RaiseTop(c.X * 4096f, c.Y * 4096f, (c.X + 1) * 4096f - 1f, (c.Y + 1) * 4096f - 1f, water);
            }

            // The tops of what stands in LOD: objects by their LOD mesh, trees by their billboard.
            int objects = 0, treeCount = 0;
            var owned = new List<(float X, float Y, float Radius, float Top)>();
            foreach (var r in refs)
            {
                if (!string.Equals(r.Worldspace, ws, StringComparison.OrdinalIgnoreCase)) continue;
                var mesh = r.Meshes.Lod4 ?? r.Meshes.Lod8 ?? r.Meshes.Lod16 ?? r.Meshes.Lod32;
                if (string.IsNullOrEmpty(mesh)) continue;
                var b = MeshBounds(assets, mesh, boundsCache);
                if (b is null) continue;
                float scale = r.Scale <= 0f ? 1f : r.Scale, radius = b.Value.Radius * scale;
                float top = r.Position.Z + b.Value.Top * scale;
                heights.RaiseTop(r.Position.X - radius, r.Position.Y - radius, r.Position.X + radius, r.Position.Y + radius, top);
                owned.Add((r.Position.X, r.Position.Y, radius, top));
                objects++;
            }
            if (trees is not null)
                foreach (var t in trees)
                {
                    if (!string.Equals(t.Worldspace, ws, StringComparison.OrdinalIgnoreCase)) continue;
                    float half = Math.Max(t.Billboard.Width * 0.5f, 64f);
                    heights.RaiseTop(t.Position.X - half, t.Position.Y - half, t.Position.X + half, t.Position.Y + half,
                        t.Position.Z + Math.Max(t.Billboard.Height, t.Billboard.Height + t.Billboard.ShiftZ));
                    treeCount++;
                }

            // Which cells get data: the worldspace's own bounds (the playable area), landscape cells that are not perfectly flat.
            var bmin = wrld.ObjectBoundsMin; var bmax = wrld.ObjectBoundsMax;
            bool bounded = bmax.X > bmin.X && bmax.Y > bmin.Y;
            var todo = landCells.Where(c => !bounded || (c.X >= (int)MathF.Floor(bmin.X) && c.X <= (int)MathF.Ceiling(bmax.X) && c.Y >= (int)MathF.Floor(bmin.Y) && c.Y <= (int)MathF.Ceiling(bmax.Y)))
                .Where(c => terrain.TryGetCell(ws, c.X, c.Y, out var v) && v.Any(h => h != v[0])).Select(c => (c.X, c.Y)).ToList();
            progress?.Report($"Occlusion {ws}: {todo.Count:N0} cells, {objects:N0} objects and {treeCount:N0} trees in the height grid ({heights.Width}x{heights.Height} samples)");
            var calc = new OcclusionCalculator(heights, grid.Settings.SwCellX, grid.Settings.SwCellY, settings);
            calc.AddOwnedTargets(owned);
            int last = 0;
            var data = calc.ComputeMany(todo, new Progress<int>(n => { if (n - last >= 2000) { last = n; progress?.Report($"Occlusion {ws}: {n:N0} of {todo.Count:N0} cells"); } }), ct);

            int written = 0;
            foreach (var ctx in game.LoadOrder.PriorityOrder.Cell().WinningContextOverrides(game.LinkCache))
            {
                if (ctx.Record.Grid is not { } g || !data.TryGetValue((g.Point.X, g.Point.Y), out var tvdt)) continue;
                if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx) || !string.Equals(wsCtx.Record.EditorID, ws, StringComparison.OrdinalIgnoreCase)) continue;
                var cell = ctx.GetOrAddAsOverride(mod);
                var (offset, steps) = OcclusionCalculator.BuildMaxHeight(heights, g.Point.X, g.Point.Y);
                var map = new Array2d<byte>(new byte[32, 32]);
                for (int i = 0; i < 32; i++)
                    for (int j = 0; j < 32; j++) map[j, i] = steps[i * 32 + j];       // Mutagen: [column, row]; the file's rows run along X
                cell.MaxHeightData = new CellMaxHeightData { Offset = offset, HeightMap = map };
                cell.OcclusionData = tvdt.ToBytes();
                written++;
            }
            progress?.Report($"Occlusion {ws}: {written:N0} cells written");
            worlds++; cellsTotal += written; objectsTotal += objects; treesTotal += treeCount;
        }

        if (cellsTotal == 0)
        {
            warn("Occlusion: no worldspace was big enough or had landscape, so no Occlusion.esp was made.");
            return new Result(null, 0, 0, 0, 0, sw.Elapsed, skipped);
        }
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, FileName);
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != mod.ModKey).ToList();
        mod.WriteToBinary(path, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });
        return new Result(path, worlds, cellsTotal, objectsTotal, treesTotal, sw.Elapsed, skipped);
    }

    /// <summary>A LOD mesh's footprint radius and top (local units), cached by path. Null when it can't be read.</summary>
    private static (float Radius, float Top)? MeshBounds(IAssetSource assets, string path, Dictionary<string, (float Radius, float Top)?> cache)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        (float Radius, float Top)? result = null;
        try
        {
            var rel = path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path;
            if (assets.TryOpen(rel, out var stream))
            {
                byte[] bytes;
                using (stream) { using var ms = new MemoryStream(); stream.CopyTo(ms); bytes = ms.ToArray(); }
                var mesh = AnvilLOD.Meshes.Nif.NifGeometryReader.Read(rel, bytes);
                float radius = 0f, top = float.MinValue;
                foreach (var part in mesh.Parts)
                    foreach (var p in part.Positions)
                    {
                        radius = MathF.Max(radius, MathF.Sqrt(p.X * p.X + p.Y * p.Y));
                        top = MathF.Max(top, p.Z);
                    }
                if (top > float.MinValue) result = (radius, top);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException or IOException) { }
        cache[path] = result;
        return result;
    }
}
