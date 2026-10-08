using System.Diagnostics;
using AnvilLOD.Core.Incremental;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Plugins;

public sealed record ScanRequest(
    GameContextOptions Game,
    ScanOptions Scan,
    string? OutputFolder = null,          // used to diff against the last build's manifest
    string SettingsFingerprint = "default",
    IReadOnlyCollection<LodLevel>? Levels = null, // null = all levels the .lod file allows
    bool Generate = false,                         // also write the .bto files (needs OutputFolder)
    string? DynDolodFolder = null,                 // DynDOLOD install folder: its rule files are reused (optional)
    LodPreset Preset = LodPreset.High,
    bool RemoveBuried = true,                      // drop LOD triangles buried under the terrain (reads LAND)
    bool GrassLod = true,                          // grass LOD in LOD4 from the grass cache (grass\*.cgid)
    float GrassDensity = 0.08f,                    // share of cached grass instances kept as LOD quads
    float GrassSize = 1f,                          // size multiplier for the kept quads (fewer, bigger tufts)
    float TreeBrightness = 1f,                     // tree LOD billboard colour multiplier (DynDOLOD-style brightness)
    bool ChildWorlds = true);                      // copy walled-city child worldspaces into the parent's LOD (DynDOLOD Configs)

public sealed record ScanSummary(
    ScanStats Stats,
    int ArchivesIndexed,
    int ArchivedFiles,
    TimeSpan IndexTime,
    TimeSpan BucketAndHashTime,
    IReadOnlyDictionary<string, LodSettings> Grids,
    IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> Quads,
    IReadOnlyDictionary<QuadKey, string> QuadHashes,
    BuildPlan Plan,
    long SkippedOutsideGrid,
    int PluginsLoaded,
    IReadOnlyList<string> MissingPlugins,
    IReadOnlyList<string> Warnings,
    GenerateStats? Generation = null,
    int StaleFilesDeleted = 0,
    int TreeReferences = 0,
    IReadOnlyDictionary<string, long>? MissingBillboards = null,
    TreeGenerateStats? Trees = null,
    int GrassCells = 0,
    IReadOnlyDictionary<string, long>? MissingGrassBillboards = null,
    IReadOnlySet<string>? SkippedByTexGen = null,
    int DynamicRefs = 0);   // keys of MissingBillboards / MissingGrassBillboards that TexGen left out on purpose (too small)

/// <summary>
/// Load order → asset index → reference scan → quad buckets → hashes → build plan,
/// and (when <see cref="ScanRequest.Generate"/> is set) writing the changed .bto files.
/// Shared by the CLI and the WPF app so both behave identically.
/// </summary>
public static class ScanPipeline
{
    public static ScanSummary Run(ScanRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (req.Generate && string.IsNullOrWhiteSpace(req.OutputFolder))
            throw new ArgumentException("Choose an output folder before generating LOD.");

        // Every run is also logged to <output>\AnvilLOD.log (Generate only, so a Scan never touches the output folder).
        using var log = new FileLogProgress(req.Generate ? Path.Combine(req.OutputFolder!, "AnvilLOD.log") : null, progress,
            $"AnvilLOD {typeof(ScanPipeline).Assembly.GetName().Version?.ToString(3)} generate");
        try
        {
            return RunLogged(req, log, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Report("ERROR: " + ex);
            throw;
        }
    }

    private static ScanSummary RunLogged(ScanRequest req, IProgress<string>? progress, CancellationToken ct)
    {

        progress?.Report("Opening load order...");
        var gameOptions = req.OutputFolder is null ? req.Game : req.Game with { ExcludeFolder = req.OutputFolder };
        using var game = GameContext.Open(gameOptions, progress);
        progress?.Report($"Source: {game.SourceDescription}  ({game.PluginsLoaded:N0} of {game.PluginsListed:N0} plugins loaded)");
        var warnings = new List<string>(game.Warnings);
        foreach (var w in game.Warnings) progress?.Report("WARNING: " + w);

        progress?.Report("Indexing BSAs (meshes, LOD settings, DynDOLOD rules, tree billboards)...");
        var assets = game.BuildAssets(
            p => p.StartsWith("meshes\\", StringComparison.Ordinal) || p.StartsWith("lodsettings\\", StringComparison.Ordinal)
                 || p.StartsWith("dyndolod\\", StringComparison.Ordinal) || p.StartsWith("textures\\terrain\\lodgen\\", StringComparison.Ordinal)
                 || p.StartsWith("grass\\", StringComparison.Ordinal)
                 || (req.GrassLod && p.StartsWith("textures\\", StringComparison.Ordinal))); // grass model textures, for rendered grass billboards
        progress?.Report($"Indexed {assets.ArchivedFileCount:N0} files from {assets.ArchiveCount} archives in {assets.IndexTime.TotalSeconds:F1}s");
        if (assets.ArchiveCount == 0)
            Warn("No BSA archives were found. Vanilla LOD meshes and LOD settings live in BSAs, so check the Data folder / MO2 instance path.");

        // LOD meshes found by file name (name_lod_N.nif) + DynDOLOD-format rules from the DynDOLOD install and Data\DynDOLOD
        var lodIndex = LodMeshIndex.Build(assets.EnumeratePaths("meshes\\").Where(p => p.Contains("_lod", StringComparison.Ordinal)));
        var dataRuleFiles = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assets.EnumeratePaths("dyndolod\\"))
        {
            var rest = path["dyndolod\\".Length..];
            if (rest.Contains('\\') || !rest.EndsWith(".ini", StringComparison.Ordinal)) continue;
            var p = path;
            dataRuleFiles[rest] = () =>
            {
                if (!assets.TryOpen(p, out var st)) return "";
                using var reader = new StreamReader(st);
                return reader.ReadToEnd();
            };
        }
        // Some mods ship their rule file in the Data root instead of Data\DynDOLOD (BIRDS does). DynDOLOD would
        // skip those; the mod author clearly meant them to be used, so they're read too (Data\DynDOLOD wins).
        var misplaced = new List<string>();
        foreach (var (name, full) in game.RootFiles())
        {
            if (!name.StartsWith("dyndolod_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (dataRuleFiles.ContainsKey(name)) continue;
            var f = full;
            dataRuleFiles[name] = () => File.ReadAllText(f);
            misplaced.Add(name);
        }
        if (misplaced.Count > 0)
            progress?.Report($"Rule files found in the Data root instead of Data\\DynDOLOD (used anyway): {string.Join(", ", misplaced)}");
        var rulesFolder = ResolveRulesFolder(req.DynDolodFolder);
        var plugins = game.LoadOrder.ListedOrder.Where(l => l.Mod is not null).Select(l => l.ModKey.ToString()).ToList();
        var rules = LodRules.Load(rulesFolder, dataRuleFiles, plugins, req.Preset);
        if (rulesFolder is not null)
        {
            var worldIgnore = Path.Combine(Path.GetDirectoryName(rulesFolder)!, "Configs", "DynDOLOD_SSE_mod_world_ignore.txt");
            if (File.Exists(worldIgnore)) rules.AddModWorldIgnore(File.ReadAllText(worldIgnore));
        }
        progress?.Report($"LOD meshes by name: {lodIndex.Count:N0} objects; rules: {rules.Count:N0} from {rules.Files.Count} files ({req.Preset} preset"
                         + (rulesFolder is null ? ", no DynDOLOD install set: mod rule files and defaults only)" : ")"));
        Dictionary<string, string>? meshLookup = null;
        if (rulesFolder is not null)
        {
            var lookupFile = Path.Combine(Path.GetDirectoryName(rulesFolder)!, "Configs", "DynDOLOD_SSE_mesh_lookup.txt");
            if (File.Exists(lookupFile)) meshLookup = LodMeshResolver.ParseMeshLookup(File.ReadAllText(lookupFile));
        }
        var resolver = new LodMeshResolver(lodIndex, rules, assets.Exists, meshLookup);

        // Walled cities: DynDOLOD's child world configs say which child worldspaces are copied into the parent's LOD.
        var childCopies = ChildWorldCopies.None;
        if (req.ChildWorlds && rulesFolder is not null)
        {
            var configs = Path.Combine(Path.GetDirectoryName(rulesFolder)!, "Configs");
            childCopies = ChildWorldCopies.Load(configs, plugins, req.Preset);
            if (childCopies.Worlds.Count > 0)
                progress?.Report($"Child worldspaces copied into their parent's LOD: {string.Join(", ", childCopies.Worlds.Select(w => $"{w.Child} -> {w.Parent}"))}");
        }
        var scanner = new MutagenReferenceScanner(game, assets, resolver, childCopies);
        var scan = scanner.Scan(req.Scan, progress, ct);
        if (scan.Grids.Count == 0)
            Warn(req.Scan.Worldspaces is { Count: > 0 } ws
                ? $"None of the requested worldspaces ({string.Join(", ", ws)}) has a LODSettings\\<name>.lod file in loose files or BSAs."
                : "No worldspace has a LODSettings\\<name>.lod file in loose files or BSAs.");
        progress?.Report($"Scan: {scan.Stats.LodReferencesFound:N0} LOD refs from {scan.Stats.PlacedObjectsVisited:N0} refs in {scan.Stats.Elapsed.TotalSeconds:F1}s");

        TerrainReader.Result? terrain = null;
        if (req.RemoveBuried && scan.Grids.Count > 0)
        {
            progress?.Report("Reading terrain heights (LAND)...");
            terrain = TerrainReader.Read(game, scan.Grids.Keys.ToList(), ct);
            progress?.Report($"Terrain: {terrain.Heights.CellCount:N0} cells in {terrain.Elapsed.TotalSeconds:F1}s");
        }

        GrassLodSource? grass = null;
        IReadOnlyList<LodReference> allRefs = scan.References;
        if (req.GrassLod && scan.Grids.Count > 0 && (req.Levels is not { Count: > 0 } || req.Levels.Contains(LodLevel.Lod4)))
        {
            grass = new GrassLodSource(game, assets, req.GrassDensity, req.GrassSize, req.Generate ? req.OutputFolder : null);
            var grassRefs = grass.Discover(scan.Grids);
            progress?.Report($"Grass cache: {grassRefs.Count:N0} cells with cached grass");
            if (grassRefs.Count == 0)
                Warn("Grass LOD is on, but no grass cache (grass\\<worldspace>x…y….cgid) was found. Generate one with NGIO or FasterNGIO.");
            allRefs = [.. scan.References, .. grassRefs];
        }
        // Trees from light (ESL) plugins: the engine never hides their .btt tree LOD, so they become billboard
        // cards in object LOD instead, which hides per cell.
        TreeCardSource? cards = null;
        IReadOnlyList<TreeReference>? bttTrees = scan.Trees;
        if (req.Scan.TreeLod && scan.Trees is { Count: > 0 } allTrees)
        {
            var light = allTrees.Where(TreeCardSource.NeedsCards).ToList();
            if (light.Count > 0)
            {
                cards = new TreeCardSource(assets);
                allRefs = [.. allRefs, .. cards.ToReferences(light)];
                bttTrees = allTrees.Where(t => !TreeCardSource.NeedsCards(t)).ToList();
                progress?.Report($"Tree LOD: {light.Count:N0} trees from light (ESL) plugins go into object LOD as billboard cards (the engine can't hide their tree LOD)");
            }
        }
        string? Fingerprint(string p) => grass?.Fingerprint(p) ?? cards?.Fingerprint(p) ?? assets.Fingerprint(p);

        var sw = Stopwatch.StartNew();
        var bucketer = new QuadBucketer(scan.Grids);
        Parallel.ForEach(allRefs, new ParallelOptions { CancellationToken = ct }, bucketer.Add);
        var quads = bucketer.Build();
        if (req.Levels is { Count: > 0 } levels)
            quads = quads.Where(kv => levels.Contains(kv.Key.Level)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<QuadKey, string>();
        Parallel.ForEach(quads, new ParallelOptions { CancellationToken = ct }, kv =>
            hashes[kv.Key] = QuadHasher.Hash(kv.Key, kv.Value, Fingerprint,
                req.SettingsFingerprint + (terrain is null ? "|keep-buried"
                    : "|terrain:" + (terrain.Fingerprints.TryGetValue(kv.Key.Worldspace, out var tf) ? tf : "none"))));
        sw.Stop();

        var manifest = req.OutputFolder is null ? new BuildManifest() : BuildManifest.LoadOrEmpty(req.OutputFolder);
        var scopeWorldspaces = scan.Grids.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool InScope(string relPath) =>
            BuildManifest.TryParseBlockPath(relPath, out var ws, out var lv)
            && scopeWorldspaces.Contains(ws)
            && (req.Levels is not { Count: > 0 } || req.Levels.Contains((LodLevel)lv));
        var plan = manifest.Plan(hashes, req.OutputFolder, InScope);

        GenerateStats? gen = null;
        TreeGenerateStats? trees = null;
        int deleted = 0;
        // TexGen lists every tree/grass it considered; the ones listed without a billboard were skipped on
        // purpose (small shrubs, thickets, rock grass). DynDOLOD gives those no LOD either, so that's not an error.
        var texGenListed = ReadTexGenLists(assets);
        var skippedByTexGen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Classify(IReadOnlyDictionary<string, long>? missing, string what, string fix)
        {
            if (missing is not { Count: > 0 }) return;
            var real = new List<KeyValuePair<string, long>>();
            long skippedPlaced = 0;
            foreach (var kv in missing)
            {
                if (texGenListed.Contains(TexGenKey(kv.Key))) { skippedByTexGen.Add(kv.Key); skippedPlaced += kv.Value; }
                else real.Add(kv);
            }
            int skipped = missing.Count - real.Count;
            if (skipped > 0)
                progress?.Report($"{skipped} small {what} types ({skippedPlaced:N0} placed) get no LOD because TexGen skips them as too small (DynDOLOD does the same).");
            if (real.Count > 0)
                Warn($"{real.Count} {what} types ({real.Sum(kv => kv.Value):N0} placed) have no billboard, and TexGen hasn't seen them. {fix} First: "
                     + string.Join(", ", real.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key)) + ".");
        }
        Classify(scan.MissingBillboards, "tree", "Re-run TexGen with the current load order.");
        if (req.Generate)
        {
            var output = req.OutputFolder!;
            progress?.Report($"Generating {plan.Rebuild.Count:N0} blocks ({plan.Unchanged.Count:N0} unchanged, skipped) into {output}...");
            var generator = new LodGenerator(assets, terrain?.Heights, new CompositeSyntheticSource(grass, cards));
            gen = generator.Generate(quads, plan.Rebuild, output, progress, ct);

            foreach (var stale in plan.StaleFiles)
            {
                try
                {
                    var full = BuildManifest.FullPath(output, stale);
                    if (File.Exists(full)) { File.Delete(full); deleted++; }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            var failed = gen.BlockErrors.Keys.ToHashSet();
            manifest.Commit(hashes, plan.Rebuild.Where(q => !failed.Contains(q)), failed, plan.StaleFiles);

            if (req.Scan.TreeLod && bttTrees is { } treeRefs)
            {
                trees = TreeLodGenerator.Generate(scan.Grids, treeRefs, assets, output, manifest, progress, ct, req.TreeBrightness);
                foreach (var w in trees.Warnings) Warn(w);
                progress?.Report($"Tree LOD: {trees.Instances:N0} trees, {trees.TreeTypes} types, {trees.Blocks:N0} blocks"
                                 + $" ({trees.WorldspacesWritten} worldspaces written, {trees.WorldspacesUnchanged} unchanged) in {trees.Elapsed.TotalSeconds:F1}s");
            }
            if (req.Scan.DynamicLod)
            {
                int n = DynamicLodWriter.Write(output, scan.Dynamic ?? []);
                progress?.Report($"Dynamic LOD: {n:N0} switchable references written to {DynamicLodWriter.RelativePath} for the AnvilLOD SKSE plugin");
            }
            else
            {
                var dyn = BuildManifest.FullPath(output, DynamicLodWriter.RelativePath);
                if (File.Exists(dyn)) File.Delete(dyn);
            }
            manifest.Save(output);

            progress?.Report($"Generated {gen.BlocksWritten:N0} blocks, {gen.Triangles:N0} triangles ({gen.TrianglesCulled:N0} buried ones removed), {gen.MeshesLoaded:N0} meshes in {gen.Elapsed.TotalSeconds:F1}s"
                             + (deleted > 0 ? $"; removed {deleted} stale blocks" : ""));
            if (gen.BlocksFailed > 0) Warn($"{gen.BlocksFailed} blocks failed to write (first: {gen.BlockErrors.First().Key}: {gen.BlockErrors.First().Value}).");
            if (grass is not null)
            {
                if (grass.TypesFromModelTexture > 0)
                    progress?.Report(grass.BillboardsRendered > 0
                        ? $"Grass LOD: {grass.TypesFromModelTexture} grass types have no TexGen billboard; {grass.BillboardsRendered} billboards were rendered from their models (textures\\anvillod\\grass)."
                        : $"Grass LOD: {grass.TypesFromModelTexture} grass types have no TexGen billboard and use their own model texture instead.");
                Classify(grass.MissingBillboards, "grass", "Re-run TexGen with grass billboards enabled.");
            } 
            if (gen.MeshErrors.Count > 0) Warn($"{gen.MeshErrors.Count} LOD meshes could not be used; see the Missing meshes tab.");
        }

        return new ScanSummary(
            scan.Stats,
            assets.ArchiveCount,
            assets.ArchivedFileCount,
            assets.IndexTime,
            sw.Elapsed,
            scan.Grids.ToDictionary(kv => kv.Key, kv => kv.Value.Settings, StringComparer.OrdinalIgnoreCase),
            quads,
            hashes,
            plan,
            bucketer.SkippedOutsideGrid,
            game.PluginsLoaded,
            game.MissingPlugins,
            warnings,
            gen,
            deleted,
            scan.Trees?.Count ?? 0,
            scan.MissingBillboards,
            trees,
            grass?.CellsFound ?? 0,
            grass?.MissingBillboards.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            skippedByTexGen,
            scan.Dynamic?.Count ?? 0);

        // "plugin.esm\\model_000a7329" -> "plugin.esm;000a7329"
        static string TexGenKey(string missingKey)
        {
            int slash = missingKey.IndexOf('\\');
            int us = missingKey.LastIndexOf('_');
            return slash < 0 || us < slash ? missingKey : $"{missingKey[..slash]};{missingKey[(us + 1)..]}";
        }

        // TexGen_SSE_Tree_Billboards.txt / TexGen_SSE_Grass_Billboards.txt: "plugin;FORMID;EditorID[,Complex]"
        static HashSet<string> ReadTexGenLists(AssetIndex assets)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in assets.EnumeratePaths("textures\\terrain\\lodgen\\"))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith("texgen_", StringComparison.Ordinal) || !name.EndsWith("_billboards.txt", StringComparison.Ordinal)) continue;
                if (!assets.TryOpen(path, out var st)) continue;
                using var reader = new StreamReader(st);
                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split(';');
                    if (parts.Length >= 2) set.Add($"{parts[0].Trim()};{parts[1].Trim()}");
                }
            }
            return set;
        }

        static string? ResolveRulesFolder(string? dyndolod)
        {
            if (string.IsNullOrWhiteSpace(dyndolod)) return null;
            foreach (var candidate in new[]
            {
                Path.Combine(dyndolod, "Edit Scripts", "DynDOLOD", "Rules"),
                Path.Combine(dyndolod, "DynDOLOD", "Rules"),
                Path.Combine(dyndolod, "Rules"),
                dyndolod,
            })
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "DynDOLOD_*.ini").Any()) return candidate;
            return null;
        }

        void Warn(string w)
        {
            warnings.Add(w);
            progress?.Report("WARNING: " + w);
        }
    }
}
