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

        Options:
          --mo2 <path>           MO2 instance folder (with ModOrganizer.ini) — reads mods directly, no need to launch from MO2
          --profile <name>       MO2 profile (default: the instance's selected profile)
          --data <path>          Skyrim Data folder (default: auto-detect / MO2 VFS)
          --plugins <path>       plugins.txt to use (requires --data)
          --worldspace <edid>    Limit to a worldspace; repeatable (default: all with a .lod file)
          --output <path>        Output folder; used to diff against the previous build's manifest
          --report <file.json>   Write a JSON report (default: AnvilLOD.scan.json next to the exe)
          --dyndolod <path>      DynDOLOD install folder: reuse its LOD rule files (optional)
          --preset <name>        low | medium | high (default high): which rule preset to use
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
          --no-enable-parented   Exclude every reference with an enable parent (by default only
                                 parented refs that start disabled are left out)

        Example: AnvilLOD scan --mo2 "E:\Tabula Rasa" --worldspace Tamriel
        """;

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
                "scan" => RunScan(CliArgs.Parse(args[1..]), generate: false),
                "generate" => RunScan(CliArgs.Parse(args[1..]), generate: true),
                "author" => RunAuthor(CliArgs.Parse(args[1..])),
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
        if (generate && a.Get("output") is null)
            throw new CliArgException("generate needs --output <folder> (e.g. a new, enabled MO2 mod folder).");
        var total = Stopwatch.StartNew();

        var req = new ScanRequest(
            a.Get("mo2") is { } mo2
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2, Mo2Profile: a.Get("profile"))
                : new GameContextOptions(a.Get("data"), a.Get("plugins")),
            new ScanOptions(
                Worldspaces: a.GetAll("worldspace"),
                IncludeInitiallyDisabled: a.Has("include-disabled"),
                IncludeEnableParented: !a.Has("no-enable-parented"),
                TreeLod: !a.Has("no-trees"),
                DynamicLod: !a.Has("no-dynamic"),
                GridObjects: !a.Has("no-grid-objects")),
            OutputFolder: a.Get("output"),
            Generate: generate,
            DynDolodFolder: a.Get("dyndolod"),
            Preset: ParsePreset(a.Get("preset")),
            RemoveBuried: !a.Has("keep-buried"),
            GrassLod: !a.Has("no-grass"),
            ChildWorlds: !a.Has("no-child-worlds"),
            Seasons: a.Has("seasons"),
            SkseDll: (a.Get("skse-dll") ?? "auto").ToLowerInvariant() switch
            {
                "auto" => SkseDllChoice.Auto,
                "1170" or "old" or "se" or "ae" => SkseDllChoice.UpTo1170,
                "17" or "new" or "1.7" => SkseDllChoice.Newer,
                "none" or "separate" => SkseDllChoice.None,
                var v => throw new CliArgException($"--skse-dll must be auto, 1170, 17 or none (got '{v}')."),
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
                ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2, Mo2Profile: a.Get("profile"))
                : new GameContextOptions(a.Get("data"), a.Get("plugins")),
            plugin, output,
            LodMeshes: !a.Has("no-meshes"),
            Billboards: !a.Has("no-billboards"),
            RuleFile: !a.Has("no-rules"),
            MinObjectSize: F("min-size", 400f),
            MinTreeHeight: F("min-tree-height", 256f),
            Overwrite: a.Has("overwrite"),
            DynDolodFolder: a.Get("dyndolod"),
            Preset: ParsePreset(a.Get("preset")));
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
