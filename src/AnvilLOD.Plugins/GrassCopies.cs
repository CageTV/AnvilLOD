using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes.Authoring;

namespace AnvilLOD.Plugins;

/// <summary>
/// Grass LOD for the walled cities' child worldspaces. Grass LOD is part of the parent's (Tamriel's) LOD blocks, and the engine hides
/// those blocks where the child worldspace has cells, so the plains around a city lose their LOD grass as you approach the walls
/// (the same bare rectangle the other Parent &gt; Child copies fill in with objects). One small loose mesh per cell, drawn by the
/// AnvilLOD SKSE plugin like any other copy: no plugin, nothing written into the blocks. Cells where the city has its own terrain are left out.
/// </summary>
public static class GrassCopies
{
    public const string Folder = "anvillod\\grasscopy";

    public sealed record Result(List<ParentCopy> Copies, int Meshes, int Cells);

    public static Result Build(GrassLodSource grass, IReadOnlyList<ParentCopyRegion> regions, string outputFolder, CancellationToken ct = default)
    {
        var copies = new List<ParentCopy>();
        int meshes = 0;
        var byCell = grass.Cells.Where(r => r.Meshes.Lod4 is not null)
            .ToDictionary(r => (r.Worldspace.ToLowerInvariant(), (int)MathF.Round(r.Position.X / CellCoord.CellSize), (int)MathF.Round(r.Position.Y / CellCoord.CellSize)));
        var written = new HashSet<(string, int, int)>();
        foreach (var region in regions)
        {
            foreach (var ((ws, x, y), r) in byCell)
            {
                ct.ThrowIfCancellationRequested();
                if (!ws.Equals(region.Parent, StringComparison.OrdinalIgnoreCase) || !region.Config.Covers(x, y)) continue;
                // Where the city worldspace has its own terrain (its streets and gardens) its own ground and grass rules decide; the plains' grass LOD
                // would show as tufts on the cobbles. Only the plains the city hides (cells it owns without terrain, and the margin) get copies.
                if (region.LandCells is { } land && land.Contains((x, y))) continue;
                var rel = GamePath.Join(Folder, $"{r.Worldspace}_{x}_{y}.nif".ToLowerInvariant());
                if (written.Add((ws, x, y)))
                {
                    var mesh = grass.Build(r.Meshes.Lod4!);
                    if (mesh is null || mesh.Parts.Count == 0 || mesh.Parts.All(p => p.TriangleCount == 0)) { written.Remove((ws, x, y)); continue; }
                    var full = Path.Combine(outputFolder, "meshes", rel.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, LodNifWriter.Write($"grass_{x}_{y}", mesh.Parts, "AnvilLOD grass copy"));
                    meshes++;
                }
                copies.Add(new ParentCopy(region.ChildWorldspace, region.WorldPlugin, region.WorldLocalId,
                    "grass cache", 0, "Grass LOD", rel, r.Position, System.Numerics.Vector3.Zero, 1f, x, y,
                    region.ChildCells.Contains((x, y)), false));
            }
        }
        return new Result(copies, meshes, written.Count);
    }
}
