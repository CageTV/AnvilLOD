using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;

namespace AnvilLOD.Plugins;

/// <summary>
/// River and pond water for the walled cities' child worldspaces. The parent's cell water around a city (the White River and the
/// ponds south of Whiterun) is drawn by the terrain LOD's water, which the child worldspace switches off (NoLodWater), and the
/// loaded cells of the child have no water of their own: so a stretch of river near the walls is simply missing. For every parent cell
/// with its own water height inside a copy region this writes a flat plane at that height over the quads where the terrain lies
/// below it (merged into runs), with the lit "fake water" material the stream stand-ins use, and hands it to the SKSE plugin as a
/// copy.
/// </summary>
public static class WaterCopies
{
    public const string Folder = "anvillod\\watercopy";

    public sealed record Result(List<ParentCopy> Copies, int Cells, int Runs);

    public static Result Build(TerrainHeights terrain, IReadOnlyDictionary<(string Ws, int X, int Y), float> waterCells,
        IReadOnlyList<ParentCopyRegion> regions, LodMeshPart template, string outputFolder, CancellationToken ct = default)
    {
        var copies = new List<ParentCopy>();
        var done = new HashSet<(string, int, int)>();
        int runsTotal = 0;
        var depthLog = new List<string> { "child,parent,x,y,waterHeight,lowestTerrain,depth,bankSlope,maxDrop,drop" };
        float uvPerUnit = UvPerUnit(template);
        // The stream stand-ins are tinted dark olive (0x2A3433, alpha 0xAF); the terrain LOD water this replaces is blue, so a
        // wider river / pond gets a blue tint over the same ripple texture and blending (R,G,B,A packed little-endian).
        // It was 0xC0806240, then 0xAD51493C (darker, less blue, 10% more transparent). From the air that read grey (about 105,110,110
        // against Markarth's real water, about 59,86,93 at the same distance), so it is pulled toward that teal: R x0.57, G x0.80, B x0.88.
        const uint color = 0xAD473A22u;
        const bool colors = true;

        foreach (var region in regions)
        {
            foreach (var ((ws, x, y), h) in waterCells)
            {
                ct.ThrowIfCancellationRequested();
                if (!ws.Equals(region.Parent, StringComparison.OrdinalIgnoreCase) || !region.Config.Covers(x, y)) continue;
                var rel = GamePath.Join(Folder, $"{ws}_{x}_{y}.nif".ToLowerInvariant());
                var full = Path.Combine(outputFolder, "meshes", rel.Replace('\\', Path.DirectorySeparatorChar));
                if (done.Add((ws.ToLowerInvariant(), x, y)))
                {
                    // The plane sits below the water height so it does not flicker against real water it overlaps. A city that uses its
                    // parent's water (Whiterun, Windhelm, Solitude) has none to overlap, and lowering the plane there only moves the
                    // shoreline inland and buries a shallow river (tested in game: Whiterun reads right at 3 units, Riften needs 28).
                    float maxDrop = region.UsesParentWater ? 3f : NativeGridObjects.WaterDrop;
                    var part = BuildCell(terrain, ws, x, y, h, template, uvPerUnit, colors, color, maxDrop, out int runs, out float lowest, out float bankSlope, out float drop);
                    if (part is null) { done.Remove((ws.ToLowerInvariant(), x, y)); continue; }
                    runsTotal += runs;
                    depthLog.Add(FormattableString.Invariant($"{region.ChildWorldspace},{ws},{x},{y},{h:F1},{lowest:F1},{h - lowest:F1},{bankSlope:F3},{maxDrop:F0},{drop:F1}"));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllBytes(full, LodNifWriter.Write($"water_{x}_{y}", [part], "AnvilLOD water copy"));
                }
                else if (!File.Exists(full)) continue;
                copies.Add(new ParentCopy(region.ChildWorldspace, region.WorldPlugin, region.WorldLocalId,
                    "cell water", 0, "Cell water", rel, new Vector3(x * CellCoord.CellSize, y * CellCoord.CellSize, 0), Vector3.Zero, 1f, x, y,
                    region.ChildCells.Contains((x, y)), false));
            }
        }
        // A diagnostic for tuning the drop: one line per water cell (water height, lowest terrain under it, the drop used).
        if (depthLog.Count > 1)
        {
            var dir = Path.Combine(outputFolder, "meshes", Folder.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "water depths.csv"), depthLog);
        }
        if (done.Count > 0) WaterTextures.WriteTo(outputFolder);
        return new Result(copies, done.Count, runsTotal);
    }

    /// <summary>Texture units per game unit of the template plane (its UVs against its positions), so the water keeps its ripple scale.</summary>
    internal static float UvPerUnit(LodMeshPart p)
    {
        for (int t = 0; t + 2 < p.Triangles.Length; t += 3)
        {
            int a = p.Triangles[t], b = p.Triangles[t + 1], c = p.Triangles[t + 2];
            float areaPos = MathF.Abs(Vector3.Cross(p.Positions[b] - p.Positions[a], p.Positions[c] - p.Positions[a]).Length()) * 0.5f;
            var e1 = p.UVs[b] - p.UVs[a];
            var e2 = p.UVs[c] - p.UVs[a];
            float areaUv = MathF.Abs(e1.X * e2.Y - e1.Y * e2.X) * 0.5f;
            if (areaPos > 1f && areaUv > 1e-9f) return MathF.Sqrt(areaUv / areaPos);
        }
        return 1f / 2048f;
    }

    private static LodMeshPart? BuildCell(TerrainHeights terrain, string ws, int cx, int cy, float h, LodMeshPart template,
        float uvPerUnit, bool colors, uint color, float maxDrop, out int runs, out float lowest, out float bankSlope, out float drop)
    {
        const int N = TerrainHeights.Grid - 1;   // 32 quads per side
        var pos = new List<Vector3>();
        var uv = new List<Vector2>();
        var tris = new List<ushort>();
        float ox = cx * CellCoord.CellSize, oy = cy * CellCoord.CellSize;
        runs = 0;
        // How far below the water height the plane sits: right at it, it flickers against the real water it overlaps, but a shallow
        // river (Whiterun's) would be buried in its banks by a fixed drop. Deep water (Riften's lake) gets the full drop; shallow
        // water gets 40% of its deepest point, never less than 3 units.
        lowest = h;
        for (int gj = 0; gj <= N; gj++)
            for (int gi = 0; gi <= N; gi++)
                if (terrain.VertexAt(ws, cx * N + gi, cy * N + gj) is { } tv && tv < lowest) lowest = tv;
        drop = Math.Clamp(0.4f * (h - lowest), 3f, maxDrop);
        // How steep the banks are where the water meets the land: the height range of the quads that are partly wet and partly dry,
        // per unit of distance (median over the cell). A flat bank moves the shoreline far inland when the plane is lowered, a steep
        // one barely at all. Logged for now, to tune the drop.
        var slopes = new List<float>();
        for (int gj = 0; gj < N; gj++)
            for (int gi = 0; gi < N; gi++)
            {
                float?[] q = [terrain.VertexAt(ws, cx * N + gi, cy * N + gj), terrain.VertexAt(ws, cx * N + gi + 1, cy * N + gj),
                              terrain.VertexAt(ws, cx * N + gi, cy * N + gj + 1), terrain.VertexAt(ws, cx * N + gi + 1, cy * N + gj + 1)];
                if (q.Any(v => v is null)) continue;
                bool anyWet = q.Any(v => v!.Value < h), anyDry = q.Any(v => v!.Value >= h);
                if (anyWet && anyDry) slopes.Add((q.Max(v => v!.Value) - q.Min(v => v!.Value)) / TerrainHeights.Spacing);
            }
        slopes.Sort();
        bankSlope = slopes.Count == 0 ? -1f : slopes[slopes.Count / 2];
        for (int j = 0; j < N; j++)
        {
            int start = -1;
            for (int i = 0; i <= N; i++)
            {
                bool wet = false;
                if (i < N)
                {
                    int gx = cx * N + i, gy = cy * N + j;
                    float? a = terrain.VertexAt(ws, gx, gy), b = terrain.VertexAt(ws, gx + 1, gy);
                    float? c = terrain.VertexAt(ws, gx, gy + 1), d = terrain.VertexAt(ws, gx + 1, gy + 1);
                    wet = (a is { } va && va < h) || (b is { } vb && vb < h) || (c is { } vc && vc < h) || (d is { } vd && vd < h);
                }
                if (wet) { if (start < 0) start = i; continue; }
                if (start < 0) continue;
                // one rectangle for the run start..i-1 of row j
                float x0 = start * TerrainHeights.Spacing, x1 = i * TerrainHeights.Spacing, y0 = j * TerrainHeights.Spacing, y1 = (j + 1) * TerrainHeights.Spacing;
                int v = pos.Count;
                // The plane sits a little below the water height: right at it, it flickers against the real water it overlaps.
                float z = h - drop;
                pos.Add(new Vector3(x0, y0, z)); pos.Add(new Vector3(x1, y0, z)); pos.Add(new Vector3(x1, y1, z)); pos.Add(new Vector3(x0, y1, z));
                for (int k = 0; k < 4; k++)
                    uv.Add(new Vector2((ox + pos[v + k].X) * uvPerUnit, (oy + pos[v + k].Y) * uvPerUnit));
                tris.AddRange([(ushort)v, (ushort)(v + 1), (ushort)(v + 2), (ushort)v, (ushort)(v + 2), (ushort)(v + 3)]);
                runs++;
                start = -1;
            }
        }
        if (pos.Count == 0) return null;
        int n = pos.Count;
        return new LodMeshPart
        {
            Name = "water",
            Material = template.Material,
            Positions = [.. pos], UVs = [.. uv],
            Normals = Enumerable.Repeat(Vector3.UnitZ, n).ToArray(),
            Tangents = Enumerable.Repeat(Vector3.UnitX, n).ToArray(),
            Bitangents = Enumerable.Repeat(Vector3.UnitY, n).ToArray(),
            Colors = colors ? Enumerable.Repeat(color, n).ToArray() : null,
            Triangles = [.. tris],
        };
    }
}
