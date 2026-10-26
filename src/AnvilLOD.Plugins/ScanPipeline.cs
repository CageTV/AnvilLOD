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
    bool RemoveUnderwater = true,                  // also drop triangles completely below the water surface of their cell, as DynDOLOD does (part of the same terrain read)
    bool GrassLod = true,                          // grass LOD in LOD4 from the grass cache (grass\*.cgid)
    float GrassDensity = 0.08f,                    // share of cached grass instances kept as LOD quads
    float GrassSize = 1f,                          // size multiplier for the kept quads (fewer, bigger tufts)
    bool WaterStandIns = false,                    // lit fake-water stand-ins for stream/creek/pond water-shader planes (grid objects), instead of black water beyond the loaded cells
    float GrassTop = GrassPatchBuilder.DefaultTop,       // grass LOD vertex-colour brightness at the top of a tuft (DynDOLOD GrassBrightnessTop)
    float GrassBottom = GrassPatchBuilder.DefaultBottom, // ... and at the roots (GrassBrightnessBottom)
    float TreeBrightness = 1f,                     // tree LOD billboard colour multiplier (DynDOLOD-style brightness)
    float ObjectBrightness = 1f,                   // object LOD colour multiplier (vertex colours; textures untouched)
    bool Seasons = false,                          // Seasons of Skyrim seasonal object and grass LOD (<block>.WIN.bto, …)
    bool ChildWorlds = true,                       // copy walled-city child worldspaces into the parent's LOD (DynDOLOD Configs)
    SkseDllChoice SkseDll = SkseDllChoice.Auto,    // which SKSE plugin build Generate puts in the output (None = installed separately)
    bool Underside = false,                        // terrain underside for volumetric lighting mods: <ws>_Underside.nif + AnvilLOD.esp (ESL)
    int UndersideStep = UndersideMesher.DefaultStep, // LAND vertices per underside quad (smaller = finer and heavier)
    int EslRange = 0,                              // size of the ESL effects plugins: 0 auto (from the installed game), 2048, or 4096 (game 1.6.1130 or newer only)
    bool NativeEffects = false,                    // fires, candles, torches, lava and streams as real references too (second plugin, AnvilLOD Effects.esp, ESL)
    bool NativeGridObjects = false,                // waterfalls, windmill fans and water wheels as real references in AnvilLOD.esp (the engine animates them) instead of SKSE copies
    bool PbrLod = false,                           // object LOD textures that match PBR full models (TexGen pbr_lod twins, converted PBR albedo)
    float PbrLodBrightness = 1f,                   // multiplier on DynDOLOD's default PBR scale (0.65) for the converted copies
    int PbrLodSize = 512,                          // largest side of a converted copy
    bool LargeReferences = false,                  // list missing large references in AnvilLOD.esm, for the engine's large reference grid
    bool LargeRefsEsl = true,                      // flag AnvilLOD.esm as ESL (no plugin slot); off = a normal ESM that uses a slot
    bool Candles = false,                          // also load DynDOLOD's Candles rules (candle/lantern/sconce lights get Far LOD)
    bool FxGlow = false,                           // also load DynDOLOD's FXGlow rules (fire and light glow cards get Far LOD)
    string? CustomRulesFile = null,                // the user's own rule file: its rules come first (null = DynDOLOD's own files only)
    bool CleanOutput = true,                       // Generate empties the output folder first (only if it is AnvilLOD's own, see OutputFolder), so old files never mix with new ones
    float IceInset = LodInset.DefaultIce,          // units glacier/ice LOD (LOD4, LOD8) is pulled inside its own surface, so the full model wins the z-fight where both are drawn (0 = off)
    bool MountainFit = true,                       // mountain LOD4/LOD8 vertices outside the full model are moved onto its surface (needs a mountain inset above 0)
    float MountainInset = 0f,                      // the same for mountain LOD (off by default)
    int SnowShaderMode = 0,                        // snow/ash LOD shader on full-texture shapes: 0 = HD at LOD4 only (DynDOLOD), 1 = plain at every level, 2 = HD at every level
    int SnowCover = 0,                             // vertex colour of snow/ash shapes: 0 = as the meshes carry it, 1 = full alpha, 2 = full alpha + lighter colour
    float ObjectInset = 0f,                        // the same for every other object (building, wall, rock) in LOD4/LOD8, so the loaded full model is the nearer surface in the swap ring (off by default)
    bool OpaqueAlpha = true,                       // shapes without an alpha property use opaque copies of textures whose alpha channel isn't opacity (ship hulls), so the LOD shader doesn't cut holes
    bool DetailedShips = true,                     // whole ships are drawn from simplified copies of their real models at LOD4 and LOD8 instead of the very coarse stock LOD meshes
    bool AnimatedShips = true,                     // with Animated Ships in the load order, its sailing ships are drawn beyond the loaded cells (real references in AnvilLOD.esp; needs native grid objects)
    bool CsGrassLod = true,
    int MapObjectLevel = -2,
    AnvilLOD.Core.Occlusion.OcclusionSettings? Occlusion = null,   // work out the occlusion data (Occlusion.esp) ourselves, with these settings (null = off)
    string? OcclusionFolder = null);                              // where Occlusion.esp goes (null = the output folder)                      // LOD32 objects for the world map: -1 none unless a rule names LOD32 (DynDOLOD's default), 0..2 = that level's LOD mesh, -2 auto (level 0 when A Clear Map of Skyrim is in the load order, else -1)                       // with Community Shaders' Grass Optimizations installed: _LOD0/_LOD1 meshes for its grass LOD system (meshes\LOD\Grass)

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
    /// <summary>
    /// ESL size for the plugins AnvilLOD splits (the effects plugins): game 1.6.1130 and newer read 4,096 FormIDs per ESL, older ones 2,048,
    /// and a plugin made for the larger range does not load on an older game. Auto reads the installed game's version and falls back to 2,048.
    /// </summary>
    internal static bool ResolveLargeEsl(int choice, string dataFolder, IProgress<string>? progress)
    {
        if (choice == 2048) return false;
        if (choice == 4096)
        {
            progress?.Report("ESL size: 4,096 records per plugin (your choice; needs game 1.6.1130 or newer).");
            return true;
        }
        var v = SksePluginInstaller.GameVersion(dataFolder);
        bool large = v is not null && v >= new Version(1, 6, 1130, 0);
        progress?.Report(v is null
            ? "ESL size: 2,048 records per plugin (the game version could not be read; pick 4,096 yourself if you run 1.6.1130 or newer)."
            : $"ESL size: {(large ? "4,096" : "2,048")} records per plugin (game {v}{(large ? "" : ", older than 1.6.1130")}).");
        return large;
    }

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

        // The same checks the app asks about before it starts; a command-line run (or one the user went ahead with) gets them in the log.
        if (req.Generate)
            foreach (var issue in Preflight.Check(req.Game, req.OutputFolder).Where(i => i.Code == Preflight.OutputModEnabled)) // the DynDOLOD one is reported below
                progress?.Report("WARNING: " + issue.Message);

        progress?.Report("Opening load order...");
        var gameOptions = req.OutputFolder is null ? req.Game : req.Game with { ExcludeFolder = req.OutputFolder };
        using var game = GameContext.Open(gameOptions, progress);
        progress?.Report($"Source: {game.SourceDescription}  ({game.PluginsLoaded:N0} of {game.PluginsListed:N0} plugins loaded)");
        var warnings = new List<string>(game.Warnings);
        // AnvilLOD replaces DynDOLOD. Generating with DynDOLOD's output enabled doubles the dynamic objects (its references stand on the
        // same places as the vanilla ones) and makes AnvilLOD pick up DynDOLOD's own LOD meshes; run it with that output disabled.
        var ddPlugins = game.LoadOrder.ListedOrder.Where(l => l.ModKey.FileName.String.StartsWith("DynDOLOD", StringComparison.OrdinalIgnoreCase)
                                                            && (l.ModKey.FileName.String.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || l.ModKey.FileName.String.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.ModKey.FileName.String).ToList();
        foreach (var w in game.Warnings) progress?.Report("WARNING: " + w);
        if (ddPlugins.Count > 0)
        {
            var dd = $"{string.Join(", ", ddPlugins)} {(ddPlugins.Count == 1 ? "is" : "are")} enabled. AnvilLOD replaces DynDOLOD: its output doubles the dynamic objects and its LOD meshes get picked up. Disable DynDOLOD's output mod and plugins before generating.";
            warnings.Add(dd);
            progress?.Report("WARNING: " + dd);
        }

        progress?.Report("Indexing BSAs (meshes, LOD settings, DynDOLOD rules, tree billboards)...");
        var assets = game.BuildAssets(
            p => p.StartsWith("meshes\\", StringComparison.Ordinal) || p.StartsWith("lodsettings\\", StringComparison.Ordinal)
                 || p.StartsWith("dyndolod\\", StringComparison.Ordinal) || p.StartsWith("textures\\terrain\\lodgen\\", StringComparison.Ordinal)
                 || p.StartsWith("grass\\", StringComparison.Ordinal) || p.StartsWith("seasons\\", StringComparison.Ordinal)
                 || p.StartsWith("textures\\", StringComparison.Ordinal)); // grass model textures (rendered grass billboards), the textures 3D tree LOD models need, and so the texture audit can tell a BSA texture from a missing one
        progress?.Report($"Indexed {assets.ArchivedFileCount:N0} files from {assets.ArchiveCount} archives in {assets.IndexTime.TotalSeconds:F1}s");
        if (req.Scan.TreeLod && req.Scan.RenderTreeBillboards) assets.FallbackRoot = TreeBillboardSynth.CacheRoot;   // tree billboards AnvilLOD draws itself: read last, so a real TexGen one wins
        foreach (var skipped in assets.SkippedArchives)
            Warn($"An archive could not be read and was skipped; its files are not used, everything else is ({skipped}).");
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
        bool animatedShips = req.AnimatedShips && req.NativeGridObjects && req.Scan.GridObjects && plugins.Any(p => p.StartsWith("AnimatedShips", StringComparison.OrdinalIgnoreCase));
        var ruleOptions = new LodRuleOptions(req.Candles, req.FxGlow, string.IsNullOrWhiteSpace(req.CustomRulesFile) ? null : req.CustomRulesFile, animatedShips);
        if (ruleOptions.CustomFile is not null && !File.Exists(ruleOptions.CustomFile))
            throw new FileNotFoundException($"The custom LOD rules file was not found: {ruleOptions.CustomFile}. Untick \"Use custom rules\" or pick the file again.", ruleOptions.CustomFile);
        if ((req.Candles || req.FxGlow) && rulesFolder is null)
            progress?.Report("Candles / FXGlow need the DynDOLOD folder (their rule files are part of DynDOLOD); nothing was added.");
        var rules = LodRules.Load(rulesFolder, dataRuleFiles, plugins, req.Preset, "SSE", ruleOptions);
        // A mod's rule file is only read when it is named for a plugin; one named after the mod (spaces, no .esp key) never is.
        var unreadable = dataRuleFiles.Keys.Where(n => n.StartsWith("DynDOLOD_", StringComparison.OrdinalIgnoreCase) && !LodRules.IsReadableRuleFileName(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (unreadable.Count > 0)
            progress?.Report($"WARNING: {unreadable.Count} rule file(s) in Data\\DynDOLOD are named for no plugin, so their rules are never used: {string.Join(", ", unreadable)}. "
                             + "Rename each to DynDOLOD_SSE_<plugin key>.ini for the plugin that places the models (TMDDirtRoads.esp -> DynDOLOD_SSE_tmddirtroadsesp.ini).");
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
        var ships = req.DetailedShips ? new ShipLodSource(assets) : null;
        int mapObjectLevel = req.MapObjectLevel;
        if (mapObjectLevel == -2)
        {
            var mapMod = plugins.FirstOrDefault(p => p.Contains("clear map", StringComparison.OrdinalIgnoreCase) || p.Contains("aclearmap", StringComparison.OrdinalIgnoreCase));
            mapObjectLevel = mapMod is not null ? 0 : -1;
            if (mapMod is not null) progress?.Report($"{mapMod} is in the load order: its map uses object LOD level 32, so objects get their most detailed LOD mesh at LOD32 (set Map objects to \"From rules\" to leave LOD32 to the rules).");
        }
        if (mapObjectLevel >= 0) progress?.Report($"LOD32 (world map): objects without a LOD32 rule use their level {mapObjectLevel} LOD mesh.");
        var resolver = new LodMeshResolver(lodIndex, rules, assets.Exists, meshLookup) { MapObjectLevel = mapObjectLevel, ModelUpgrade = ships is null ? null : ships.Upgrade, ModelFallback = ships is null ? null : ships.Fallback };

        // Walled cities: DynDOLOD's child world configs say which child worldspaces are copied into the parent's LOD.
        var childCopies = ChildWorldCopies.None;
        if ((req.ChildWorlds || req.Scan.ParentCopies) && rulesFolder is not null)
        {
            var configs = Path.Combine(Path.GetDirectoryName(rulesFolder)!, "Configs");
            childCopies = ChildWorldCopies.Load(configs, plugins, req.Preset);
            if (!req.ChildWorlds) childCopies = childCopies.WithoutChildCopies();
            if (childCopies.Worlds.Count > 0)
                progress?.Report($"Child worldspaces copied into their parent's LOD: {string.Join(", ", childCopies.Worlds.Select(w => $"{w.Child} -> {w.Parent}"))}");
        }
        else if (req.Scan.ParentCopies && rulesFolder is null)
            progress?.Report("Parent > Child copies need DynDOLOD's Configs folder (set the DynDOLOD install folder); none written.");
        var scanner = new MutagenReferenceScanner(game, assets, resolver, childCopies);
        // The PBR option also picks the PBR-patched waterfall models (Community Shaders only), when the load order has them.
        var scan = scanner.Scan(req.Scan with { PbrWaterfalls = req.PbrLod }, progress, ct);
        if (scan.Grids.Count == 0)
            Warn(req.Scan.Worldspaces is { Count: > 0 } ws
                ? $"None of the requested worldspaces ({string.Join(", ", ws)}) has a LODSettings\\<name>.lod file in loose files or BSAs."
                : "No worldspace has a LODSettings\\<name>.lod file in loose files or BSAs.");
        // Debugging aid: ANVILLOD_DEBUG_PARENT_COPIES=<file> dumps the Parent > Child candidates as a table (source reference, child world, base, cell).
        if (scan.ParentCopies is { } dumpCopies && Environment.GetEnvironmentVariable("ANVILLOD_DEBUG_PARENT_COPIES") is { Length: > 0 } dumpPath)
            File.WriteAllLines(dumpPath, dumpCopies.Select(c => string.Join('	', c.ChildWorldspace, c.SourcePlugin, c.SourceLocalId.ToString("X6"), c.BaseEditorId, c.Model, c.CellX, c.CellY, c.ChildCellExists ? 1 : 0, c.HasAlternateTextures ? 1 : 0)));
        if (scan.ParentCopyStats?.Regions is { } dumpRegions && Environment.GetEnvironmentVariable("ANVILLOD_DEBUG_PARENT_COPIES") is { Length: > 0 } cellsPath)
            File.WriteAllLines(cellsPath + ".cells", dumpRegions.SelectMany(r => r.ChildCells.Select(c => $"{r.ChildWorldspace}	{c.X}	{c.Y}")));
        progress?.Report($"Scan: {scan.Stats.LodReferencesFound:N0} LOD refs from {scan.Stats.PlacedObjectsVisited:N0} refs in {scan.Stats.Elapsed.TotalSeconds:F1}s");

        TerrainReader.Result? terrain = null;
        if ((req.RemoveBuried || req.Underside || req.Occlusion is not null) && scan.Grids.Count > 0)
        {
            progress?.Report("Reading terrain heights (LAND)...");
            terrain = TerrainReader.Read(game, scan.Grids.Keys.ToList(), ct);
            progress?.Report($"Terrain: {terrain.Heights.CellCount:N0} cells in {terrain.Elapsed.TotalSeconds:F1}s");
        }
        // Only the buried-triangle test (and the block hashes it feeds) depends on RemoveBuried; the underside reads the same heights.
        var cullTerrain = req.RemoveBuried ? terrain : null;

        if (req.Occlusion is not null && terrain is not null && (req.OcclusionFolder ?? req.OutputFolder) is { } occlusionFolder)
        {
            var occ = OcclusionStage.Run(game, terrain.Heights, scan.Grids, scan.References, scan.Trees, assets, occlusionFolder, req.Occlusion, progress, Warn, ct);
            if (occ.Path is not null)
                progress?.Report($"Occlusion.esp: {occ.Cells:N0} cells of {occ.Worldspaces} worldspace(s) in {occ.Elapsed.TotalSeconds:F0}s (clearance {req.Occlusion.Clearance:0}), written to {occ.Path}. Put it last in your plugin order.");
            if (occ.Skipped.Count > 0) progress?.Report($"Occlusion: left out (too small, a child world or no landscape): {string.Join(", ", occ.Skipped)}");
        }

        GrassLodSource? grass = null;
        IReadOnlyList<LodReference> allRefs = scan.References;
        if (req.GrassLod && scan.Grids.Count > 0 && (req.Levels is not { Count: > 0 } || req.Levels.Contains(LodLevel.Lod4)))
        {
            grass = new GrassLodSource(game, assets, req.GrassDensity, req.GrassSize, req.Generate ? req.OutputFolder : null, req.GrassTop, req.GrassBottom);
            var grassRefs = grass.Discover(scan.Grids);
            if (req.Generate && grassRefs.Count > 0)
            {
                progress?.Report("Grass LOD: packing the grass billboards into one atlas...");
                grass.PrepareAtlas();
            }
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
        // A custom worldspace (Bruma) ships its own pre-generated tree LOD. When most of its trees have no TexGen billboard (TexGen has not seen its
        // plugins), ours would be a partial copy that empties the mod's complete one: leave that worldspace's tree LOD as the mod made it.
        var keepTreeWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Trees TexGen listed but gave no billboard were skipped on purpose (small plants; DynDOLOD gives them no LOD either): they are not a gap
        // and must not count towards "this worldspace's trees are mostly missing".
        var texGenListed = ReadTexGenLists(assets);
        if (scan.TooSmallTreeKeys is { Count: > 0 } tooSmall) texGenListed.UnionWith(tooSmall);   // small plants the renderer left out count like those TexGen skipped
        var realMissingByWs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long realMissingTotal = 0;
        if (scan.MissingBillboardsByWorldType is { } byType)
            foreach (var (k, n) in byType)
            {
                int bar = k.IndexOf('|');
                if (bar < 0 || texGenListed.Contains(TexGenKey(k[(bar + 1)..]))) continue;
                realMissingByWs[k[..bar]] = realMissingByWs.GetValueOrDefault(k[..bar]) + n;
                realMissingTotal += n;
            }
        else
        {
            foreach (var (ws, n) in scan.MissingBillboardsByWorld ?? new Dictionary<string, long>()) realMissingByWs[ws] = n;
            realMissingTotal = scan.MissingBillboards?.Values.Sum() ?? 0;
        }
        if (req.Scan.TreeLod && scan.MissingBillboardsByWorld is { Count: > 0 } && scan.Trees is not null)
        {
            var foundByWs = scan.Trees.GroupBy(t => t.Worldspace, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (long)g.Count(), StringComparer.OrdinalIgnoreCase);
            foreach (var (ws, missing) in realMissingByWs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                long found = foundByWs.GetValueOrDefault(ws);
                if (missing < 500 || found >= 0.8 * (found + missing)) continue;
                bool shipsOwn = assets.EnumeratePaths(GamePath.Join("meshes", "terrain", ws, "trees") + "\\").Any(p => p.EndsWith(".btt", StringComparison.OrdinalIgnoreCase));
                if (!shipsOwn) continue;   // nothing of the mod's to keep: ours is all there would be
                keepTreeWorlds.Add(ws);
                Warn($"Tree LOD for {ws} not written: {missing:N0} of {found + missing:N0} of its trees have no TexGen billboard, so the tree LOD that is already installed for it is kept as it is "
                     + "(ours would cover only part of the trees). Run TexGen with the current load order to let AnvilLOD make it.");
            }
        }
        var sceneTrees = keepTreeWorlds.Count == 0 ? scan.Trees : scan.Trees?.Where(t => !keepTreeWorlds.Contains(t.Worldspace)).ToList();
        IReadOnlyList<TreeReference>? bttTrees = sceneTrees;
        var objectLodWorlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tree billboards come from TexGen's output, named after each tree's model. When most trees have none (a tree mod added or changed after TexGen ran),
        // writing tree LOD would leave the distance bare: our few trees, and the vanilla tree LOD emptied to make room for them. Keep the vanilla tree LOD instead.
        long keptMissing = keepTreeWorlds.Sum(ws => realMissingByWs.GetValueOrDefault(ws));
        long treesFound = sceneTrees?.Count ?? 0, treesMissing = realMissingTotal - keptMissing;
        bool treeLodUnusable = req.Scan.TreeLod && treesMissing >= 1000 && treesFound < 0.25 * (treesFound + treesMissing);
        if (treeLodUnusable)
        {
            bttTrees = null;
            var examples = new List<string>();
            foreach (var kv in scan.MissingBillboards!.OrderByDescending(k => k.Value))
            {
                if (StaleTexGenBillboard(assets, kv.Key) is { } hint) { examples.Add(hint); if (examples.Count == 3) break; }
            }
            Warn($"Tree LOD not written: {treesMissing:N0} of {treesFound + treesMissing:N0} trees have no TexGen billboard, so the vanilla tree LOD is left as it is (writing ours would leave the distance without trees). "
                 + "Re-run TexGen with the current load order" + (examples.Count > 0 ? "; for example " + string.Join("; ", examples) : "") + ".");
            progress?.Report("Tree types without a billboard, most placed first: "
                             + string.Join(", ", scan.MissingBillboards!.OrderByDescending(k => k.Value).Take(8).Select(kv => $"{kv.Key} ({kv.Value:N0})")));
        }
        if (req.Scan.TreeLod && !treeLodUnusable && sceneTrees is { Count: > 0 } allTrees)
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
                if (scanner.Tree3DPatchedMeshes > 0)
                    progress?.Report($"3D tree LOD: PGPatcher's ParallaxGen_Diff.json lists {scanner.Tree3DPatchedMeshes:N0} patched meshes; {scanner.Tree3DMatchedByOriginalCrc} tree models were found under the checksum from before patching, as DynDOLOD does.");
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
        string? Fingerprint(string p) => grass?.Fingerprint(p) ?? cards?.Fingerprint(p) ?? tree3d?.Fingerprint(p) ?? ships?.Fingerprint(p) ?? assets.Fingerprint(p);

        var sw = Stopwatch.StartNew();
        var bucketer = new QuadBucketer(scan.Grids);
        Parallel.ForEach(allRefs, new ParallelOptions { CancellationToken = ct }, bucketer.Add);
        var quads = bucketer.Build();
        if (req.Levels is { Count: > 0 } levels)
            quads = quads.Where(kv => levels.Contains(kv.Key.Level)).ToDictionary(kv => kv.Key, kv => kv.Value);

        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<QuadKey, string>();
        Parallel.ForEach(quads, new ParallelOptions { CancellationToken = ct }, kv =>
            hashes[kv.Key] = QuadHasher.Hash(kv.Key, kv.Value, Fingerprint,
                req.SettingsFingerprint + (mapObjectLevel >= 0 ? $"|map32:{mapObjectLevel}" : "") + (MathF.Abs(req.ObjectBrightness - 1f) > 0.001f ? $"|bright:{req.ObjectBrightness:F2}" : "") + (req.PbrLod ? FormattableString.Invariant($"|pbr:{req.PbrLodBrightness:F2}:{req.PbrLodSize}") : "") + (ruleOptions.Candles ? "|candles" : "") + (ruleOptions.FxGlow ? "|fxglow" : "") + (ruleOptions.CustomFile is { } cf ? $"|rules:{new FileInfo(cf).Length}:{new FileInfo(cf).LastWriteTimeUtc.Ticks}" : "") + (req.SnowShaderMode != 0 ? $"|snowmode:{req.SnowShaderMode}" : "") + (req.SnowCover != 0 ? $"|snowcover:{req.SnowCover}" : "") + (req.MountainFit && req.MountainInset > 0f ? "|mtnfit1" : "") + (req.OpaqueAlpha ? "|opaque1" : "") + (req.DetailedShips ? FormattableString.Invariant($"|ships2:{LodInset.DefaultShip:F1}") : "") + (req.IceInset > 0f ? FormattableString.Invariant($"|iceinset:{req.IceInset:F1}") : "") + (req.MountainInset > 0f ? FormattableString.Invariant($"|mtninset:{req.MountainInset:F1}") : "") + (req.ObjectInset > 0f ? FormattableString.Invariant($"|objinset:{req.ObjectInset:F1}") : "") + (cullTerrain is null ? "|keep-buried"
                    : "|terrain:" + (cullTerrain.Fingerprints.TryGetValue(kv.Key.Worldspace, out var tf) ? tf : "none") + (req.RemoveUnderwater ? "|water:" + cullTerrain.Heights.WaterFingerprint(kv.Key.Worldspace) : "")),
                id => scan.TextureVariants is { } tv && tv.TryGetValue(id, out var v) ? Fingerprint(v.FullModel) : null));
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
            var opaqueLod = req.OpaqueAlpha ? new OpaqueLodTextures(assets, OpaqueLodStage.IsTranslucent) : null;
            var generator = new LodGenerator(assets, cullTerrain?.Heights, new CompositeSyntheticSource(grass, cards, tree3d, ships)) { RemoveUnderwater = req.RemoveUnderwater, Brightness = req.ObjectBrightness, SnowShaderMode = req.SnowShaderMode, SnowCover = req.SnowCover, FitMountains = req.MountainFit && req.MountainInset > 0f, Pbr = pbrLod, Opaque = opaqueLod, Categorize = SizeBreakdown.Categorize, Inset = req.IceInset > 0f || req.MountainInset > 0f || req.ObjectInset > 0f || req.DetailedShips ? path => LodInset.For(path, req.IceInset, req.MountainInset, req.DetailedShips ? LodInset.DefaultShip : 0f, req.ObjectInset) : null, TextureVariants = scan.TextureVariants };
            gen = generator.Generate(quads, plan.Rebuild, output, progress, ct);
            if (ships is not null && ships.Built() is { Count: > 0 } builtShips)
                progress?.Report($"Detailed ships: {builtShips.Count} ship models drawn from their real models ("
                                 + string.Join(", ", builtShips.Select(b => $"{Path.GetFileNameWithoutExtension(b.Model)} {b.Lod4:N0}/{b.Lod8:N0} triangles")) + " at LOD4/LOD8)"
                                 + (ships.WithoutStockLod.Count > 0 ? $"; {ships.WithoutStockLod.Count} of them had no LOD at all and got one: " + string.Join(", ", ships.WithoutStockLod.Select(Path.GetFileNameWithoutExtension)) : ""));
            var tm = generator.Matching;
            if (scan.TextureVariants is not null && tm.Meshes > 0)
                progress?.Report($"Texture matching: {tm.ShapesChanged:N0} of {tm.Shapes:N0} LOD shapes (in {tm.MeshesChanged:N0} of {tm.Meshes:N0} LOD meshes) now use the texture their full model has, snow, ice and other variants included;"
                                 + $" {tm.ShapesStitched:N0} shapes keep stitched LOD textures. {tm.FullModelsRead:N0} full models read, {tm.FullModelsMissing:N0} not found.");

            var cl = generator.Cleaning;
            if (cl.TrianglesDropped > 0 || cl.VerticesMerged > 0)
                progress?.Report($"Mesh tidy-up: {cl.TrianglesDropped:N0} triangles without area dropped and {cl.VerticesMerged:N0} duplicate vertices merged in {cl.Parts:N0} LOD mesh shapes.");

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
                progress?.Report("Seasons: building seasonal object LOD from Data\\Seasons\\*_WIN/_SPR/_SUM/_AUT.ini...");
                var seasonal = SeasonalLod.Build(game, assets, assets.EnumeratePaths, resolver, quads, generator, output, progress, ct, grass);
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
            // Grid objects the engine can animate itself become real references in AnvilLOD.esp; the SKSE plugin then leaves them out.
            var nativeKinds = (req.NativeGridObjects ? AnvilLOD.Plugins.NativeGridObjects.Kinds.Waterworks : 0)
                              | (req.NativeEffects ? AnvilLOD.Plugins.NativeGridObjects.Kinds.Effects : 0)
                              | (ruleOptions.AnimatedShips ? AnvilLOD.Plugins.NativeGridObjects.Kinds.Ships : 0);
            var largeEsl = ResolveLargeEsl(req.EslRange, game.DataFolder, progress);
            // Other worldspaces than Tamriel and Solstheim (mod worlds, small worlds) get them too, unless they are children of another one.
            var childWorlds = UndersidePluginWriter.ChildWorldspaces(game);
            var otherWorlds = (scan.Dynamic ?? []).Where(d => d.IsGridObject && !d.Glow).Select(d => d.Ref.Worldspace)
                .Where(w => !childWorlds.Contains(w) && !NativeGridObjects.Worlds.Contains(w) && !UndersideStage.IgnoredWorlds.Contains(w))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var natives = nativeKinds != 0 && req.Scan.GridObjects ? NativeGridObjects.Select(scan.Dynamic ?? [], nativeKinds, req.WaterStandIns, otherWorlds) : [];
            if (natives.Any(p => otherWorlds.Contains(p.Source.Ref.Worldspace)))
                progress?.Report("Native grid objects also written for: " + string.Join(", ", natives.Where(p => otherWorlds.Contains(p.Source.Ref.Worldspace))
                    .GroupBy(p => p.Source.Ref.Worldspace, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} ({g.Count():N0})")));
            if (ruleOptions.AnimatedShips)
            {
                var sailing = natives.Where(p => NativeGridObjects.IsAnimatedShip(p.Model.ToLowerInvariant())).ToList();
                progress?.Report(sailing.Count > 0
                    ? $"Animated Ships: {sailing.Count:N0} sailing ship references ({sailing.Select(p => p.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()} models) are real references in AnvilLOD.esp; the SKSE plugin enables them with the mod's own schedule"
                    : "Animated Ships is in the load order, but none of its sailing ships were found to draw (is the plugin active?)");
            }
            var nativeSources = natives.Select(p => p.Source).ToHashSet();
            // Left out of AnvilLOD.dyn entirely: the engine draws the real references in the walled cities too (tested at Whiterun: they are
            // there and they turn), so a model drawn by the plugin as well showed up as a second, motionless fan.
            var dynamicList = natives.Count == 0 ? scan.Dynamic ?? [] : (scan.Dynamic ?? []).Where(d => !nativeSources.Contains(d)).ToList();
            if (req.Scan.DynamicLod || req.Scan.GridObjects)
            {
                IReadOnlyDictionary<string, string>? waterMap = null;
                if (req.WaterStandIns && req.Scan.GridObjects)
                {
                    var gridMeshes = dynamicList.Where(d => d.IsGridObject && !d.Glow)
                        .Select(d => d.Ref.Meshes.Lod4 ?? d.Ref.Meshes.Lod8 ?? d.Ref.Meshes.Lod16 ?? d.Ref.Meshes.Lod32)
                        .Where(m => !string.IsNullOrEmpty(m)).Select(m => m!);
                    var standIns = WaterStandIns.Build(gridMeshes, assets, output);
                    waterMap = standIns.Map;
                    progress?.Report($"Water stand-ins: {standIns.Converted} stream/pond water meshes now draw as lit fake water"
                                     + (standIns.Converted > 0 ? $" ({string.Join(", ", standIns.Names.Select(Path.GetFileNameWithoutExtension).Take(8))}{(standIns.Names.Count > 8 ? ", ..." : "")})" : "")
                                     + $"; {standIns.NoWaterShader} matching meshes had no water shader, {standIns.Unreadable} could not be read.");
                }
                int n = DynamicLodWriter.Write(output, dynamicList, waterMap);
                int g = dynamicList.Count(d => d.IsGridObject);
                progress?.Report($"Dynamic LOD: {n - g:N0} switchable references and {g:N0} grid objects written to {DynamicLodWriter.RelativePath} for the AnvilLOD SKSE plugin");
            }
            else
            {
                var dyn = BuildManifest.FullPath(output, DynamicLodWriter.RelativePath);
                if (File.Exists(dyn)) File.Delete(dyn);
            }
            var parentCopies = scan.ParentCopies;
            if (req.Scan.ParentCopies && grass is not null && scan.ParentCopyStats?.Regions is { Count: > 0 } copyRegions)
            {
                var gc = GrassCopies.Build(grass, copyRegions, output, ct);
                if (gc.Copies.Count > 0)
                {
                    parentCopies = [.. parentCopies ?? [], .. gc.Copies];
                    progress?.Report($"Parent > Child copies: grass LOD for {gc.Cells:N0} cells around the walled cities ({gc.Meshes:N0} small meshes in meshes\\{GrassCopies.Folder}), the parent's grass blocks are hidden there");
                }
            }
            if (req.Scan.ParentCopies && terrain is not null && scan.ParentCopyStats is { Regions: { Count: > 0 } wRegions, WaterCells: { Count: > 0 } wCells })
            {
                var standInDir = Path.Combine(output, "meshes", "anvillod", "water");
                var tplPath = Directory.Exists(standInDir) ? Directory.EnumerateFiles(standInDir, "water*.nif").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;
                LodMeshPart? tpl = null;
                if (tplPath is not null)
                {
                    try { tpl = AnvilLOD.Meshes.Nif.NifGeometryReader.Read(tplPath, File.ReadAllBytes(tplPath)).Parts.FirstOrDefault(); }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException) { }
                }
                if (tpl is null) progress?.Report("Parent > Child copies: no water stand-in to take the water material from (turn on the water stand-ins), so the cell water around the cities is not drawn.");
                else
                {
                    var wc = WaterCopies.Build(terrain.Heights, wCells, wRegions, tpl, output, ct);
                    if (wc.Copies.Count > 0)
                    {
                        parentCopies = [.. parentCopies ?? [], .. wc.Copies];
                        progress?.Report($"Parent > Child copies: water for {wc.Cells:N0} cells around the walled cities ({wc.Runs:N0} water strips in meshes\\{WaterCopies.Folder}), the terrain LOD water is switched off there");
                    }
                }
            }
            if (req.Scan.ParentCopies && parentCopies is { Count: > 0 })
            {
                // A skinned model (a cloth banner or flag) drawn from a copy has no skeleton to take its bones from, so its vertices explode into
                // giant sheets across the sky (Whiterun's tower banners, 2026-10-13). The plugin refuses them too; leaving them out here keeps the list clean and says so.
                var skinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in parentCopies.Select(c => GamePath.Normalize(c.Model)).Where(m => !m.Contains("\\anvillod\\", StringComparison.Ordinal) && !m.StartsWith("anvillod\\", StringComparison.Ordinal)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var rel = model.StartsWith("meshes\\", StringComparison.Ordinal) ? model : "meshes\\" + model;
                    try
                    {
                        if (!assets.TryOpen(rel, out var ns)) continue;
                        byte[] nb;
                        using (ns) { using var ms = new MemoryStream(); ns.CopyTo(ms); nb = ms.ToArray(); }
                        if (AnvilLOD.Meshes.Nif.NifGeometryReader.Read(rel, nb).Warnings.Any(w => w.StartsWith("Skinned shape", StringComparison.Ordinal))) skinned.Add(model);
                    }
                    catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException or IOException) { }
                }
                if (skinned.Count > 0)
                {
                    int before = parentCopies.Count;
                    parentCopies = [.. parentCopies.Where(c => !skinned.Contains(GamePath.Normalize(c.Model)))];
                    progress?.Report($"Parent > Child copies: {before - parentCopies.Count:N0} references left out because their model is skinned (cloth, it would stretch across the sky): {string.Join(", ", skinned.OrderBy(s => s, StringComparer.Ordinal).Take(8))}{(skinned.Count > 8 ? ", ..." : "")}");
                }
            }
            if (req.Scan.ParentCopies && parentCopies is { Count: > 0 } pcs)
            {
                int written = ParentCopyWriter.Write(output, pcs);
                int altTex = pcs.Count(c => c.HasAlternateTextures), inCells = pcs.Count(c => c.ChildCellExists);
                progress?.Report($"Parent > Child copies: {written:N0} references written to {ParentCopyWriter.RelativePath} for the AnvilLOD SKSE plugin"
                                 + $" ({inCells:N0} in cells the child worldspace has; {altTex:N0} use texture variants the plugin can't apply, they show the default textures)");
            }
            else ParentCopyWriter.Remove(output);

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

            if (opaqueLod is not null)
            {
                try
                {
                    var o = OpaqueLodStage.Run(assets, opaqueLod, output, progress, ct);
                    progress?.Report(plan.Rebuild.Count == 0
                        ? "Opaque LOD textures: no blocks were rebuilt, so the existing ones are kept."
                        : $"Opaque LOD textures: {o.Written + o.Unchanged} textures whose alpha channel isn't opacity point at opaque copies ({o.Written} written, {o.Unchanged} unchanged) in {o.Elapsed.TotalSeconds:F1}s");
                    if (o.Failed > 0) Warn($"Opaque LOD textures: {o.Failed} could not be written (first: {o.Errors.FirstOrDefault()}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Opaque LOD textures failed: {ex.Message}");
                }
            }
            else OpaqueLodStage.RemoveAll(output);

            // Community Shaders' Grass Optimizations: middle and far LOD meshes for the grass models (it keeps the full mesh for any that has none).
            if (req.CsGrassLod && CsGrassLodStage.FeaturePresent(assets))
            {
                try
                {
                    var gl = CsGrassLodStage.Run(game, assets, output, ct);
                    progress?.Report($"Grass Optimizations (Community Shaders) found: {gl.Middle:N0} middle and {gl.Far:N0} far LOD meshes made for {gl.Models:N0} grass models, written to meshes\\LOD\\Grass"
                                     + $" ({gl.TrianglesMiddle:N0} + {gl.TrianglesFar:N0} triangles in them, against {gl.TrianglesFull:N0} in the models) in {gl.Elapsed.TotalSeconds:F1}s."
                                     + $" Left alone: {gl.TooSmall:N0} already tiny, {gl.Authored:N0} with LOD meshes from their grass mod, {gl.MultiShape:N0} with several shapes, {gl.LegacyFormat:N0} in the old Skyrim LE format, {gl.OtherLayout:N0} with another vertex layout"
                                     + (gl.DuplicateNames > 0 ? $", {gl.DuplicateNames:N0} sharing a file name with another model" : "") + ". Turn on mesh LODs in the feature's settings in the Community Shaders menu.");
                    if (req.GrassLod)
                        progress?.Report("Note: AnvilLOD's own grass LOD (the grass in the LOD4 blocks) is still on. The feature's author recommends NGIO's extended grass distance plus these LOD meshes instead of grass in the blocks; if you use extended grass, consider unticking Grass LOD.");
                    if (gl.Failed > 0) Warn($"Grass Optimizations LOD: {gl.Failed} grass models could not be processed (first: {gl.Errors.FirstOrDefault()}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Grass Optimizations LOD meshes failed: {ex.Message}");
                }
            }
            else CsGrassLodStage.Remove(output);

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
                    var u = UndersideStage.Run(game, terrain.Heights, scan.Grids, output, req.UndersideStep, progress, Warn, ct, natives, largeEsl);
                    progress?.Report($"Underside: {u.Worldspaces} worldspaces, {u.Blocks:N0} blocks, {u.Triangles:N0} triangles in {u.Elapsed.TotalSeconds:F1}s"
                                     + (u.Plugin is null ? "" : $". Enable {UndersidePluginWriter.FileName} in your mod manager{(u.PluginLight ? " (flagged ESL, no plugin slot)" : " (too many records for an ESL: it is a regular plugin and takes a slot)")}."));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Underside failed: {ex.Message}");
                }
            }
            else if (natives.Count > 0)
            {
                try
                {
                    var r = UndersidePluginWriter.Write(game, [], output, natives, largeEsl);
                    if (r.NativeRefs is { Count: > 0 } ne) NativeGridObjectWriter.Write(output, ne);
                    progress?.Report($"Native grid objects: {r.NativeReferences:N0} references on {r.NativeBases:N0} base records written to {UndersidePluginWriter.FileName} (waterfalls, windmill fans, water wheels, effects); the SKSE plugin no longer draws them. Enable {UndersidePluginWriter.FileName}{(r.EffectsPlugins == 0 ? "" : r.EffectsPlugins == 1 ? $" and {UndersidePluginWriter.EffectsFileName}" : $" and the {r.EffectsPlugins} AnvilLOD Effects*.esp files")} in your mod manager{(r.IsLight ? " (flagged ESL, no plugin slot)" : " (too many records for an ESL: a regular plugin that takes a slot)")}.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Warn($"Native grid objects failed: {ex.Message}");
                }
            }
            else UndersideStage.RemoveAll(output);
            // DynDOLOD's "File Not Found Textures": a LOD mesh naming a texture that exists nowhere draws purple.
            if (gen.MeshesLoaded > 0)
            {
                var texMissing = generator.AuditTextures(output);
                if (texMissing.Count > 0)
                {
                    foreach (var kv in texMissing.OrderByDescending(k => k.Value.Meshes).Take(25))
                        progress?.Report($"Missing texture: {kv.Key} (used by {kv.Value.Meshes:N0} LOD mesh{(kv.Value.Meshes == 1 ? "" : "es")}, first {kv.Value.FirstMesh})");
                    int stubs = 0;
                    foreach (var kv in texMissing)
                    {
                        // A missing texture draws purple, and some game builds crash on it in the lighting shader. A plain stand-in at the same path
                        // fixes both; it is only written where nothing exists (the output is cleared on every run, and installing the real one wins on the next).
                        try { StubTextures.Write(output, kv.Key); stubs++; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    Warn($"{texMissing.Count:N0} textures named by LOD meshes exist nowhere (loose, BSA or output). Plain stand-ins were written to the output for {stubs:N0} of them so the game neither draws them purple nor crashes on them. Most used: "
                         + string.Join(", ", texMissing.OrderByDescending(k => k.Value.Meshes).Take(3).Select(k => k.Key)) + ". All are listed above in the log; install the mod that ships the real ones and generate again.");
                }
                else progress?.Report("Texture audit: every texture named by the LOD meshes exists.");
                var texBad = generator.AuditTextureFiles(output);
                if (texBad.Count > 0)
                {
                    foreach (var kv in texBad.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(25))
                        progress?.Report($"Unloadable texture: {kv.Key}: {kv.Value.Problem} (used by LOD mesh {kv.Value.FirstMesh})");
                    Warn($"{texBad.Count:N0} textures named by LOD meshes cannot be loaded by the game (a shader using one crashes the game): "
                         + string.Join(", ", texBad.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Take(3).Select(k => $"{k.Key} ({k.Value.Problem})")) + ". All are listed above in the log.");
                }
            }
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
            foreach (var line in SizeBreakdown.Lines(gen, output)) progress?.Report(line);
            if (gen.BlocksFailed > 0) Warn($"{gen.BlocksFailed} blocks failed to write (first: {gen.BlockErrors.First().Key}: {gen.BlockErrors.First().Value}).");
            if (grass is not null)
            {
                if (grass.TypesFromModelTexture > 0)
                    progress?.Report(grass.BillboardsRendered > 0
                        ? $"Grass LOD: {grass.TypesFromModelTexture} grass types have no TexGen billboard; {grass.BillboardsRendered} billboards were rendered from their models (textures\\anvillod\\grass)."
                        : $"Grass LOD: {grass.TypesFromModelTexture} grass types have no TexGen billboard and use their own model texture instead.");
                if (grass.Atlas is { } atlas)
                    progress?.Report($"Grass LOD: {atlas.Tiles} billboards in one {atlas.Size}x{atlas.Size} atlas (tiles up to {(atlas.SizeCap == int.MaxValue ? "full size" : atlas.SizeCap + " px")}, {atlas.Reduced} scaled down), so each block needs one grass material.");
                Classify(grass.MissingBillboards, "grass", "Re-run TexGen with grass billboards enabled.");
                if (grass.ModelsNotFound.Count > 0)
                    Warn($"The grass cache names {grass.ModelsNotFound.Count} grass models that no enabled mod provides ({string.Join(", ", grass.ModelsNotFound.OrderByDescending(kv => kv.Value).Take(4).Select(kv => Path.GetFileName(kv.Key)))}). "
                         + "The cache was probably made with a different set of grass mods (or with one disabled since): rebuild the grass cache (NGIO / FasterNGIO) with the grass mods you use now, otherwise those areas get no grass LOD and no real grass either.");
            } 
            if (gen.MeshErrors.Count > 0)
            {
                foreach (var (mesh, err) in gen.MeshErrors.Take(20)) progress?.Report($"Mesh problem: {mesh}: {err}");
                Warn($"{gen.MeshErrors.Count} LOD meshes could not be used; see the Missing meshes tab.");
            }
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
        static string TexGenKey(string missingKey) => TexGenLists.Key(missingKey);

        // A billboard TexGen made for the same tree record under another model name ("plugin\model_formid" is what is missing).
        static string? StaleTexGenBillboard(AssetIndex assets, string missingKey)
        {
            int slash = missingKey.IndexOf('\\'), us = missingKey.LastIndexOf('_');
            if (slash < 0 || us < slash) return null;
            var plugin = missingKey[..slash].ToLowerInvariant();
            var stem = missingKey[(slash + 1)..us].ToLowerInvariant();
            var suffix = "_" + missingKey[(us + 1)..].ToLowerInvariant() + ".dds";
            foreach (var path in assets.EnumeratePaths("textures\\terrain\\lodgen\\" + plugin + "\\"))
            {
                var name = Path.GetFileName(path).ToLowerInvariant();
                if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length && name[..^suffix.Length] != stem)
                    return $"{missingKey[..slash]} {missingKey[(us + 1)..]}: TexGen's billboard is for the model '{name[..^suffix.Length]}', the tree now uses '{stem}'";
            }
            return null;
        }

        // TexGen_SSE_Tree_Billboards.txt / TexGen_SSE_Grass_Billboards.txt: "plugin;FORMID;EditorID[,Complex]"
        static HashSet<string> ReadTexGenLists(AssetIndex assets) => TexGenLists.Read(assets);

        static string? ResolveRulesFolder(string? dyndolod) => LodRules.FindInstallRulesFolder(dyndolod);

        void Warn(string w)
        {
            warnings.Add(w);
            progress?.Report("WARNING: " + w);
        }
    }
}
