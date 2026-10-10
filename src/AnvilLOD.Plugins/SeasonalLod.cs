using System.Runtime.InteropServices;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// EXPERIMENTAL Seasons of Skyrim support: for every season that has form-swap INIs (<c>Data\Seasons\*_WIN.ini</c>, …),
/// writes a full seasonal object LOD set (<c>&lt;World&gt;.&lt;L&gt;.&lt;X&gt;.&lt;Y&gt;.WIN.bto</c>). Blocks with a swapped object are
/// rebuilt with the swap's LOD meshes (or without it, if the swap has none); every other block is a hard link (or
/// copy) of the normal one. Seasons without INIs get no files, so Seasons of Skyrim falls back to the normal LOD.
/// Tree LOD and terrain LOD are left to their defaults (no seasonal .btt/.lst/.btr is written).
/// </summary>
public static class SeasonalLod
{
    public sealed record Result(int Seasons, int Rebuilt, int Linked, IReadOnlyList<string> Messages);

    public static Result Build(
        GameContext game, IAssetSource assets, Func<string, IEnumerable<string>> enumerate, LodMeshResolver resolver,
        IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> quads, LodGenerator generator, string outputFolder,
        IProgress<string>? progress, CancellationToken ct, GrassLodSource? grass = null)
    {
        var messages = new List<string>();
        var files = enumerate("seasons\\")
            .Where(p => p.EndsWith(".ini", StringComparison.Ordinal) && SeasonSwaps.SeasonOf(Path.GetFileName(p)) is not null)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase) // Seasons of Skyrim reads the folder in name order; later wins
            .ToList();
        var scopes = Scopes(quads.Keys);
        int seasons = 0, rebuilt = 0, linked = 0;

        foreach (var (suffix, name) in SeasonSwaps.Seasons)
        {
            ct.ThrowIfCancellationRequested();
            RemoveSeasonFiles(outputFolder, scopes, suffix);
            var seasonFiles = files.Where(f => SeasonSwaps.SeasonOf(Path.GetFileName(f)) == suffix).ToList();
            bool seasonalGrass = grass?.HasSeason(suffix) == true; // grass\<ws>x..y...<SUF>.cgid from Grass Cache Helper
            if (seasonFiles.Count == 0 && !seasonalGrass) continue;

            // Base FormKey → replacement LOD meshes (null = the swap has no object LOD: drop the reference).
            var swaps = new Dictionary<string, LodMeshSet?>(StringComparer.OrdinalIgnoreCase);
            int unresolved = 0;
            foreach (var f in seasonFiles)
            {
                if (!assets.TryOpen(f, out var st)) continue;
                string text;
                using (var r = new StreamReader(st)) text = r.ReadToEnd();
                foreach (var s in SeasonSwaps.Parse(text))
                {
                    if (!SeasonSwaps.ObjectSections.Contains(s.Section)) continue;
                    var baseKey = Resolve(game, s.Section, s.Base);
                    var swapKey = Resolve(game, s.Section, s.Swap);
                    if (baseKey is null || swapKey is null) { unresolved++; continue; }
                    if (baseKey == swapKey) continue;
                    swaps[baseKey.Value.ToString()] = MeshesFor(game, resolver, swapKey.Value);
                }
            }
            if (swaps.Count == 0 && !seasonalGrass)
            {
                messages.Add($"Seasons ({name}): {seasonFiles.Count} INI file(s) but no object swaps"
                    + (unresolved > 0 ? $" ({unresolved} entries name forms that aren't loaded)" : "") + "; no seasonal LOD written.");
                continue;
            }

            // Which blocks change this season.
            var changed = new Dictionary<QuadKey, IReadOnlyList<LodReference>>();
            foreach (var (quad, refs) in quads)
            {
                bool any = false;
                var seasonal = new List<LodReference>(refs.Count);
                foreach (var r in refs)
                {
                    if (swaps.TryGetValue(r.BaseFormKey, out var m))
                    {
                        any = true;
                        if (m is not null) seasonal.Add(r with { Meshes = m });   // swap without LOD: left out
                    }
                    else if (seasonalGrass && grass!.SeasonalVariant(r, suffix) is { } g) { any = true; seasonal.Add(g); }
                    else seasonal.Add(r);
                }
                if (any) changed[quad] = seasonal;
            }

            var gen = generator.Generate(changed, changed.Keys.ToList(), outputFolder, progress, ct, suffix);
            rebuilt += gen.BlocksWritten;

            // Everything else: the normal block under the seasonal name.
            foreach (var quad in quads.Keys)
            {
                if (changed.ContainsKey(quad)) continue;
                var src = BuildManifestPath(outputFolder, quad.RelativePath);
                if (!File.Exists(src)) continue;
                LinkOrCopy(src, BuildManifestPath(outputFolder, SeasonSwaps.SeasonalPath(quad.RelativePath, suffix)));
                linked++;
            }
            seasons++;
            messages.Add($"Seasons ({name}): {swaps.Count:N0} swapped object types from {seasonFiles.Count} INI file(s)" + (seasonalGrass ? ", seasonal grass cache" : "") + $", {changed.Count:N0} blocks rebuilt"
                + (unresolved > 0 ? $", {unresolved} entries skipped (forms not loaded)" : ""));
        }
        foreach (var m in messages) progress?.Report(m);
        return new Result(seasons, rebuilt, linked, messages);
    }

    private static string BuildManifestPath(string output, string rel) => Path.Combine(output, rel.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>Deletes every seasonal block AnvilLOD wrote (used when seasons are turned off).</summary>
    public static void RemoveAll(string output, IEnumerable<QuadKey> quads)
    {
        var scopes = Scopes(quads);
        foreach (var (suffix, _) in SeasonSwaps.Seasons) RemoveSeasonFiles(output, scopes, suffix);
    }

    /// <summary>(worldspace, level) pairs in this run, so a run limited to some levels leaves the others' files alone.</summary>
    private static List<(string Ws, int Level)> Scopes(IEnumerable<QuadKey> quads) =>
        quads.Select(q => (q.Worldspace, (int)q.Level)).Distinct().ToList();

    private static void RemoveSeasonFiles(string output, IEnumerable<(string Ws, int Level)> scopes, string suffix)
    {
        foreach (var (ws, level) in scopes)
        {
            var dir = Path.Combine(output, "meshes", "terrain", ws, "objects");
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, $"{ws}.{level}.*.{suffix}.bto"))
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static FormKey? Resolve(GameContext game, string section, SeasonFormRef f)
    {
        if (f.Plugin is not null)
            return new FormKey(ModKey.FromFileName(f.Plugin), f.LocalId);
        var edid = f.EditorId!;
        var cache = game.LinkCache;
        return section.ToLowerInvariant() switch
        {
            "statics" => cache.TryResolve<IStaticGetter>(edid, out var a) ? a.FormKey : null,
            "movablestatics" => cache.TryResolve<IMoveableStaticGetter>(edid, out var b) ? b.FormKey : null,
            "activators" => cache.TryResolve<IActivatorGetter>(edid, out var c) ? c.FormKey : null,
            "furniture" => cache.TryResolve<IFurnitureGetter>(edid, out var d) ? d.FormKey : null,
            _ => (FormKey?)null,
        };
    }

    /// <summary>LOD meshes for a (swap) base object, the same way the scanner resolves them.</summary>
    private static LodMeshSet? MeshesFor(GameContext game, LodMeshResolver resolver, FormKey fk)
    {
        if (!game.LinkCache.TryResolve(fk, out var rec)) return null;
        if (rec is not (IStaticGetter or IMoveableStaticGetter or IActivatorGetter or IFurnitureGetter or IDoorGetter or IContainerGetter)) return null;
        var model = (rec as IModeledGetter)?.Model?.File;
        if (model is null || model.IsNull) return null;
        string?[]? mnam = null;
        if (rec is IStaticGetter { Lod: { } lod })
        {
            static string? P(Mutagen.Bethesda.Plugins.Assets.AssetLinkGetter<Mutagen.Bethesda.Skyrim.Assets.SkyrimModelAssetType> l) =>
                l.IsNull ? null : l.DataRelativePath.Path;
            mnam = [P(lod.Level0), P(lod.Level1), P(lod.Level2), P(lod.Level3)];
        }
        var res = resolver.Resolve(null, LodRules.FormIdKey(fk.ModKey.ToString(), fk.ID), model.DataRelativePath.Path, mnam);
        return res.Meshes.HasAny ? res.Meshes : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    /// <summary>Hard link where possible (no extra disk space), else a copy.</summary>
    private static void LinkOrCopy(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(dst)) File.Delete(dst);
        if (OperatingSystem.IsWindows())
        {
            try { if (CreateHardLinkW(dst, src, IntPtr.Zero)) return; } catch (EntryPointNotFoundException) { }
        }
        File.Copy(src, dst, overwrite: true);
    }
}
