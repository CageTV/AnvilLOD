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
    float ObjectBrightness = 1f,                   // object LOD colour multiplier (vertex colours; textures untouched)
    bool Seasons = false,                          // EXPERIMENTAL: Seasons of Skyrim seasonal object LOD (<block>.WIN.bto, …)
    bool ChildWorlds = true,                       // copy walled-city child worldspaces into the parent's LOD (DynDOLOD Configs)
    SkseDllChoice SkseDll = SkseDllChoice.Auto,    // which SKSE plugin build Generate puts in the output (None = installed separately)
    bool Underside = false,                        // terrain underside for volumetric lighting mods: <ws>_Underside.nif + AnvilLOD.esp (ESL)
    int UndersideStep = UndersideMesher.DefaultStep, // LAND vertices per underside quad (smaller = finer and heavier)
    bool PbrLod = false,                           // object LOD textures that match PBR full models (TexGen pbr_lod twins, converted PBR albedo)
    float PbrLodBrightness = 1f,                   // multiplier on DynDOLOD's default PBR scale (0.65) for the converted copies
    int PbrLodSize = 512,                          // largest side of a converted copy
    bool LargeReferences = false,                  // list missing large references in AnvilLOD.esm, for the engine's large reference grid
    bool LargeRefsEsl = true,                      // flag AnvilLOD.esm as ESL (no plugin slot); off = a normal ESM that uses a slot
    bool Candles = false,                          // also load DynDOLOD's Candles rules (candle/lantern/sconce lights get Far LOD)
    bool FxGlow = false,                           // also load DynDOLOD's FXGlow rules (fire and light glow cards get Far LOD)
    string? CustomRulesFile = null,                // the user's own rule file: its rules come first (null = DynDOLOD's own files only)
    bool CleanOutput = true);                      // Generate empties the output folder first (only if it is AnvilLOD's own, see OutputFolder), so old files never mix with new ones

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
    int DynamicRefs = 0,
    Tree3DStats? Tree3D = null);   // keys of MissingBillboards / MissingGrassBillboards that TexGen left out on purpose (too small)

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

        // The output folder is made and, unless told not to, emptied before the log is opened in it.
        var prepared = req.Generate ? OutputFolder.Prepare(req.OutputFolder!, req.CleanOutput) : null;

        // Every run is also logged to <output>\AnvilLOD.log (Generate only, so a Scan never touches the output folder).
        using var log = new FileLogProgress(req.Generate ? Path.Combine(req.OutputFolder!, "AnvilLOD.log") : null, progress,
            $"AnvilLOD {typeof(ScanPipeline).Assembly.GetName().Version?.ToString(3)} generate");
        if (prepared is not null)
        {
            if (prepared.Created) log.Report($"Output folder created: {req.OutputFolder}");
            if (prepared.Cleared && prepared.Deleted > 0) log.Report($"Output folder cleared ({prepared.Deleted:N0} old files removed), so nothing stale is left: {req.OutputFolder}");
            if (prepared.Warning is not null) log.Report("WARNING: " + prepared.Warning);
        }
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
                 || p.StartsWith("grass\\", StringComparison.Ordinal) || p.StartsWith("seasons\\", StringComparison.Ordinal)
                 || ((req.GrassLod || req.Scan.Tree3D is { Enabled: true }) && p.StartsWith("textures\\", StringComparison.Ordinal))); // grass model textures (rendered grass billboards), and the textures 3D tree LOD models need
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
        var ruleOptions = new LodRuleOptions(req.Candles, req.FxGlow, string.IsNullOrWhiteSpace(req.CustomRulesFile) ? null : req.CustomRulesFile);
        if (ruleOptions.CustomFile is not null && !File.Exists(ruleOptions.CustomFile))
            throw new FileNotFoundException($"The custom LOD rules file was not found: {ruleOptions.CustomFile}. Untick \"Use custom rules\" or pick the file again.", ruleOptions.CustomFile);
        if ((req.Candles || req.FxGlow) && rulesFolder is null)
            progress?.Report("Candles / FXGlow need the DynDOLOD folder (their rule files are part of DynDOLOD); nothing was added.");
        var rules = LodRules.Load(rulesFolder, dataRuleFiles, plugins, req.Preset, "SSE", ruleOptions);
        if (rulesFolder is not null)
        {
            var worldIgnore = Path.Combine(Path.GetDirectoryName(rulesFolder)!, "Configs", "DynDOLOD_SSE_mod_world_ignore.txt");
            if (File.Exists(worldIgnore)) rules.AddModWorldIgnore(File.ReadAllText(worldIgnore));
        }
        progress?.Report($"LOD meshes by name: {lodIndex.Count:N0} objects; rules: {rules.Count:N0} from {rules.Files.Count} files ({req.Preset} preset{(ruleOptions.Candles ? " + Candles" : "")}{(ruleOptions.FxGlow ? " + FXGlow" : "")}{(ruleOptions.CustomFile is null ? "" : " + custom rules")}"
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
        if ((req.RemoveBuried || req.Underside) && scan.Grids.Count > 0)
        {
            progress?.Report("Reading terrain heights (LAND)...");
            terrain = TerrainReader.Read(game, scan.Grids.Keys.ToList(), ct);
            progress?.Report($"Terrain: {terrain.Heights.CellCount:N0} cells in {terrain.Elapsed.TotalSeconds:F1}s");
        }
        // Only the buried-triangle test (and the block hashes it feeds) depends on RemoveBuried; the underside reads the same heights.
        var cullTerrain = req.RemoveBuried ? terrain : null;

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
        // Trees that go into object LOD instead of the billboard tree LOD files:
        //  - 3D tree LOD (opt-in): trees with a 3D model (DynDOLOD's passthru_lod.nif): the model at LOD4 (and LOD8), cards beyond.
        //  - Trees from light (ESL) plugins: the engine never hides their .btt tree LOD, so they become billboard
        //    cards instead, which hide per cell like any other object LOD.
        TreeCardSource? cards = null;
        Tree3DSource? tree3d = null;
        Tree3DStats? tree3dStats = null;
        IReadOnlyList<TreeReference>? bttTrees = scan.Trees;
        var objectLodWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (req.Scan.TreeLod && scan.Trees is { Count: > 0 } allTrees)
        {
            var in3d = new HashSet<TreeReference>(ReferenceEqualityComparer.Instance);
            var t3 = req.Scan.Tree3D ?? Tree3DSettings.Off;
            if (t3.Enabled)
            {
                tree3d = new Tree3DSource(assets, req.TreeBrightness);
                var withModel = allTrees.Where(t => t.Model3D is not null).ToList();
                var reasons = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(withModel.Select(t => t.Model3D!).Distinct(StringComparer.OrdinalIgnoreCase), new ParallelOptions { CancellationToken = ct },
                    m => reasons[m] = tree3d.Validate(m));
                var t3Warnings = new List<string>();
                var skippedTypes = reasons.Where(kv => kv.Value is not null).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
                long skippedTrees = 0;
                foreach (var t in withModel)
                {
                    if (reasons[t.Model3D!] is not null) { skippedTrees++; continue; }
                    in3d.Add(t);
                }
                if (skippedTypes.Count > 0)
                {
                    var missingTex = skippedTypes.Count(kv => kv.Value!.StartsWith("missing texture", StringComparison.Ordinal));
                    var first = skippedTypes.First();
                    Warn($"3D tree LOD: {skippedTypes.Count} models ({skippedTrees:N0} trees) can't be used and keep billboard tree LOD"
                         + (missingTex > 0 ? $"; {missingTex} are missing textures (trunk billboards are made by TexGen from the model's _trunk.nif in DynDOLOD\\Render; run TexGen with it)" : "")
                         + $". First: {Path.GetFileName(first.Key)}: {first.Value}.");
                }
                int byName = in3d.Where(t => t.Model3DByName).Select(t => t.Model3D!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int types = in3d.Select(t => t.Model3D!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                int nameOnly = scanner.Tree3DNameOnlyAvailable;
                long t3Tris = 0, t3Bytes = 0;
                foreach (var g in in3d.GroupBy(t => t.Model3D!, StringComparer.OrdinalIgnoreCase))
                {
                    var (v, tr) = tree3d.Size(g.Key);
                    t3Tris += (long)tr * g.Count();
                    t3Bytes += ((long)v * 32 + (long)tr * 6) * g.Count();
                }
                int t3Levels = t3.Lod8 ? 2 : 1;
                tree3dStats = new Tree3DStats(in3d.Count, types, types - byName, byName, skippedTypes.Count, skippedTrees, nameOnly, t3Warnings,
                    t3Tris, t3Bytes / 1048576.0);
                progress?.Report($"3D tree LOD: {in3d.Count:N0} of {allTrees.Count:N0} trees use a 3D model ({types} models: {types - byName} matched by CRC32, {byName} by name; "
                                 + (t3.Lod8 ? "LOD4 and LOD8" : "LOD4 only") + "), billboard cards further out");
                if (in3d.Count > 0)
                    progress?.Report($"3D tree LOD: the models add about {t3Tris * t3Levels / 1e6:F1} million triangles and {t3Bytes * t3Levels / 1048576.0:F0} MB to the LOD blocks"
                                     + (t3.Lod8 ? "" : " (turning on LOD8 would roughly double that)")
                                     + ". Heavy models are the usual cause of huge LOD files; a lighter model set or fewer levels keeps them small.");
                if (nameOnly > 0)
                    progress?.Report($"3D tree LOD: {nameOnly} tree types have a 3D model under the plain tree name that doesn't match their mesh (changed by a mod or patcher). "
                                     + "Turn on \"accept models by name\" to use them.");
                if (allTrees.Count > 0 && in3d.Count == 0 && withModel.Count == 0)
                    progress?.Report("3D tree LOD: no tree has a matching 3D model (meshes\\DynDOLOD\\lod\\trees\\<tree>_<CRC32>passthru_lod.nif). Install a 3D tree LOD resource such as DynDOLOD Resources or Happy Little Trees' 3D LOD add-on.");
            }

            var light = allTrees.Where(t => TreeCardSource.NeedsCards(t) && !in3d.Contains(t)).ToList();
            if (light.Count > 0 || in3d.Count > 0) cards = new TreeCardSource(assets, req.TreeBrightness);
            if (in3d.Count > 0)
            {
                allRefs = [.. allRefs, .. Tree3DSource.ToReferences(in3d, t3)];
                foreach (var t in in3d) objectLodWorlds.Add(t.Worldspace);
            }
            if (light.Count > 0)
            {
                allRefs = [.. allRefs, .. cards!.ToReferences(light)];
                foreach (var t in light) objectLodWorlds.Add(t.Worldspace);
                progress?.Report($"Tree LOD: {light.Count:N0} trees from light (ESL) plugins go into object LOD as billboard cards (the engine can't hide their tree LOD)");
            }
            if (light.Count > 0 || in3d.Count > 0)
                bttTrees = allTrees.Where(t => !TreeCardSource.NeedsCards(t) && !in3d.Contains(t)).ToList();
        }
        string? Fingerprint(string p) => grass?.Fingerprint(p) ?? cards?.Fingerprint(p) ?? tree3d?.Fingerprint(p) ?? assets.Fingerprint(p);

        var sw = Stopwatch.StartNew();
        var bucketer = new QuadBucketer(scan.Grids);
        Parallel.ForEach(allRefs, new ParallelOptions { CancellationToken = ct }, bucketer.Add);
        var quads = bucketer.Build();
        if (req.Levels is { Count: > 0 } levels)
            quads = quads.Where(kv => levels.Contains(kv.Key.Level)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<QuadKey, string>();
        Parallel.ForEach(quads, new ParallelOptions { CancellationToken = ct }, kv =>
            hashes[kv.Key] = QuadHasher.Hash(kv.Key, kv.Value, Fingerprint,
                req.SettingsFingerprint + (MathF.Abs(req.ObjectBrightness - 1f) > 0.001f ? $"|bright:{req.ObjectBrightness:F2}" : "") + (req.PbrLod ? FormattableString.Invariant($"|pbr:{req.PbrLodBrightness:F2}:{req.PbrLodSize}") : "") + (ruleOptions.Candles ? "|candles" : "") + (ruleOptions.FxGlow ? "|fxglow" : "") + (ruleOptions.CustomFile is { } cf ? $"|rules:{new FileInfo(cf).Length}:{new FileInfo(cf).LastWriteTimeUtc.Ticks}" : "") + (cullTerrain is null ? "|keep-buried"
                    : "|terrain:" + (cullTerrain.Fingerprints.TryGetValue(kv.Key.Worldspace, out var tf) ? tf : "none"))));
        sw.Stop();

        // A cleared output has no earlier build to compare with: every block is written again.
        var manifest = req.OutputFolder is null || req.CleanOutput ? new BuildManifest() : BuildManifest.LoadOrEmpty(req.OutputFolder);
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
            var pbrLod = req.PbrLod ? new PbrLodTextures(assets) : null;
            var generator = new LodGenerator(assets, cullTerrain?.Heights, new CompositeSyntheticSource(grass, cards, tree3d)) { Brightness = req.ObjectBrightness, Pbr = pbrLod };
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

            // Seasons of Skyrim: seasonal copies of the object LOD blocks, after the normal ones are up to date.
            if (req.Seasons)
            {
                progress?.Report("Seasons (experimental): building seasonal object LOD from Data\\Seasons\\*_WIN/_SPR/_SUM/_AUT.ini...");
                var seasonal = SeasonalLod.Build(game, assets, assets.EnumeratePaths, resolver, quads, generator, output, progress, ct);
                if (seasonal.Seasons == 0 && seasonal.Messages.Count == 0)
                    progress?.Report("Seasons: no season INI files found in Data\\Seasons; nothing written.");
            }
            else SeasonalLod.RemoveAll(output, quads.Keys);

            if (req.Scan.TreeLod && bttTrees is { } treeRefs)
            {
                trees = TreeLodGenerator.Generate(scan.Grids, treeRefs, assets, output, manifest, progress, ct, req.TreeBrightness, objectLodWorlds);
                foreach (var w in trees.Warnings) Warn(w);
                progress?.Report($"Tree LOD: {trees.Instances:N0} trees, {trees.TreeTypes} types, {trees.Blocks:N0} blocks"
                                 + $" ({trees.WorldspacesWritten} worldspaces written, {trees.WorldspacesUnchanged} unchanged) in {trees.Elapsed.TotalSeconds:F1}s");
            }
            if (req.Scan.DynamicLod || req.Scan.GridObjects)
            {
                int n = DynamicLodWriter.Write(output, scan.Dynamic ?? []);
                int g = scan.Dynamic?.Count(d => d.IsGridObject) ?? 0;
                progress?.Report($"Dynamic LOD: {n - g:N0} switchable references and {g:N0} grid objects written to {DynamicLodWriter.RelativePath} for the AnvilLOD SKSE plugin");
            }
            else
            {
                var dyn = BuildManifest.FullPath(output, DynamicLodWriter.RelativePath);
                if (File.Exists(dyn)) File.Delete(dyn);
            }

            if (pbrLod is not null)
            {
                try
                {
                    var p = PbrLodStage.Run(assets, pbrLod, output, req.PbrLodSize, req.PbrLodBrightness, progress, ct);
                    progress?.Report(plan.Rebuild.Count == 0
                        ? "PBR LOD textures: no blocks were rebuilt, so the existing ones are kept."
                        : $"PBR LOD textures: {pbrLod.TwinsUsed} swapped for TexGen pbr_lod twins, {p.Converted + p.Unchanged} converted from PBR albedo"
                          + $" ({p.Converted} written, {p.Unchanged} unchanged) in {p.Elapsed.TotalSeconds:F1}s");
                    if (p.Failed > 0) Warn($"PBR LOD: {p.Failed} textures could not be converted (first: {p.Errors.FirstOrDefault()}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"PBR LOD textures failed: {ex.Message}");
                }
            }
            else PbrLodStage.RemoveAll(output);

            if (req.LargeReferences)
            {
                try
                {
                    var lr = LargeReferenceStage.Run(game, scan.Grids.Keys.Where(w => !UndersideStage.IgnoredWorlds.Contains(w)).ToList(), output, progress, Warn, ct,
                        flagEsl: req.LargeRefsEsl, dyndolodDllInstalled: assets.Exists("skse\\plugins\\dyndolod.dll"));
                    progress?.Report($"Large references: {lr.Listed:N0} listed in {lr.Elapsed.TotalSeconds:F1}s. Report: {lr.ReportFile}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Large references failed: {ex.Message}");
                }
            }
            else LargeReferenceStage.Remove(output);

            if (req.Underside && terrain is not null)
            {
                try
                {
                    var u = UndersideStage.Run(game, terrain.Heights, scan.Grids, output, req.UndersideStep, progress, Warn, ct);
                    progress?.Report($"Underside: {u.Worldspaces} worldspaces, {u.Blocks:N0} blocks, {u.Triangles:N0} triangles in {u.Elapsed.TotalSeconds:F1}s"
                                     + (u.Plugin is null ? "" : $". Enable {UndersidePluginWriter.FileName} (flagged ESL, no plugin slot) in your mod manager."));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Underside failed: {ex.Message}");
                }
            }
            else UndersideStage.RemoveAll(output);
            try
            {
                var skse = SksePluginInstaller.Install(output, game.DataFolder, req.SkseDll);
                if (skse.Installed || req.SkseDll == SkseDllChoice.None) progress?.Report(skse.Message);
                else Warn(skse.Message);
                if (skse.Message.Contains("WARNING")) Warn(skse.Message);
            }
            catch (IOException ex) { Warn($"SKSE plugin: couldn't copy AnvilLOD.dll into the output ({ex.Message})."); }
            catch (UnauthorizedAccessException ex) { Warn($"SKSE plugin: couldn't copy AnvilLOD.dll into the output ({ex.Message})."); }
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
            scan.Dynamic?.Count ?? 0,
            tree3dStats);

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

        static string? ResolveRulesFolder(string? dyndolod) => LodRules.FindInstallRulesFolder(dyndolod);

        void Warn(string w)
        {
            warnings.Add(w);
            progress?.Report("WARNING: " + w);
        }
    }
}
