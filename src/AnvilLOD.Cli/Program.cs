using System.Diagnostics;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;

namespace AnvilLOD.Cli;

public static class Program
{
    private const string Usage = """
        AnvilLOD — fast object LOD generator for Skyrim SE/AE

        Usage:
          AnvilLOD scan [options]        analyse the load order and show what would be built
          AnvilLOD generate [options]    scan, then write the changed LOD blocks (.bto) to --output
          AnvilLOD author --plugin <file> --author-out <folder> [options]
                                         Mod author tools: report the plugin's objects and trees that have no
                                         LOD, and generate LOD meshes, tree billboards and a rule file for them
                                         into their own folder. Options: --no-meshes --no-billboards --no-rules
                                         --min-size <units, default 400> --min-tree-height <units, default 256>
                                         --overwrite (replace files already in --author-out)
                                         --budget recommended|off|<LOD0,LOD1,LOD2> triangle budget for the LOD meshes (default
                                         recommended, by model size; see lodmaker)

          AnvilLOD lodmaker --input <file|folder> [--input ...] (--output <folder> | --mo2 <instance> --new-mod <name>) [options]
                                         LOD Mesh Maker: make LOD meshes (name_lod_0/1/2.nif, simplified, no collision, the
                                         model's own textures) from full models, plus a DynDOLOD-format rule file.
                                         --input is a .nif, a mod folder, its meshes folder or any folder of models.
                                         --new-mod <name> creates the mod folder in the MO2 mods folder (needs --mo2).
                                         Options: --group <name> (meshes\lod\<name>, default the output folder's name)
                                         --levels 0,1,2 (LOD4, LOD8, LOD16; default all) --detail <0.1-10, default 1;
                                         lower keeps more detail> --min-size <units> --no-rules --overwrite
                                         --budget recommended|off|<LOD0,LOD1,LOD2>  triangle budget per model: the default,
                                         "recommended", scales with the model's size (from real SE/AE LOD meshes);
                                         "off" has none; three numbers set the maximums (0 = no limit), e.g. 800,500,300

        Options:
          --mo2 <path>           MO2 instance folder (with ModOrganizer.ini) — reads mods directly, no need to launch from MO2
          --profile <name>       MO2 profile (default: the instance's selected profile)
          --data <path>          Skyrim Data folder (default: auto-detect / MO2 VFS)
          --plugins <path>       plugins.txt to use (requires --data)
          --worldspace <edid>    Limit to a worldspace; repeatable (default: all with a .lod file)
          --output <path>        Output folder (default for generate: "AnvilLOD Output" in the MO2 mods folder, Vortex's staging
                                 folder or Documents). It is created if missing and, if it is AnvilLOD's own (empty, or holds its
                                 manifest or log), emptied before every generate so old files never mix with new ones
          --keep-output          Don't empty the output folder first: update it in place (only changed blocks are rewritten)
          --report <file.json>   Write a JSON report (default: AnvilLOD.scan.json next to the exe)
          --dyndolod <path>      DynDOLOD install folder: reuse its LOD rule files (optional)
          --preset <name>        low | medium | high (default high): which rule preset to use
          --candles              Also load DynDOLOD's Candles rules (candles, lanterns and sconces get Far LOD); needs --dyndolod
          --fxglow               Also load DynDOLOD's FXGlow rules (fire and light glow cards get Far LOD); needs --dyndolod
          --rules <file>         Your own LOD rule file (DynDOLOD rule format, e.g. saved by the app's rule editor). Its rules come
                                 before all others; without it only DynDOLOD's own rule files are used
          --keep-buried          Don't remove LOD triangles buried under the terrain
          --include-disabled     Include initially-disabled references
          --no-child-worlds      Don't copy walled cities (Whiterun, Solitude, ...) into Tamriel's LOD
          --no-dynamic           Keep switchable references (quest-toggled) in static LOD instead of
                                 handing them to the AnvilLOD SKSE plugin
          --seasons              EXPERIMENTAL: seasonal object LOD for Seasons of Skyrim (Data\Seasons\*_WIN.ini ...)
          --skse-dll <which>     SKSE plugin put in the output: auto (default, reads SkyrimSE.exe),
                                 1170 (SE 1.5.97 - AE 1.6.1170), 17 (newer, 1.7.x), none (installed separately)
          --no-grid-objects      Don't hand water planes, waterfalls, fires and windmills (DynDOLOD
                                 "grid" objects) to the AnvilLOD SKSE plugin
          --no-grass             Don't build grass LOD from the grass cache
          --grass-density <pct>  Share of cached grass kept as LOD (default 8)
          --tree-brightness <pct> Tree LOD billboard brightness, 10-110 (default 100)
          --object-brightness <pct> Object LOD brightness, 10-110 (default 100)
          --no-trees             Don't build billboard tree LOD (.lst/.btt + atlas)
          --tree-3d              Use 3D tree LOD models (DynDOLOD passthru_lod.nif, matched by the CRC32 of the
                                 tree's mesh) in object LOD: 3D at LOD4, billboard cards from LOD8 on.
                                 Trees without a model keep billboard tree LOD.
          --tree-3d-lod8         With --tree-3d: use the 3D model at LOD8 too (bigger files)
          --tree-3d-by-name      With --tree-3d: when no model matches the CRC32, accept the one stored under the
                                 plain tree name (it may be for another version of the mesh)
          --mo2-game <dir>       With --mo2: the game folder (SkyrimSE.exe), instead of the gamePath in ModOrganizer.ini
          --mo2-mods <dir>       With --mo2: the mods folder, instead of the ini's (for lists on another drive)
          --mo2-profiles <dir>   With --mo2: the profiles folder, instead of the ini's
          --mo2-overwrite <dir>  With --mo2: the overwrite folder, instead of the ini's
                                 (Give the game, mods and profiles folders and ModOrganizer.ini isn't needed.)
          --large-refs           List the references that qualify for the engine's large reference grid but that no plugin
                                 lists yet in AnvilLOD.esm (flagged ESL). Enable it after your other ESMs
          --large-refs-no-esl    With --large-refs: don't flag AnvilLOD.esm as ESL; it is then a normal ESM that uses a
                                 plugin slot (try it if large references flicker and you want to rule the ESL flag out)
          --pbr-lod              Make object LOD textures match PBR full models: use TexGen's pbr_lod twins and
                                 convert PBR albedo copies (textures\anvillod\pbr) for LOD meshes that use a texture
                                 the full model replaces with textures\pbr\...
          --pbr-lod-brightness <pct> Brightness of the converted copies, 10-150 (default 100 = DynDOLOD's PBR scale 0.65)
          --pbr-lod-size <px>    Largest side of a converted copy: 256, 512 (default), 1024 or 2048
          --underside            Build the terrain underside for volumetric lighting mods (DVLaSS, EVLaS, Community
                                 Shaders sky sync): meshes\Terrain\<ws>\<ws>_Underside.nif plus AnvilLOD.esp
                                 (flagged ESL, no plugin slot). Enable the ESM; load it after plugins that edit worldspaces
          --underside-detail <n> LAND vertices per underside quad: 4, 8, 16 (default), 32, 64 or 128. Smaller is finer and heavier
          --no-enable-parented   Exclude every reference with an enable parent (by default only
                                 parented refs that start disabled are left out)

        Example: AnvilLOD scan --mo2 "D:\Modlists\MyList" --worldspace Tamriel
        """;

    /// <summary>Refuses options that would otherwise be silently ignored.</summary>
    private static CliArgs Checked(CliArgs a)
    {
        _ = Mo2LocationsFromArgs(a);
        return CheckedLargeRefs(a);
    }

    private static AnvilLOD.Plugins.Mo2.Mo2Locations? Mo2LocationsFromArgs(CliArgs a)
    {
        var l = new AnvilLOD.Plugins.Mo2.Mo2Locations(a.Get("mo2-game"), a.Get("mo2-mods"), a.Get("mo2-profiles"), a.Get("mo2-overwrite"));
        if (l.IsEmpty) return null;
        if (a.Get("mo2") is null) throw new CliArgException("--mo2-game, --mo2-mods, --mo2-profiles and --mo2-overwrite only apply together with --mo2 <instance folder>.");
        return l;
    }

    private static CliArgs CheckedLargeRefs(CliArgs a)
    {
        if (a.Has("large-refs-no-esl") && !a.Has("large-refs"))
            throw new CliArgException("--large-refs-no-esl only applies together with --large-refs.");
        return a;
    }

    private static int PbrLodSizeFromArgs(CliArgs a)
    {
        if (a.Get("pbr-lod-size") is not { } v) return 512;
        if (!a.Has("pbr-lod") && a.Get("pbr-lod-brightness") is null) throw new CliArgException("--pbr-lod-size only applies together with --pbr-lod.");
        if (!int.TryParse(v, out var px) || px is not (256 or 512 or 1024 or 2048))
            throw new CliArgException($"--pbr-lod-size must be 256, 512, 1024 or 2048 (got '{v}').");
        return px;
    }

    private static int UndersideStepFromArgs(CliArgs a)
    {
        if (a.Get("underside-detail") is not { } v) return AnvilLOD.Core.World.UndersideMesher.DefaultStep;
        if (!a.Has("underside"))
            throw new CliArgException("--underside-detail only applies together with --underside.");
        if (!int.TryParse(v, out var step) || !AnvilLOD.Core.World.UndersideMesher.IsValidStep(step))
            throw new CliArgException($"--underside-detail must be 4, 8, 16, 32, 64 or 128 (got '{v}').");
        return step;
    }

    private static Tree3DSettings? Tree3DFromArgs(CliArgs a)
    {
        if (!a.Has("tree-3d"))
        {
            if (a.Has("tree-3d-lod8") || a.Has("tree-3d-by-name"))
                throw new CliArgException("--tree-3d-lod8 and --tree-3d-by-name only apply together with --tree-3d.");
            return null;
        }
        return new Tree3DSettings(Enabled: true, Lod8: a.Has("tree-3d-lod8"), PlainNameFallback: a.Has("tree-3d-by-name"));
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "scan" => RunScan(Checked(CliArgs.Parse(args[1..])), generate: false),
                "generate" => RunScan(Checked(CliArgs.Parse(args[1..])), generate: true),
                "author" => RunAuthor(Checked(CliArgs.Parse(args[1..]))),
                "lodmaker" => RunLodMaker(Checked(CliArgs.Parse(args[1..]))),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (CliArgException e)
        {
            return Fail(e.Message + "\n\n" + Usage);
        }
    }

    private static int RunScan(CliArgs a, bool generate)
    {
        var total = Stopwatch.StartNew();
        var gameOptions = a.Get("mo2") is { } mo2Folder
            ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2Folder, Mo2Profile: a.Get("profile"), Mo2Locations: Mo2LocationsFromArgs(a))
            : new GameContextOptions(a.Get("data"), a.Get("plugins"));
        var outputFolder = a.Get("output") ?? (generate ? OutputFolder.DefaultFolder(gameOptions) : null);
        if (generate && a.Get("output") is null) Console.WriteLine($"No --output given: using {outputFolder}");

        var req = new ScanRequest(
            gameOptions,
            new ScanOptions(
                Worldspaces: a.GetAll("worldspace"),
                IncludeInitiallyDisabled: a.Has("include-disabled"),
                IncludeEnableParented: !a.Has("no-enable-parented"),
                TreeLod: !a.Has("no-trees"),
                DynamicLod: !a.Has("no-dynamic"),
                GridObjects: !a.Has("no-grid-objects"),
                Tree3D: Tree3DFromArgs(a)),
            OutputFolder: outputFolder,
            CleanOutput: !a.Has("keep-output"),
            Generate: generate,
            DynDolodFolder: a.Get("dyndolod"),
            Preset: ParsePreset(a.Get("preset")),
            Candles: a.Has("candles"),
            FxGlow: a.Has("fxglow"),
            CustomRulesFile: a.Get("rules"),
            RemoveBuried: !a.Has("keep-buried"),
            GrassLod: !a.Has("no-grass"),
            ChildWorlds: !a.Has("no-child-worlds"),
            Seasons: a.Has("seasons"),
            Underside: a.Has("underside"),
            PbrLod: a.Has("pbr-lod"),
            LargeReferences: a.Has("large-refs"),
            LargeRefsEsl: !a.Has("large-refs-no-esl"),
            PbrLodBrightness: (a.Get("pbr-lod-brightness") is { } pb && float.TryParse(pb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pbp) ? Math.Clamp(pbp, 10f, 150f) : 100f) / 100f,
            PbrLodSize: PbrLodSizeFromArgs(a),
            UndersideStep: UndersideStepFromArgs(a),
            SkseDll: (a.Get("skse-dll") ?? "auto").ToLowerInvariant() switch
            {
                "auto" => SkseDllChoice.Auto,
                "1170" or "old" or "se" or "ae" => SkseDllChoice.UpTo1170,
                "17" or "new" or "1.7" => SkseDllChoice.Newer,
                "vr" or "1.4.15" => SkseDllChoice.Vr,
                "none" or "separate" => SkseDllChoice.None,
                var v => throw new CliArgException($"--skse-dll must be auto, 1170, 17, vr or none (got '{v}')."),
            },
            ObjectBrightness: (a.Get("object-brightness") is { } ob && float.TryParse(ob, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var obp) ? Math.Clamp(obp, 10f, 110f) : 100f) / 100f,
            TreeBrightness: (a.Get("tree-brightness") is { } tb && float.TryParse(tb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tbp) ? tbp : 100f) / 100f,
            GrassDensity: (a.Get("grass-density") is { } gd && float.TryParse(gd, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 8f) / 100f);

        // Progress<T> posts asynchronously; a synchronous reporter keeps console output ordered.
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var s = ScanPipeline.Run(req, sync);
        total.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Scan summary ===");
        Console.WriteLine($"Plugins:              {s.PluginsLoaded:N0} found of {s.Stats.PluginsInLoadOrder:N0} listed");
        Console.WriteLine($"Worldspaces w/ .lod:  {s.Grids.Count} ({string.Join(", ", s.Grids.Keys.Take(8))}{(s.Grids.Count > 8 ? ", ..." : "")})");
        Console.WriteLine($"REFRs visited:        {s.Stats.PlacedObjectsVisited:N0}");
        Console.WriteLine($"LOD references:       {s.Stats.LodReferencesFound:N0}");
        Console.WriteLine($"Skipped (disabled):   {s.Stats.SkippedDisabled:N0}");
        Console.WriteLine($"Skipped (no mesh):    {s.Stats.SkippedMissingMesh:N0}");
        Console.WriteLine($"Outside LOD grid:     {s.SkippedOutsideGrid:N0}");
        Console.WriteLine($"Missing LOD meshes:   {s.Stats.MissingMeshes.Count:N0} unique paths");
        Console.WriteLine($"Dynamic LOD refs:     {s.DynamicRefs:N0}");
        Console.WriteLine($"Grass cache cells:    {s.GrassCells:N0}" + (s.MissingGrassBillboards is { Count: > 0 } mg ? $" ({mg.Count} grass types without billboard)" : ""));
        Console.WriteLine($"Trees with billboard: {s.TreeReferences:N0} ({s.MissingBillboards?.Count ?? 0:N0} tree types without one)");
        foreach (var level in LodLevels.All)
            Console.WriteLine($"  LOD{(int)level,-2} blocks:        {s.Quads.Keys.Count(q => q.Level == level):N0}");
        Console.WriteLine($"Build plan:           {s.Plan.Rebuild.Count:N0} rebuild, {s.Plan.Unchanged.Count:N0} unchanged, {s.Plan.StaleFiles.Count:N0} stale");
        Console.WriteLine();
        Console.WriteLine($"Timing: index {s.IndexTime.TotalSeconds:F1}s | scan {s.Stats.Elapsed.TotalSeconds:F1}s | bucket+hash {s.BucketAndHashTime.TotalSeconds:F2}s | total {total.Elapsed.TotalSeconds:F1}s");

        if (s.Generation is { } g)
        {
            Console.WriteLine($"Generated:            {g.BlocksWritten:N0} blocks, {g.Triangles:N0} triangles ({g.TrianglesCulled:N0} buried removed), {g.MeshesLoaded:N0} meshes, {g.Elapsed.TotalSeconds:F1}s");
            if (s.StaleFilesDeleted > 0) Console.WriteLine($"Removed stale:        {s.StaleFilesDeleted:N0} blocks");
            if (s.Trees is { } t)
                Console.WriteLine($"Tree LOD:             {t.Instances:N0} trees, {t.TreeTypes} types, {t.Blocks:N0} blocks, {t.EmptyBlocks:N0} vanilla blocks emptied, {t.Elapsed.TotalSeconds:F1}s");
            foreach (var (mesh, err) in g.MeshErrors.Take(20)) Console.WriteLine($"  mesh problem: {mesh}: {err}");
        }
        foreach (var w in s.Warnings) Console.WriteLine($"WARNING: {w}");

        var reportPath = a.Get("report") ?? Path.Combine(AppContext.BaseDirectory, "AnvilLOD.scan.json");
        ScanReport.Write(reportPath, s, total.Elapsed);
        Console.WriteLine($"Report: {reportPath}");
        return 0;
    }

    private static int RunAuthor(CliArgs a)
    {
        var plugin = a.Get("plugin") ?? throw new CliArgException("author needs --plugin <file name>, e.g. --plugin \"The Archwood.esp\".");
        var output = a.Get("author-out") ?? throw new CliArgException("author needs --author-out <folder> (a new, separate mod folder).");
        float F(string key, float def) => a.Get(key) is { } v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        var total = Stopwatch.StartNew();
        var req = new AnvilLOD.Plugins.Authoring.AuthorRequest(
            a.Get("mo2") is { } mo2
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2, Mo2Profile: a.Get("profile"), Mo2Locations: Mo2LocationsFromArgs(a))
                : new GameContextOptions(a.Get("data"), a.Get("plugins")),
            plugin, output,
            LodMeshes: !a.Has("no-meshes"),
            Billboards: !a.Has("no-billboards"),
            RuleFile: !a.Has("no-rules"),
            MinObjectSize: F("min-size", 400f),
            MinTreeHeight: F("min-tree-height", 256f),
            Overwrite: a.Has("overwrite"),
            DynDolodFolder: a.Get("dyndolod"),
            Preset: ParsePreset(a.Get("preset")),
            Budget: ParseBudget(a.Get("budget")).Mode,
            CustomBudget: ParseBudget(a.Get("budget")).Custom);
        Console.WriteLine("WARNING: " + AnvilLOD.Plugins.Authoring.ModAuthorTool.Warning);
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var r = AnvilLOD.Plugins.Authoring.ModAuthorTool.Run(req, sync);
        Console.WriteLine();
        foreach (var g in r.Items.GroupBy(i => (i.Kind, Status: i.Status.Split(':')[0])).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Status))
            Console.WriteLine($"  {g.Key.Kind,-7} {g.Key.Status,-45} {g.Count(),5}");
        Console.WriteLine($"Generated {r.MeshesWritten} LOD meshes, {r.BillboardsWritten} billboards{(r.RuleFile is null ? "" : ", rule file " + r.RuleFile)}");
        Console.WriteLine($"Report: {r.ReportFile}");
        return 0;
    }

    private static (AnvilLOD.Meshes.Authoring.BudgetMode Mode, IReadOnlyList<int>? Custom) ParseBudget(string? v)
    {
        if (v is null || v.Equals("recommended", StringComparison.OrdinalIgnoreCase)) return (AnvilLOD.Meshes.Authoring.BudgetMode.Recommended, null);
        if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) return (AnvilLOD.Meshes.Authoring.BudgetMode.Off, null);
        var nums = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) && n >= 0 ? n : -1).ToList();
        if (nums.Count is < 1 or > 3 || nums.Any(n => n < 0))
            throw new CliArgException($"--budget must be recommended, off, or up to three numbers LOD0,LOD1,LOD2 (got '{v}').");
        while (nums.Count < 3) nums.Add(0);
        return (AnvilLOD.Meshes.Authoring.BudgetMode.Custom, nums);
    }

    private static int RunLodMaker(CliArgs a)
    {
        var inputs = a.GetAll("input") ?? throw new CliArgException("lodmaker needs at least one --input <file or folder>.");
        string output;
        if (a.Get("new-mod") is { } modName)
        {
            var mo2 = a.Get("mo2") ?? throw new CliArgException("--new-mod needs --mo2 <instance folder>, to find the mods folder.");
            if (a.Get("output") is not null) throw new CliArgException("Use either --output or --new-mod, not both.");
            output = Path.Combine(AnvilLOD.Plugins.Mo2.Mo2Instance.Open(mo2, Mo2LocationsFromArgs(a)).ModsFolder, modName.Trim());
        }
        else output = a.Get("output") ?? throw new CliArgException("lodmaker needs --output <folder> (or --mo2 <instance> --new-mod <name>).");

        float F(string key, float def) => a.Get(key) is { } v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        IReadOnlyList<int>? levels = null;
        if (a.Get("levels") is { } lv)
        {
            var parsed = lv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) ? n : -1).ToList();
            if (parsed.Count == 0 || parsed.Any(n => n is < 0 or > 2)) throw new CliArgException($"--levels must be a list of 0, 1 and 2 (got '{lv}').");
            levels = parsed;
        }
        var detail = F("detail", 1f);
        if (!(detail >= 0.1f && detail <= 10f)) throw new CliArgException("--detail must be between 0.1 and 10.");
        var group = a.Get("group") ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(output));
        var (budget, custom) = ParseBudget(a.Get("budget"));

        var total = Stopwatch.StartNew();
        var req = new AnvilLOD.Plugins.Authoring.LodMakerRequest(inputs.ToList(), output, group, levels, detail, F("min-size", 0f),
            RuleFile: !a.Has("no-rules"), Overwrite: a.Has("overwrite"), Budget: budget, CustomBudget: custom);
        Console.WriteLine("WARNING: " + AnvilLOD.Plugins.Authoring.LodMeshMaker.Warning);
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var r = AnvilLOD.Plugins.Authoring.LodMeshMaker.Run(req, sync);
        Console.WriteLine();
        foreach (var g in r.Items.GroupBy(i => i.Status.Split(':')[0]).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-48} {g.Count(),5}");
        Console.WriteLine($"{r.ModelsDone} models, {r.MeshesWritten} LOD meshes{(r.RuleFile is null ? "" : ", rule file " + r.RuleFile)}");
        Console.WriteLine($"Output: {output}");
        Console.WriteLine($"Report: {r.ReportFile}");
        return 0;
    }

    private static AnvilLOD.Core.Lod.LodPreset ParsePreset(string? s) => s?.ToLowerInvariant() switch
    {
        null or "high" => AnvilLOD.Core.Lod.LodPreset.High,
        "medium" => AnvilLOD.Core.Lod.LodPreset.Medium,
        "low" => AnvilLOD.Core.Lod.LodPreset.Low,
        _ => throw new CliArgException($"Unknown preset '{s}' (use low, medium or high)."),
    };

    private static int Fail(string msg)
    {
        Console.Error.WriteLine(msg);
        return 2;
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
