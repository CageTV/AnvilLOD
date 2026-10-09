using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.Incremental;
using AnvilLOD.Core.World;
using AnvilLOD.Textures;

namespace AnvilLOD.Plugins;

public sealed record TreeGenerateStats(
    int WorldspacesWritten,
    int WorldspacesUnchanged,
    int TreeTypes,
    int Instances,
    int Blocks,
    int EmptyBlocks,
    int FilesDeleted,
    TimeSpan Elapsed,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Billboard tree LOD in the engine's own format (what xLODGen/DynDOLOD call "Billboard" tree LOD):
/// one <c>.lst</c> + atlas per worldspace and one <c>.btt</c> per LOD4 block.
/// <para>
/// Billboards come from TexGen output (or any mod shipping <c>textures\terrain\lodgen\…</c>). Every vanilla
/// <c>.btt</c> that we don't replace with our own gets an empty one, because its type numbers would point at the
/// wrong entries of our <c>.lst</c>. Rebuilt per worldspace only when its trees or billboards changed.
/// </para>
/// </summary>
public static class TreeLodGenerator
{
    private const int Version = 1;

    public static TreeGenerateStats Generate(
        IReadOnlyDictionary<string, LodGrid> grids,
        IReadOnlyList<TreeReference> trees,
        AssetIndex assets,
        string outputFolder,
        BuildManifest manifest,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        float brightness = 1f,
        IReadOnlySet<string>? objectLodWorlds = null)   // worldspaces whose trees are (partly) in object LOD: vanilla tree LOD must be emptied there even if no billboard tree is left
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        int written = 0, unchanged = 0, types = 0, instances = 0, blocks = 0, empty = 0, deleted = 0;
        var byWs = trees.GroupBy(t => t.Worldspace, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (ws, grid) in grids.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var list = byWs.TryGetValue(ws, out var l)
                ? l.Where(t => grid.IsInsideGrid(t.Cell)).OrderBy(t => t.FormKey, StringComparer.Ordinal).ToList()
                : [];

            var treeFolder = Path.Combine(outputFolder, "meshes", "terrain", ws, "trees");
            if (list.Count == 0)
            {
                // Every tree here is in object LOD (3D tree LOD, light-plugin trees): no .lst or atlas of ours, but the
                // vanilla .btt files would still draw the vanilla trees next to ours, so each gets an empty one.
                if (objectLodWorlds is not null && objectLodWorlds.Contains(ws))
                {
                    var vanillaBlocks = assets.EnumeratePaths(GamePath.Join("meshes", "terrain", ws, "trees") + "\\")
                        .Where(p => p.EndsWith(".btt", StringComparison.Ordinal))
                        .Select(p => Path.GetFileName(p))
                        .ToList();
                    var emptyHash = "empty v" + Version + "|" + string.Join("|", vanillaBlocks.Order(StringComparer.OrdinalIgnoreCase));
                    if (!manifest.Trees.TryGetValue(ws, out var prev) || prev != emptyHash
                        || vanillaBlocks.Any(n => !File.Exists(Path.Combine(treeFolder, n))))
                    {
                        var keepEmpty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var name in vanillaBlocks)
                        {
                            Write(Path.Combine(treeFolder, name), TreeLodFiles.EmptyBlock());
                            keepEmpty.Add(name);
                        }
                        empty += vanillaBlocks.Count;
                        deleted += DeleteOurFiles(outputFolder, ws, keepEmpty);
                        foreach (var rel in new[] { TreeLodFiles.ListPath(ws), TreeLodFiles.AtlasPath(ws) })
                        {
                            var f = BuildManifest.FullPath(outputFolder, rel);
                            try { if (File.Exists(f)) { File.Delete(f); deleted++; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                        manifest.Trees[ws] = emptyHash;
                        written++;
                        progress?.Report($"Tree LOD {ws}: all trees are in object LOD; {vanillaBlocks.Count:N0} vanilla tree LOD blocks emptied");
                    }
                    else unchanged++;
                    continue;
                }
                // Nothing to do; clean up what an earlier build wrote for this worldspace.
                if (manifest.Trees.Remove(ws))
                    deleted += DeleteOurFiles(outputFolder, ws, keep: null);
                continue;
            }

            // Vanilla / other mods' .btt files for this worldspace (our own output folder is not an input).
            var foreignBlocks = assets.EnumeratePaths(GamePath.Join("meshes", "terrain", ws, "trees") + "\\")
                .Where(p => p.EndsWith(".btt", StringComparison.Ordinal))
                .Select(p => Path.GetFileName(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var hash = Hash(list, foreignBlocks, assets, brightness);
            var listFile = BuildManifest.FullPath(outputFolder, TreeLodFiles.ListPath(ws));
            var atlasFile = BuildManifest.FullPath(outputFolder, TreeLodFiles.AtlasPath(ws));
            if (manifest.Trees.TryGetValue(ws, out var old) && old == hash && File.Exists(listFile) && File.Exists(atlasFile))
            {
                unchanged++;
                continue;
            }

            progress?.Report($"Tree LOD {ws}: {list.Count:N0} trees...");

            // Tree types = distinct billboards.
            var billboards = list.Select(t => t.Billboard.TexturePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var inputs = new List<TreeAtlasBuilder.Input>();
            foreach (var path in billboards)
            {
                if (!assets.TryOpen(path, out var st)) { warnings.Add($"{ws}: billboard {path} could not be read."); continue; }
                using (st)
                {
                    using var ms = new MemoryStream();
                    st.CopyTo(ms);
                    inputs.Add(new(path, ms.ToArray()));
                }
            }

            TreeAtlasBuilder.Output atlas;
            try
            {
                atlas = new TreeAtlasBuilder { Brightness = brightness }.Build(inputs, ct);
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"{ws}: {ex.Message}");
                continue;
            }
            foreach (var (k, err) in atlas.Errors) warnings.Add($"{ws}: billboard {k}: {err}");
            if (atlas.Reduced > 0)
                progress?.Report($"Tree LOD {ws}: {atlas.Reduced} of {atlas.Rects.Count} billboards were reduced to at most {atlas.SizeCap}px to fit the {atlas.Size}px atlas.");

            var typeIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var typeList = new List<TreeLodFiles.TreeType>();
            var firstBillboard = list.GroupBy(t => t.Billboard.TexturePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Billboard, StringComparer.OrdinalIgnoreCase);
            foreach (var path in billboards)
            {
                if (!atlas.Rects.TryGetValue(path, out var rect)) continue;
                var bb = firstBillboard[path];
                typeIndex[path] = typeList.Count;
                typeList.Add(new(typeList.Count, bb.Width, bb.Height, rect.U0, rect.V0, rect.U1, rect.V1));
            }

            var perBlock = new Dictionary<(int X, int Y), List<TreeLodFiles.Instance>>();
            int placed = 0;
            foreach (var t in list)
            {
                if (!typeIndex.TryGetValue(t.Billboard.TexturePath, out var ti)) continue;
                var o = grid.BlockOrigin(t.Cell, LodLevel.Lod4);
                if (!perBlock.TryGetValue((o.X, o.Y), out var bl)) perBlock[(o.X, o.Y)] = bl = [];
                var pos = t.Position with { Z = t.Position.Z + t.Billboard.ShiftZ * t.Scale };
                bl.Add(new(ti, pos, t.RotationZ, t.Scale, t.RuntimeFormId));
                placed++;
            }

            // Write everything.
            Write(listFile, TreeLodFiles.WriteList(typeList));
            Write(atlasFile, atlas.Dds);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ((x, y), inst) in perBlock)
            {
                var rel = TreeLodFiles.BlockPath(ws, x, y);
                Write(BuildManifest.FullPath(outputFolder, rel), TreeLodFiles.WriteBlock(inst));
                keep.Add(Path.GetFileName(rel));
                blocks++;
            }
            int emptyHere = 0;
            foreach (var name in foreignBlocks)
            {
                if (keep.Contains(name)) continue;
                Write(Path.Combine(treeFolder, name), TreeLodFiles.EmptyBlock());
                keep.Add(name);
                emptyHere++;
            }
            empty += emptyHere;
            deleted += DeleteOurFiles(outputFolder, ws, keep);

            manifest.Trees[ws] = hash;
            written++;
            types += typeList.Count;
            instances += placed;
            progress?.Report($"Tree LOD {ws}: {typeList.Count} types in a {atlas.Size}px atlas, {placed:N0} trees in {perBlock.Count:N0} blocks"
                             + (emptyHere > 0 ? $", {emptyHere:N0} vanilla blocks emptied" : ""));
        }

        return new TreeGenerateStats(written, unchanged, types, instances, blocks, empty, deleted, sw.Elapsed, warnings);
    }

    private static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Removes .btt files in our output for <paramref name="ws"/> that aren't in <paramref name="keep"/> (null = all, plus .lst and atlas).</summary>
    private static int DeleteOurFiles(string outputFolder, string ws, HashSet<string>? keep)
    {
        int n = 0;
        var dir = Path.Combine(outputFolder, "meshes", "terrain", ws, "trees");
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.btt"))
            {
                if (keep is not null && keep.Contains(Path.GetFileName(f))) continue;
                try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        if (keep is null)
            foreach (var rel in new[] { TreeLodFiles.ListPath(ws), TreeLodFiles.AtlasPath(ws) })
            {
                var f = BuildManifest.FullPath(outputFolder, rel);
                try { if (File.Exists(f)) { File.Delete(f); n++; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        return n;
    }

    private static string Hash(List<TreeReference> trees, HashSet<string> foreignBlocks, AssetIndex assets, float brightness)
    {
        var sb = new StringBuilder();
        sb.Append("trees v").Append(Version).Append(" b").Append(brightness.ToString("R")).Append('\n');
        foreach (var t in trees)
            sb.Append(t.FormKey).Append('|').Append(t.Position.X.ToString("R")).Append(',').Append(t.Position.Y.ToString("R")).Append(',')
              .Append(t.Position.Z.ToString("R")).Append('|').Append(t.RotationZ.ToString("R")).Append('|').Append(t.Scale.ToString("R"))
              .Append('|').Append(t.RuntimeFormId).Append('|').Append(t.Billboard).Append('\n');
        foreach (var p in trees.Select(t => t.Billboard.TexturePath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            sb.Append(p).Append('=').Append(assets.Fingerprint(p)).Append('\n');
        foreach (var b in foreignBlocks.Order(StringComparer.OrdinalIgnoreCase)) sb.Append(b).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
