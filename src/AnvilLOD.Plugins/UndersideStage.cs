using System.Collections.Concurrent;
using System.Diagnostics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// The terrain underside step of a Generate run: one <c>meshes\Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c> per worldspace
/// that has LOD32 blocks, plus <c>AnvilLOD.esp</c> (ESL) that places them. Volumetric lighting mods (DVLaSS,
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

        var parents = ParentWorldspaces(game);

        foreach (var (ws, grid) in grids.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (IgnoredWorlds.Contains(ws) || grid.Settings.MaxLevel < (int)LodLevel.Lod32) continue;

            // A child worldspace whose terrain sits on cell coordinates its parent also uses (all of Tamriel's children do;
            // DeepwoodRedoubtWorld even repeats the Dragon Bridge cells) can't have an always-enabled underside: the reference
            // is a full-LOD persistent one, and the engine drew DeepwoodRedoubtWorld's in Tamriel too, as a translucent slab at
            // Dragon Bridge (tested in game: Tamriel + Solstheim clean, + Deepwood slab). DynDOLOD keeps such references
            // disabled until a script enables them; without that script the safe choice is none.
            if (parents.TryGetValue(ws, out var parent) && grids.ContainsKey(parent))
            {
                var shared = terrain.CellsOf(ws).Intersect(terrain.CellsOf(parent)).Count();
                if (shared > 0)
                {
                    // Expected, not a problem: log only, so the summary doesn't show a warning on every run.
                    progress?.Report($"Underside: {ws} gets none: it is a child of {parent} on {shared} of the same cell coordinates, where an always-enabled underside is drawn in {parent} as a slab.");
                    continue;
                }
            }

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
            progress?.Report($"Underside: {UndersidePluginWriter.FileName} written for {r.Worldspaces.Count} worldspaces (placement masters: {string.Join(", ", r.Masters)})");
            if (r.Skipped.Count > 0)
                warn($"Underside: no worldspace record found for {string.Join(", ", r.Skipped)}; they get no placement (their NIF is still written).");
        }
        else RemovePlugin(output);

        return new Result(made.Count, blocks, tris, written, unchanged, plugin, sw.Elapsed);
    }

    /// <summary>EditorID of each worldspace's parent worldspace (winning records), for the ones that have one.</summary>
    private static Dictionary<string, string> ParentWorldspaces(GameContext game)
    {
        var worlds = game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides().ToList();
        var byKey = worlds.Where(w => w.EditorID is not null).ToDictionary(w => w.FormKey, w => w.EditorID!);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in worlds)
        {
            if (w.EditorID is null || w.Parent is null) continue;
            if (byKey.TryGetValue(w.Parent.Worldspace.FormKey, out var parent)) result[w.EditorID] = parent;
        }
        return result;
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
            foreach (var name in UndersidePluginWriter.LegacyFileNames.Append(UndersidePluginWriter.FileName))
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
