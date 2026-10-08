using System.Diagnostics;
using System.Security.Cryptography;
using AnvilLOD.Core.World;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>Reads terrain heights (winning LAND/VHGT per exterior cell) for the given worldspaces.</summary>
public static class TerrainReader
{
    public sealed record Result(TerrainHeights Heights, IReadOnlyDictionary<string, string> Fingerprints, TimeSpan Elapsed);

    public static Result Read(GameContext game, IReadOnlyCollection<string> worldspaces, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var wanted = new HashSet<string>(worldspaces, StringComparer.OrdinalIgnoreCase);
        var heights = new TerrainHeights();
        var perWs = new Dictionary<string, List<(int X, int Y, float[] H)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ctx in game.LoadOrder.PriorityOrder.Landscape().WinningContextOverrides(game.LinkCache))
        {
            ct.ThrowIfCancellationRequested();
            if (!ctx.TryGetParentSimpleContext<ICellGetter>(out var cellCtx)) continue;
            var grid = cellCtx.Record.Grid;
            if (grid is null) continue;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx)) continue;
            var ws = wsCtx.Record.EditorID;
            if (ws is null || !wanted.Contains(ws)) continue;

            var vh = ctx.Record.VertexHeightMap;
            if (vh is null) continue;
            var map = vh.HeightMap;
            var h = TerrainHeights.Decode(vh.Offset, (x, y) => map[x, y]);
            heights.Set(ws, grid.Point.X, grid.Point.Y, h);

            if (!perWs.TryGetValue(ws, out var list)) perWs[ws] = list = [];
            list.Add((grid.Point.X, grid.Point.Y, h));
        }

        // Fingerprint per worldspace, so editing terrain rebuilds that worldspace's blocks.
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ws, cells) in perWs)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[TerrainHeights.Grid * TerrainHeights.Grid * 4 + 8];
            foreach (var c in cells.OrderBy(c => c.X).ThenBy(c => c.Y))
            {
                BitConverter.TryWriteBytes(buf.AsSpan(0, 4), c.X);
                BitConverter.TryWriteBytes(buf.AsSpan(4, 4), c.Y);
                Buffer.BlockCopy(c.H, 0, buf, 8, c.H.Length * 4);
                hash.AppendData(buf);
            }
            fingerprints[ws] = Convert.ToHexString(hash.GetHashAndReset(), 0, 8);
        }

        return new Result(heights, fingerprints, sw.Elapsed);
    }
}
