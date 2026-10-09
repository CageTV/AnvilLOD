using System.Collections.Concurrent;
using System.Diagnostics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Plugins;

/// <summary>
/// The terrain underside step of a Generate run: one <c>meshes\Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c> per worldspace
/// that has LOD32 blocks, plus <c>AnvilLOD Underside.esm</c> (ESL) that places them. Volumetric lighting mods (DVLaSS,
/// EVLaS, Community Shaders' sky sync) need it to stop rays and shadows leaking through the landscape.
/// </summary>
public static class UndersideStage
{
    /// <summary>Worldspaces DynDOLOD leaves without an underside (Blackreach, Soul Cairn, Apocrypha, …): nothing to look up at.</summary>
    public static readonly IReadOnlySet<string> IgnoredWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Blackreach", "DLC01SoulCairn", "DLC2ApocryphaWorld", "ccBGSSSE067DeadlandsWorld", "ccKRTSSE001QNWorld", "JaphetsFollyWorld",
    };

    public sealed record Result(int Worldspaces, int Blocks, long Triangles, int FilesWritten, int FilesUnchanged, string? Plugin, TimeSpan Elapsed);

    public static Result Run(GameContext game, TerrainHeights terrain, IReadOnlyDictionary<string, LodGrid> grids, string output,
        int step, IProgress<string>? progress, Action<string> warn, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var made = new List<string>();
        int blocks = 0, written = 0, unchanged = 0;
        long tris = 0;

        foreach (var (ws, grid) in grids.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (IgnoredWorlds.Contains(ws) || grid.Settings.MaxLevel < (int)LodLevel.Lod32) continue;

            var origins = terrain.CellsOf(ws)
                .Select(c => grid.BlockOrigin(c, LodLevel.Lod32))
                .Distinct()
                .ToList();
            if (origins.Count == 0) continue;

            var shapes = new ConcurrentBag<UndersideMesher.Shape>();
            Parallel.ForEach(origins, new ParallelOptions { CancellationToken = ct }, o =>
            {
                if (UndersideMesher.Build(terrain, ws, o.X, o.Y, step) is { } shape) shapes.Add(shape);
            });

            var nif = UndersideBuilder.Build(ws, [.. shapes]);
            if (nif is null) continue;

            var path = Path.Combine(output, UndersideBuilder.RelativePath(ws));
            if (File.Exists(path) && new FileInfo(path).Length == nif.Bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(nif.Bytes))
                unchanged++;
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, nif.Bytes);
                written++;
            }
            made.Add(ws);
            blocks += nif.Shapes;
            tris += nif.Triangles;
            progress?.Report($"Underside: {ws}: {nif.Shapes} blocks, {nif.Triangles:N0} triangles, {nif.Bytes.Length / 1024:N0} KB");
        }

        // Files from an earlier run for worldspaces that no longer get one.
        RemoveFiles(output, keep: made);

        string? plugin = null;
        if (made.Count > 0)
        {
            var r = UndersidePluginWriter.Write(game, made, output);
            plugin = r.Path;
            progress?.Report($"Underside: {UndersidePluginWriter.FileName} and {UndersidePluginWriter.PlacementFileName} written for {r.Worldspaces.Count} worldspaces (placement masters: {string.Join(", ", r.Masters)})");
            if (r.Skipped.Count > 0)
                warn($"Underside: no worldspace record found for {string.Join(", ", r.Skipped)}; they get no placement (their NIF is still written).");
        }
        else RemovePlugin(output);

        return new Result(made.Count, blocks, tris, written, unchanged, plugin, sw.Elapsed);
    }

    /// <summary>Deletes every underside file this tool wrote (used when the option is off).</summary>
    public static void RemoveAll(string output)
    {
        RemoveFiles(output, keep: []);
        RemovePlugin(output);
    }

    private static void RemovePlugin(string output)
    {
        try
        {
            foreach (var name in new[] { UndersidePluginWriter.FileName, UndersidePluginWriter.PlacementFileName })
            {
                var file = Path.Combine(output, name);
                if (File.Exists(file)) File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RemoveFiles(string output, IReadOnlyCollection<string> keep)
    {
        var root = Path.Combine(output, "meshes", "Terrain");
        if (!Directory.Exists(root)) return;
        var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*_Underside.nif", SearchOption.AllDirectories))
            {
                var dir = Path.GetFileName(Path.GetDirectoryName(file));
                if (dir is not null && keepSet.Contains(dir)) continue;
                File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
