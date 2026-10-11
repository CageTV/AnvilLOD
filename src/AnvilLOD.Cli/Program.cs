using System.Diagnostics;
using AnvilLOD.Core.Localization;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;

namespace AnvilLOD.Cli;

public static class Program
{
    /// <summary>Picks up and removes --lang before anything is parsed, so the messages come in the wanted language wherever it sits on the line.</summary>
    private static string[] ApplyLanguageArg(string[] args)
    {
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Locale.Set(args[++i]);
                continue;
            }
            rest.Add(args[i]);
        }
        return rest.ToArray();
    }

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
        if (a.Get("mo2") is null) throw new CliArgException(L.T("Err_Mo2Locations"));
        return l;
    }

    private static CliArgs CheckedLargeRefs(CliArgs a)
    {
        if (a.Has("large-refs-no-esl") && !a.Has("large-refs"))
            throw new CliArgException(L.T("Err_LargeRefsNoEsl"));
        return a;
    }

    private static int PbrLodSizeFromArgs(CliArgs a)
    {
        if (a.Get("pbr-lod-size") is not { } v) return 512;
        if (!a.Has("pbr-lod") && a.Get("pbr-lod-brightness") is null) throw new CliArgException(L.T("Err_PbrLodSizeOnly"));
        if (!int.TryParse(v, out var px) || px is not (256 or 512 or 1024 or 2048))
            throw new CliArgException(L.F("Err_PbrLodSizeFmt", v));
        return px;
    }

    private static int UndersideStepFromArgs(CliArgs a)
    {
        if (a.Get("underside-detail") is not { } v) return AnvilLOD.Core.World.UndersideMesher.DefaultStep;
        if (!a.Has("underside"))
            throw new CliArgException(L.T("Err_UndersideDetailOnly"));
        if (!int.TryParse(v, out var step) || !AnvilLOD.Core.World.UndersideMesher.IsValidStep(step))
            throw new CliArgException(L.F("Err_UndersideDetailFmt", v));
        return step;
    }

    private static Tree3DSettings? Tree3DFromArgs(CliArgs a)
    {
        if (!a.Has("tree-3d"))
        {
            if (a.Has("tree-3d-lod8") || a.Has("tree-3d-by-name") || a.Has("tree-3d-crc-only"))
                throw new CliArgException(L.T("Err_Tree3dOnly"));
            return null;
        }
        return new Tree3DSettings(Enabled: true, Lod8: a.Has("tree-3d-lod8"), PlainNameFallback: !a.Has("tree-3d-crc-only"));   // --tree-3d-by-name is still accepted: it is the default now
    }

    public static int Main(string[] args)
    {
        args = ApplyLanguageArg(args);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(L.T("Usage"));
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
                _ => Fail(L.F("Err_UnknownCommandFmt", args[0]) + "\n\n" + L.T("Usage")),
            };
        }
        catch (CliArgException e)
        {
            return Fail(e.Message + "\n\n" + L.T("Usage"));
        }
    }

    private static int RunScan(CliArgs a, bool generate)
    {
        var total = Stopwatch.StartNew();
        var gameOptions = a.Get("mo2") is { } mo2Folder
            ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2Folder, Mo2Profile: a.Get("profile"), Mo2Locations: Mo2LocationsFromArgs(a))
            : new GameContextOptions(a.Get("data"), a.Get("plugins"));
        var outputFolder = a.Get("output") ?? (generate ? OutputFolder.DefaultFolder(gameOptions) : null);
        if (generate && a.Get("output") is null) Console.WriteLine(L.F("Out_NoOutputFmt", outputFolder));

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
                var v => throw new CliArgException(L.F("Err_SkseDllFmt", v)),
            },
            ObjectBrightness: (a.Get("object-brightness") is { } ob && float.TryParse(ob, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var obp) ? Math.Clamp(obp, 10f, 110f) : 100f) / 100f,
            TreeBrightness: (a.Get("tree-brightness") is { } tb && float.TryParse(tb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tbp) ? tbp : 100f) / 100f,
            GrassDensity: (a.Get("grass-density") is { } gd && float.TryParse(gd, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 8f) / 100f,
            WaterStandIns: a.Has("water-standins"),
            GrassTop: (a.Get("grass-top") is { } gt && float.TryParse(gt, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gtp) ? Math.Clamp(gtp, 20f, 120f) : 85f) / 100f,
            GrassBottom: (a.Get("grass-bottom") is { } gb && float.TryParse(gb, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gbp) ? Math.Clamp(gbp, 10f, 120f) : 50f) / 100f);

        // Progress<T> posts asynchronously; a synchronous reporter keeps console output ordered.
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var s = ScanPipeline.Run(req, sync);
        total.Stop();

        Console.WriteLine();
        Console.WriteLine(L.T("Sum_Header"));
        Console.WriteLine(L.F("Sum_PluginsFmt", s.PluginsLoaded, s.Stats.PluginsInLoadOrder));
        Console.WriteLine(L.F("Sum_WorldspacesFmt", s.Grids.Count, string.Join(", ", s.Grids.Keys.Take(8)), s.Grids.Count > 8 ? L.T("Sum_More") : ""));
        Console.WriteLine(L.F("Sum_REFRsFmt", s.Stats.PlacedObjectsVisited));
        Console.WriteLine(L.F("Sum_LodRefsFmt", s.Stats.LodReferencesFound));
        Console.WriteLine(L.F("Sum_SkippedDisabledFmt", s.Stats.SkippedDisabled));
        Console.WriteLine(L.F("Sum_SkippedNoMeshFmt", s.Stats.SkippedMissingMesh));
        Console.WriteLine(L.F("Sum_OutsideGridFmt", s.SkippedOutsideGrid));
        Console.WriteLine(L.F("Sum_MissingMeshesFmt", s.Stats.MissingMeshes.Count));
        Console.WriteLine(L.F("Sum_DynamicFmt", s.DynamicRefs));
        Console.WriteLine(L.F("Sum_GrassCellsFmt", s.GrassCells, s.MissingGrassBillboards is { Count: > 0 } mg ? L.F("Sum_GrassMissingFmt", mg.Count) : ""));
        Console.WriteLine(L.F("Sum_TreesFmt", s.TreeReferences, s.MissingBillboards?.Count ?? 0));
        foreach (var level in LodLevels.All)
            Console.WriteLine(L.F("Sum_LodBlockFmt", (int)level, s.Quads.Keys.Count(q => q.Level == level)));
        Console.WriteLine(L.F("Sum_PlanFmt", s.Plan.Rebuild.Count, s.Plan.Unchanged.Count, s.Plan.StaleFiles.Count));
        Console.WriteLine();
        Console.WriteLine(L.F("Sum_TimingFmt", s.IndexTime.TotalSeconds, s.Stats.Elapsed.TotalSeconds, s.BucketAndHashTime.TotalSeconds, total.Elapsed.TotalSeconds));

        if (s.Generation is { } g)
        {
            Console.WriteLine(L.F("Sum_GeneratedFmt", g.BlocksWritten, g.Triangles, g.TrianglesCulled, g.MeshesLoaded, g.Elapsed.TotalSeconds));
            if (s.StaleFilesDeleted > 0) Console.WriteLine(L.F("Sum_StaleFmt", s.StaleFilesDeleted));
            if (s.Trees is { } t)
                Console.WriteLine(L.F("Sum_TreeLodFmt", t.Instances, t.TreeTypes, t.Blocks, t.EmptyBlocks, t.Elapsed.TotalSeconds));
            foreach (var (mesh, err) in g.MeshErrors.Take(20)) Console.WriteLine(L.F("Sum_MeshProblemFmt", mesh, err));
        }
        foreach (var w in s.Warnings) Console.WriteLine($"WARNING: {w}");

        var reportPath = a.Get("report") ?? Path.Combine(AppContext.BaseDirectory, "AnvilLOD.scan.json");
        ScanReport.Write(reportPath, s, total.Elapsed);
        Console.WriteLine(L.F("Sum_ReportFmt", reportPath));
        return 0;
    }

    private static int RunAuthor(CliArgs a)
    {
        var plugin = a.Get("plugin") ?? throw new CliArgException(L.T("Err_AuthorPlugin"));
        var output = a.Get("author-out") ?? throw new CliArgException(L.T("Err_AuthorOut"));
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
        Console.WriteLine(L.F("Author_GeneratedFmt", r.MeshesWritten, r.BillboardsWritten, r.RuleFile is null ? "" : L.F("Author_RulesFileFmt", r.RuleFile)));
        Console.WriteLine(L.F("Sum_ReportFmt", r.ReportFile));
        return 0;
    }

    private static (AnvilLOD.Meshes.Authoring.BudgetMode Mode, IReadOnlyList<int>? Custom) ParseBudget(string? v)
    {
        if (v is null || v.Equals("recommended", StringComparison.OrdinalIgnoreCase)) return (AnvilLOD.Meshes.Authoring.BudgetMode.Recommended, null);
        if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) return (AnvilLOD.Meshes.Authoring.BudgetMode.Off, null);
        var nums = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) && n >= 0 ? n : -1).ToList();
        if (nums.Count is < 1 or > 3 || nums.Any(n => n < 0))
            throw new CliArgException(L.F("Err_BudgetFmt", v));
        while (nums.Count < 3) nums.Add(0);
        return (AnvilLOD.Meshes.Authoring.BudgetMode.Custom, nums);
    }

    private static int RunLodMaker(CliArgs a)
    {
        var inputs = a.GetAll("input") ?? throw new CliArgException(L.T("Err_LodmakerInput"));
        string output;
        if (a.Get("new-mod") is { } modName)
        {
            var mo2 = a.Get("mo2") ?? throw new CliArgException(L.T("Err_NewModNeedsMo2"));
            if (a.Get("output") is not null) throw new CliArgException(L.T("Err_OutputOrNewMod"));
            output = Path.Combine(AnvilLOD.Plugins.Mo2.Mo2Instance.Open(mo2, Mo2LocationsFromArgs(a)).ModsFolder, modName.Trim());
        }
        else output = a.Get("output") ?? throw new CliArgException(L.T("Err_LodmakerOutput"));

        float F(string key, float def) => a.Get(key) is { } v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        IReadOnlyList<int>? levels = null;
        if (a.Get("levels") is { } lv)
        {
            var parsed = lv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) ? n : -1).ToList();
            if (parsed.Count == 0 || parsed.Any(n => n is < 0 or > 2)) throw new CliArgException(L.F("Err_LevelsFmt", lv));
            levels = parsed;
        }
        var detail = F("detail", 1f);
        if (!(detail >= 0.1f && detail <= 10f)) throw new CliArgException(L.T("Err_Detail"));
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
        Console.WriteLine(L.F("Maker_SummaryFmt", r.ModelsDone, r.MeshesWritten, r.RuleFile is null ? "" : L.F("Maker_RulesFileFmt", r.RuleFile)));
        Console.WriteLine(L.F("Maker_OutputFmt", output));
        Console.WriteLine(L.F("Sum_ReportFmt", r.ReportFile));
        return 0;
    }

    private static AnvilLOD.Core.Lod.LodPreset ParsePreset(string? s) => s?.ToLowerInvariant() switch
    {
        null or "high" => AnvilLOD.Core.Lod.LodPreset.High,
        "medium" => AnvilLOD.Core.Lod.LodPreset.Medium,
        "low" => AnvilLOD.Core.Lod.LodPreset.Low,
        _ => throw new CliArgException(L.F("Err_PresetFmt", s)),
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
