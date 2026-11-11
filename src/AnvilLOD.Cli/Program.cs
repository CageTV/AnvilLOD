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
          AnvilLOD dyncheck --dyndolod-output <folder> --mo2 <instance> [--csv <file>]
                                         Read-only: DynDOLOD's dynamic LOD references (DynDOLOD_Data\*_Objects.txt) against the
                                         load order as it is now: sources that moved, were deleted or are gone. Writes nothing.
          AnvilLOD stale --baseline <file> --snapshot|--check|--refresh [scan options]
                                         Read-only staleness report for LOD another tool made (DynDOLOD, xLODGen).
                                         --snapshot right after that tool finished records what every object-LOD block
                                         was built from; --check later lists the blocks whose inputs changed (new, moved
                                         or removed references, changed LOD/full meshes or textures, terrain).
                                         --dyndolod-output <folder> marks which stale blocks exist in that output,
                                         --csv <file> writes the list. Use the same scan options for both.
                                         --refresh --output <folder> writes ONLY the stale blocks (new LOD4/8/16/32 .bto)
                                         into that folder, to load as a mod after the original LOD output so
                                         it wins; the snapshot is then updated to the new state. Object LOD blocks only:
                                         no plugins, dynamic LOD or tree LOD (.btt) are touched.
          AnvilLOD author --plugin <file> --author-out <folder> [options]
                                         Mod author tools: report the plugin's objects and trees that have no
                                         LOD, and generate LOD meshes, tree billboards and a rule file for them
                                         into their own folder. Options: --no-meshes --no-billboards --no-rules
                                         --min-size <units, default 400> --min-tree-height <units, default 256>
                                         --overwrite (replace files already in --author-out)
                                         --budget recommended|off|<LOD0,LOD1,LOD2> triangle budget for the LOD meshes (default
                                         recommended, by model size; see lodmaker)

          AnvilLOD fix-textures (--input <folder> [--input ...] | --mo2 <instance> [--profile <name>]) [options]
                                         Finds textures Skyrim SE cannot load (BC6H, a format that crashes the game when a shader
                                         samples it) and converts them to BC7. Without --apply it only lists them. Options:
                                         --apply  --output <folder> (put the fixed files there, same relative paths: a new mod that
                                         overrides the broken ones) or --in-place --backup <folder> (replace them; the originals are
                                         copied to the backup folder first)  --texconv <texconv.exe or a folder that has it>
                                         (default: found in the xLODGen / SSEEdit / DynDOLOD folders "Edit Scripts" or on the path)
                                         --mo2 scans every enabled mod of the profile and Overwrite.

          AnvilLOD convert-lod --source <mod folder> --output <folder> [--all-meshes] [--overwrite]
                                         Skyrim LE (Oldrim) -> SE: converts the static LOD meshes of a mod folder (loose files and its BSAs)
                                         to Skyrim SE meshes under the same paths in --output. Collision is dropped; animated, skinned and
                                         particle meshes are left alone. --all-meshes converts every mesh, not just LOD ones.

          AnvilLOD lodmaker --input <file|folder> [--input ...] (--output <folder> | --mo2 <instance> --new-mod <name>) [options]
                                         LOD Mesh Maker: make LOD meshes (name_lod_0/1/2.nif, simplified, no collision, the
                                         model's own textures) from full models, plus a DynDOLOD-format rule file.
                                         --input is a .nif, a mod folder, its meshes folder or any folder of models.
                                         --new-mod <name> creates the mod folder in the MO2 mods folder (needs --mo2).
                                         Options: --group <name> (meshes\lod\<name>, default the output folder's name)
                                         --plugin <file> the plugin that places the models (e.g. TMDDirtRoads.esp): the rule
                                         file is named for it (DynDOLOD_SSE_tmddirtroadsesp.ini), the only name it is read under
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
          --keep-underwater      Don't remove LOD triangles that lie completely under water
          --include-disabled     Include initially-disabled references
          --no-child-worlds      Don't copy walled cities (Whiterun, Solitude, ...) into Tamriel's LOD
          --no-dynamic           Keep switchable references (quest-toggled) in static LOD instead of
                                 handing them to the AnvilLOD SKSE plugin
          --seasons              Seasonal object and grass LOD for Seasons of Skyrim (Data\Seasons\*_WIN.ini ...)
          --skse-dll <which>     SKSE plugin put in the output: auto (default, reads SkyrimSE.exe),
                                 1170 (SE 1.5.97 - AE 1.6.1170), 17 (newer, 1.7.x), none (installed separately)
          --no-grid-objects      Don't hand water planes, waterfalls, fires and windmills (DynDOLOD
                                 "grid" objects) to the AnvilLOD SKSE plugin
          --glow-windows         (experimental) DynDOLOD's window glow: the <model>glow_lod_N.nif meshes of farmhouses, inns and forts are
                                 drawn by the SKSE plugin beyond the loaded cells and lit at night (AnvilLOD.dyn). Off by default
          --parent-copies        (experimental) DynDOLOD's Parent > Child copies: the objects around Whiterun, Solitude, Windhelm and
                                 Riften that the walled-city worldspaces leave out are drawn by the SKSE plugin while you are in the
                                 city (AnvilLOD.copies). Needs --dyndolod for its childworld configs. Off by default
          --no-match-textures    Keep each LOD mesh's own textures. By default they follow the full model
                                 (alternate textures, snow/ice variants), the way DynDOLOD corrects them
          --no-grass             Don't build grass LOD from the grass cache
          --water-standins       Stream/creek/pond water planes (grid objects) that use a real water shader, which
                                 draws black beyond the loaded cells, get a lit fake-water stand-in mesh in the output
          --grass-density <pct>  Share of cached grass kept as LOD, 1-100 (default 8; 15 is high, 100 keeps every blade)
          --grass-top <pct>      Grass LOD brightness at the top of a tuft, 20-120 (default 85), like DynDOLOD's GrassBrightnessTop
          --grass-bottom <pct>   Grass LOD brightness at the roots, 10-120 (default 50), like GrassBrightnessBottom
          --tree-brightness <pct> Tree LOD billboard brightness, 10-110 (default 100)
          --object-brightness <pct> Object LOD brightness, 10-110 (default 100)
          --esl-range <auto|2048|4096>  Records per ESL effects plugin: auto reads the installed game (4096 on 1.6.1130 or newer, else 2048)
          --native-grid-objects     Waterfalls, windmill fans and water wheels (and Animated Ships' sailing ships) as real references in AnvilLOD.esp, which the engine animates
          --native-effects          Fires, candles, torches, lava and streams as real references too (second plugin, AnvilLOD Effects.esp)
          --no-cs-grass             Don't make LOD meshes for Community Shaders' Grass Optimizations (meshes\LOD\Grass; made only when the feature is installed)
          --no-animated-ships       Don't draw Animated Ships' sailing ships in the distance (they need the native grid objects; on by default when the mod is in the load order)
          --no-detailed-ships       Draw moored ships from the stock LOD meshes instead of simplified copies of their real models
          --ice-inset <units>       Pull glacier/ice LOD this far inside its surface so the full model wins the z-fight (default 6, 0 = off)
          --no-mountain-fit         Keep mountain LOD where its mesh has it (default: LOD4/LOD8 vertices outside the full mountain model are moved onto its surface, when a mountain inset is on)
          --mountain-inset <units>  The same for mountain LOD (default 12, 0 = off)
          --snow-cover <off|full|bright>  Vertex colour of snow/ash LOD shapes: full (default, alpha 1 = full snow intensity), off (as the meshes carry it), bright (full and lighter colour)
          --snow-shader <auto|plain|hd>  Snow/ash LOD shader on full-texture shapes: auto (default, DynDOLOD: HD at LOD4 only), plain (the plain shader at every level, so near mountains match far ones), hd (HD at every level)
          --object-inset <units>    The same for every other object in LOD4/LOD8 (buildings, walls, rocks; default 0 = off): smooths the swap to the full model
          --map-objects <mode>      LOD 32 objects for the world map: auto (default; level 0 when A Clear Map of Skyrim is loaded, else rules only), rules, 0, 1 or 2 (that level's LOD mesh)
          --no-map-trees            With --tree-3d: no tree cards in LOD 32, so no trees on the world map (LOD 4 to 16 unchanged)
          --occlusion               Work out the occlusion data (Occlusion.esp) ourselves instead of running xLODGen (EXPERIMENTAL)
          --occlusion-clearance <u> Units a hill must stand above the line of sight to hide what is behind it (default 3072; larger = safer, hides less)
          --occlusion-folder <dir>  Where Occlusion.esp goes (default: the output folder; also works with scan)
          --no-trees             Don't build billboard tree LOD (.lst/.btt + atlas)
          --render-trees         Trees without a TexGen billboard get one drawn from their own model, so TexGen is not needed
                                 for them (tall trees only; small plants TexGen skipped on purpose stay without LOD).
                                 The billboards are cached in %LOCALAPPDATA%\AnvilLOD\TreeBillboards; a real TexGen one wins.
          --tree-3d              Use 3D tree LOD models (DynDOLOD passthru_lod.nif, matched by the CRC32 of the
                                 tree's mesh) in object LOD: 3D at LOD4, billboard cards from LOD8 on.
                                 Trees without a model keep billboard tree LOD.
          --tree-3d-lod8         With --tree-3d: use the 3D model at LOD8 too (bigger files)
          --tree-3d-crc-only     With --tree-3d: only use models whose file name carries the tree mesh's CRC32.
                                 Default: when none matches, the model stored under the plain tree name is used
                                 (how DynDOLOD Resources ships almost all of its models, and how DynDOLOD matches)
          --tree-3d-dyndolod-first
                                 With --tree-3d: use a DynDOLOD-style model (CRC32 or plain name) before a tree mod's own LOD
                                 model (<tree>_LOD_0.nif), as DynDOLOD does. Default: the mod's own LOD model wins when
                                 it has one (it needs its TexGen trunk billboards); trees without one keep the other kind.
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
            if (a.Has("tree-3d-lod8") || a.Has("tree-3d-by-name") || a.Has("tree-3d-crc-only") || a.Has("tree-3d-own-lod") || a.Has("tree-3d-dyndolod-first"))
                throw new CliArgException("--tree-3d-lod8, --tree-3d-by-name, --tree-3d-crc-only and --tree-3d-dyndolod-first only apply together with --tree-3d.");
            return null;
        }
        return new Tree3DSettings(Enabled: true, Lod8: a.Has("tree-3d-lod8"), PlainNameFallback: !a.Has("tree-3d-crc-only"), MapTrees: !a.Has("no-map-trees"), PreferNamedLod: !a.Has("tree-3d-dyndolod-first"));   // --tree-3d-own-lod (0.2.62) is still accepted: it is the default now   // --tree-3d-by-name is still accepted: it is the default now
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
                "stale" => RunStale(Checked(CliArgs.Parse(args[1..])), args[1..]),
                "dyncheck" => RunDynCheck(Checked(CliArgs.Parse(args[1..]))),
                "author" => RunAuthor(Checked(CliArgs.Parse(args[1..]))),
                "lodmaker" => RunLodMaker(Checked(CliArgs.Parse(args[1..]))),
                "convert-lod" => RunConvertLod(Checked(CliArgs.Parse(args[1..]))),
                "fix-textures" => RunFixTextures(Checked(CliArgs.Parse(args[1..]))),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (CliArgException e)
        {
            return Fail(e.Message + "\n\n" + Usage);
        }
    }

    private static readonly HashSet<string> StaleOnlyKeys = new(StringComparer.OrdinalIgnoreCase) { "baseline", "dyndolod-output", "csv", "report", "to" };

    private static int RunStale(CliArgs a, string[] raw)
    {
        bool snapshot = a.Has("snapshot"), check = a.Has("check"), refresh = a.Has("refresh"), rebase = a.Has("rebase-files");
        if ((snapshot ? 1 : 0) + (check ? 1 : 0) + (refresh ? 1 : 0) + (rebase ? 1 : 0) != 1) throw new CliArgException("stale needs exactly one of --snapshot, --check, --refresh or --rebase-files.");
        if (rebase && a.Get("to") is null) throw new CliArgException("--rebase-files needs --to <new baseline file>: the old baseline is not overwritten.");
        var baselinePath = a.Get("baseline") ?? throw new CliArgException("stale needs --baseline <file> (for example AnvilLOD.baseline.json.gz).");
        if (rebase && !File.Exists(baselinePath)) return Fail($"No snapshot at {baselinePath}.");
        if (!refresh && (a.Has("output") || a.Has("keep-output"))) throw new CliArgException("only --refresh writes LOD; --snapshot and --check never do, so --output does not apply.");
        if (refresh && a.Get("output") is null) throw new CliArgException("--refresh needs --output <folder> for the refreshed blocks (a folder of its own, not the original LOD output).");
        if (refresh && !File.Exists(baselinePath)) return Fail($"No snapshot at {baselinePath}. Run 'stale --snapshot' right after the LOD tool finishes.");

        // The scan options are part of the snapshot: a check with other options would compare unlike things.
        var kept = new List<string>();
        for (int i = 0; i < raw.Length; i++)
        {
            var key = raw[i].TrimStart('-').ToLowerInvariant();
            if (key is "snapshot" or "check" or "refresh" or "ignore-assets" or "rebase-files") continue;
            if (StaleOnlyKeys.Contains(key) || (refresh && key == "output")) { i++; continue; }
            kept.Add(raw[i]);
        }
        var options = string.Join(' ', kept);

        AnvilLOD.Core.Incremental.StaleReport? refreshReport = null;
        AnvilLOD.Core.Incremental.LodBaseline? refreshOld = null;
        Func<IReadOnlyDictionary<AnvilLOD.Core.World.QuadKey, IReadOnlyList<AnvilLOD.Core.World.LodReference>>, IReadOnlyDictionary<AnvilLOD.Core.World.QuadKey, string>, IReadOnlySet<string>>? selector = null;
        if (refresh)
            selector = (quads, hashes) =>
            {
                refreshOld = AnvilLOD.Core.Incremental.LodBaseline.Load(baselinePath);
                refreshReport = refreshOld.Diff(quads, hashes, quads.Keys.Select(q => q.Worldspace).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), options, a.Has("ignore-assets"));
                if (refreshReport.OptionsWarning is { } ow) Console.WriteLine("WARNING: " + ow);
                // A block that no longer exists has nothing to write; its old file stays in the original output (see the summary).
                return refreshReport.Stale.Where(d => d.Kind != AnvilLOD.Core.Incremental.BlockChange.Removed).Select(d => d.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            };

        return RunScan(a, generate: refresh, stale: true, refresh: selector, after: s =>
        {
            if (refresh)
            {
                int written = s.Generation?.BlocksWritten ?? 0, failed = s.Generation?.BlocksFailed ?? 0;
                var gone = refreshReport!.Stale.Where(d => d.Kind == AnvilLOD.Core.Incremental.BlockChange.Removed).ToList();
                Console.WriteLine();
                Console.WriteLine($"=== Refresh ===");
                Console.WriteLine($"Stale blocks: {refreshReport.Stale.Count:N0}; written to {a.Get("output")}: {written:N0}; failed: {failed:N0}.");
                if (gone.Count > 0)
                    Console.WriteLine($"{gone.Count:N0} blocks no longer have any LOD object. Their old files in the original output were NOT removed (an overlay can't delete them): {string.Join(", ", gone.Take(5).Select(d => Path.GetFileName(d.RelativePath)))}{(gone.Count > 5 ? ", ..." : "")}");
                if (failed > 0) { Console.WriteLine("Some blocks failed, so the snapshot was NOT updated; fix the errors above and run --refresh again."); return 1; }
                var nb = AnvilLOD.Core.Incremental.LodBaseline.Capture(s.Quads, s.QuadHashes, options, components: Comp(s));
                nb.Save(baselinePath);
                Console.WriteLine($"Snapshot updated: {baselinePath}. In MO2, enable that folder as a mod with HIGHER priority than the original LOD output, so its blocks win.");
                return 0;
            }
            if (rebase)
            {
                var baseOld = AnvilLOD.Core.Incremental.LodBaseline.Load(baselinePath);
                if (!string.Equals(baseOld.Options, options, StringComparison.Ordinal))
                    Console.WriteLine($"WARNING: the snapshot's scan options ('{baseOld.Options}') differ from these ('{options}'); repeat the same options or the grafted file state will not fit.");
                var rebased = baseOld.WithCurrentFiles(s.Quads, s.QuadHashes, Comp(s), out var adopted, out var keptStale);
                rebased.Save(a.Get("to")!);
                Console.WriteLine();
                Console.WriteLine($"Rebased: {adopted:N0} blocks adopt today's file state (their objects are unchanged since {baseOld.TakenAt:yyyy-MM-dd HH:mm}); {keptStale:N0} keep their old fingerprint and stay stale (objects changed or block gone).");
                Console.WriteLine($"Saved: {a.Get("to")} (still dated {baseOld.TakenAt:yyyy-MM-dd HH:mm}). Check against it with the same options; the original is untouched.");
                return 0;
            }
            if (snapshot)
            {
                var b = AnvilLOD.Core.Incremental.LodBaseline.Capture(s.Quads, s.QuadHashes, options, components: Comp(s));
                b.Save(baselinePath);
                Console.WriteLine();
                Console.WriteLine($"Snapshot saved: {baselinePath} ({b.Blocks.Count:N0} blocks, {b.Blocks.Values.Sum(x => x.Refs.Count):N0} reference entries, {new FileInfo(baselinePath).Length / 1048576.0:F1} MB)");
                return 0;
            }
            if (!File.Exists(baselinePath)) return Fail($"No snapshot at {baselinePath}. Run 'stale --snapshot' right after the LOD tool finishes.");
            var old = AnvilLOD.Core.Incremental.LodBaseline.Load(baselinePath);
            var report = old.Diff(s.Quads, s.QuadHashes, s.Grids.Keys.ToList(), options, a.Has("ignore-assets"), Comp(s));
            if (a.Get("dyndolod-output") is { } dyn)
                foreach (var d in report.Stale)
                    d.OnDisk = File.Exists(AnvilLOD.Core.Incremental.BuildManifest.FullPath(dyn, d.RelativePath));

            Console.WriteLine();
            Console.WriteLine($"=== Stale LOD report (snapshot of {old.TakenAt:yyyy-MM-dd HH:mm}) ===");
            if (report.OptionsWarning is { } w) Console.WriteLine("WARNING: " + w);
            Console.WriteLine($"Blocks compared: {report.BlocksCompared:N0}; unchanged {report.Unchanged:N0}; stale {report.Stale.Count:N0}");
            if (a.Has("ignore-assets"))
                Console.WriteLine($"  --ignore-assets: {report.AssetOnlyIgnored:N0} blocks differ only by a mesh, texture, terrain or water fingerprint (a support app rewrote files) and are counted as unchanged. Only object changes count here.");
            var nowHashing = s.ContentHashed ? "content" : "mtime";
            if (old.Hashing != nowHashing)
                Console.WriteLine($"NOTE: the snapshot fingerprints files by {old.Hashing}, this check by {nowHashing}, so blocks that differ only by files are not comparable. Use --ignore-assets, or take a new snapshot.");
            else if (report.Stale.Any(d => d.AssetOnly))
            {
                Console.WriteLine("Blocks whose objects are unchanged but whose fingerprint moved, by what changed (a block can be under several):");
                foreach (var (k, n) in report.AssetBreakdown().OrderBy(x => x.Key))
                    Console.WriteLine($"  {k,-9} {n,6:N0}");
                foreach (var part in new[] { AnvilLOD.Core.Incremental.AssetChange.Terrain, AnvilLOD.Core.Incremental.AssetChange.Files, AnvilLOD.Core.Incremental.AssetChange.Water })
                {
                    var worlds = report.Stale.Where(d => d.AssetOnly && d.AssetReasons.HasFlag(part)).GroupBy(d => d.Worldspace).OrderByDescending(g => g.Count()).Take(6).ToList();
                    if (worlds.Count > 0) Console.WriteLine($"    {part}: " + string.Join(", ", worlds.Select(g => $"{g.Key} ({g.Count()})")));
                }
            }
            if (report.Stale.Count == 0) Console.WriteLine("Nothing changed since the snapshot: no refresh needed.");
            else
            {
                foreach (var kind in new[] { AnvilLOD.Core.Incremental.BlockChange.Changed, AnvilLOD.Core.Incremental.BlockChange.New, AnvilLOD.Core.Incremental.BlockChange.Removed })
                {
                    var of = report.Stale.Where(d => d.Kind == kind).ToList();
                    if (of.Count == 0) continue;
                    Console.WriteLine($"  {kind,-8} {of.Count,6:N0} blocks" + (kind == AnvilLOD.Core.Incremental.BlockChange.Changed ? $" ({of.Count(d => d.AssetOnly):N0} only because a mesh/texture/terrain file changed)" : ""));
                }
                Console.WriteLine("By worldspace and level:");
                foreach (var g in report.ByWorldspace.OrderByDescending(g => g.Count()).Take(15))
                    Console.WriteLine($"  {g.Key,-28} {g.Count(),6:N0}  (" + string.Join(", ", g.GroupBy(d => d.Level).OrderBy(x => x.Key).Select(x => $"LOD{x.Key} {x.Count()}")) + ")");
                Console.WriteLine("Added or changed by (blocks touched / references):");
                foreach (var (plugin, blocks, refs) in report.ByPlugin().Take(20))
                    Console.WriteLine($"  {plugin,-48} {blocks,6:N0} blocks {refs,8:N0} refs");
                var gone = report.RemovedByPlugin().Take(10).ToList();
                if (gone.Count > 0)
                {
                    Console.WriteLine("No longer in LOD, previously from (a later plugin disabled or replaced them; this names the plugin that owned them, not the one that removed them):");
                    foreach (var (plugin, blocks, refs) in gone)
                        Console.WriteLine($"  {plugin,-48} {blocks,6:N0} blocks {refs,8:N0} refs");
                }
                if (a.Get("dyndolod-output") is not null)
                {
                    int have = report.Stale.Count(d => d.OnDisk == true);
                    Console.WriteLine($"In the DynDOLOD output: {have:N0} of the stale blocks exist there (refresh), {report.Stale.Count - have:N0} do not (new LOD needed, or the block was never made).");
                }
            }
            if (a.Get("csv") is { } csv) { File.WriteAllText(csv, report.ToCsv()); Console.WriteLine($"List: {csv}"); }
            return 0;
        });
    }

    private static int RunScan(CliArgs a, bool generate, bool stale = false, Func<ScanSummary, int>? after = null,
        Func<IReadOnlyDictionary<AnvilLOD.Core.World.QuadKey, IReadOnlyList<AnvilLOD.Core.World.LodReference>>, IReadOnlyDictionary<AnvilLOD.Core.World.QuadKey, string>, IReadOnlySet<string>>? refresh = null)
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
                Tree3D: Tree3DFromArgs(a),
                MatchTextures: !a.Has("no-match-textures"),
                ParentCopies: a.Has("parent-copies"),
                GlowWindows: a.Has("glow-windows"),
                RenderTreeBillboards: a.Has("render-trees")),
            OutputFolder: outputFolder,
            CleanOutput: !a.Has("keep-output") && refresh is null,   // a refresh folder keeps its earlier refreshed blocks
            RefreshSelector: refresh,
            ContentHashCache: stale && a.Get("baseline") is { } hashBase ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hashBase)) ?? ".", "content-hashes.json") : null,   // stale commands compare file CONTENT, not file times
            Generate: generate,
            DynDolodFolder: a.Get("dyndolod"),
            Preset: ParsePreset(a.Get("preset")),
            Candles: a.Has("candles"),
            FxGlow: a.Has("fxglow"),
            CustomRulesFile: a.Get("rules"),
            RemoveBuried: !a.Has("keep-buried"),
            RemoveUnderwater: !a.Has("keep-underwater"),
            GrassLod: !stale && !a.Has("no-grass"),
            ChildWorlds: !a.Has("no-child-worlds"),
            Seasons: a.Has("seasons"),
            Underside: a.Has("underside"),
            NativeGridObjects: a.Has("native-grid-objects"),
            NativeEffects: a.Has("native-effects"),
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
            EslRange: a.Get("esl-range") is { } er && int.TryParse(er, out var erv) && erv is 2048 or 4096 ? erv : 0,
            DetailedShips: !a.Has("no-detailed-ships"),
            AnimatedShips: !a.Has("no-animated-ships"),
            CsGrassLod: !stale && !a.Has("no-cs-grass"),
            IceInset: a.Get("ice-inset") is { } ii && float.TryParse(ii, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var iip) ? Math.Clamp(iip, 0f, 64f) : AnvilLOD.Meshes.LodInset.DefaultIce,
            MountainFit: !a.Has("no-mountain-fit"),
            MountainInset: a.Get("mountain-inset") is { } mi && float.TryParse(mi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mip) ? Math.Clamp(mip, 0f, 64f) : 12f,
            Occlusion: a.Has("occlusion") ? new AnvilLOD.Core.Occlusion.OcclusionSettings(Clearance: a.Get("occlusion-clearance") is { } oc && float.TryParse(oc, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ocv) ? Math.Max(0f, ocv) : 3072f, ParentClearance: a.Get("occlusion-clearance") is { } oc2 && float.TryParse(oc2, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ocv2) ? Math.Max(0f, ocv2) : 3072f) : null,
            OcclusionFolder: a.Get("occlusion-folder"),
            MapObjectLevel: a.Get("map-objects")?.ToLowerInvariant() switch { null or "auto" => -2, "rules" => -1, "0" => 0, "1" => 1, "2" => 2, var bad => throw new CliArgException($"--map-objects must be auto, rules, 0, 1 or 2 (got '{bad}').") },
            SnowCover: a.Get("snow-cover") switch { "off" => 0, "bright" => 2, _ => 1 },
            SnowShaderMode: a.Get("snow-shader") switch { "plain" => 1, "hd" => 2, _ => 0 },
            ObjectInset: a.Get("object-inset") is { } oi && float.TryParse(oi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var oip) ? Math.Clamp(oip, 0f, 64f) : 0f,
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

        if (after is not null) return after(s);
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
            TreeStandIns: a.Has("tree-stand-ins"),
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

    private static int RunFixTextures(CliArgs a)
    {
        var folders = new List<string>(a.GetAll("input") ?? []);
        string? mo2Root = null;
        if (a.Get("mo2") is { } mo2)
        {
            var instance = AnvilLOD.Plugins.Mo2.Mo2Instance.Open(mo2, Mo2LocationsFromArgs(a));
            folders.AddRange(instance.OpenProfile(a.Get("profile")).EnabledModFolders());
            mo2Root = instance.InstanceFolder;
        }
        if (folders.Count == 0) throw new CliArgException("fix-textures needs --input <folder> (repeatable) or --mo2 <instance>.");
        var total = Stopwatch.StartNew();
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var bad = AnvilLOD.Core.Textures.TextureFixer.Scan(folders, sync);
        foreach (var b in bad.OrderBy(b => b.Mod, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Relative, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {b.Mod}\\{b.Relative}  {b.Format}  {b.Width}x{b.Height}, {b.Mips} mips  {(b.Fixable ? "can be converted" : "cannot be converted here")}");
        if (!a.Has("apply") || bad.Count == 0)
        {
            if (bad.Count > 0) Console.WriteLine("Nothing was changed. Add --apply (with --output <folder> or --in-place --backup <folder>) to convert them.");
            return 0;
        }
        bool inPlace = a.Has("in-place");
        var output = a.Get("output");
        var backup = a.Get("backup");
        if (inPlace == (output is not null)) throw new CliArgException("--apply needs either --output <folder> or --in-place with --backup <folder>.");
        if (inPlace && backup is null) throw new CliArgException("--in-place needs --backup <folder> (the originals are copied there first).");
        var texconv = AnvilLOD.Core.Textures.TextureFixer.FindTexconv([a.Get("texconv"), .. new[] { mo2Root }.Where(x => x is not null).SelectMany(r => new[] { Path.Combine(r!, "tools") }),
            Environment.GetEnvironmentVariable("TEXCONV")])
            ?? throw new CliArgException("texconv.exe was not found: pass --texconv <exe or folder> (xEdit, xLODGen and DynDOLOD have it in their \"Edit Scripts\" folder).");
        Console.WriteLine($"Using {texconv}");
        var results = AnvilLOD.Core.Textures.TextureFixer.Fix(bad, texconv, inPlace ? null : output, inPlace ? backup : null, dryRun: false, sync);
        foreach (var r in results.Where(r => !r.Status.StartsWith("Converted", StringComparison.Ordinal))) Console.WriteLine($"  {r.Source.Mod}\\{r.Source.Relative}: {r.Status}");
        return results.Any(r => r.Status.StartsWith("Failed", StringComparison.Ordinal)) ? 1 : 0;
    }

    private static int RunConvertLod(CliArgs a)
    {
        var source = a.Get("source") ?? throw new CliArgException("convert-lod needs --source <mod folder>.");
        var output = a.Get("output") ?? throw new CliArgException("convert-lod needs --output <folder> (a new, separate mod folder).");
        var total = Stopwatch.StartNew();
        Console.WriteLine("WARNING: " + AnvilLOD.Plugins.Authoring.OldrimLodConverter.Warning);
        var sync = new SyncProgress(m => Console.WriteLine($"[{total.Elapsed:mm\\:ss\\.f}] {m}"));
        var r = AnvilLOD.Plugins.Authoring.OldrimLodConverter.Run(new AnvilLOD.Plugins.Authoring.ConvertRequest(source, output, a.Has("all-meshes"), a.Has("overwrite")), sync);
        Console.WriteLine($"Report: {r.ReportFile}");
        return 0;
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
            RuleFile: !a.Has("no-rules"), Overwrite: a.Has("overwrite"), Budget: budget, CustomBudget: custom, Plugin: a.Get("plugin"));
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

    /// <summary>Read-only: DynDOLOD's dynamic LOD references against the load order as it is now (moved, deleted or missing sources).</summary>
    private static int RunDynCheck(CliArgs a)
    {
        var dynOut = a.Get("dyndolod-output") ?? throw new CliArgException("dyncheck needs --dyndolod-output <folder> (the folder that holds skse\\plugins\\DynDOLOD_Data).");
        var gameOptions = a.Get("mo2") is { } mo2Folder
            ? new GameContextOptions(Mode: GameSourceMode.Mo2Instance, Mo2InstanceFolder: mo2Folder, Mo2Profile: a.Get("profile"), Mo2Locations: Mo2LocationsFromArgs(a))
            : new GameContextOptions(a.Get("data"), a.Get("plugins"));
        var progress = new SyncProgress(Console.WriteLine);
        using var game = GameContext.Open(gameOptions, progress);
        var r = AnvilLOD.Plugins.DynamicRefCheck.Run(game, dynOut, progress);

        Console.WriteLine();
        Console.WriteLine("=== DynDOLOD dynamic references vs the load order now ===");
        foreach (var w in r.Warnings) Console.WriteLine("WARNING: " + w);
        Console.WriteLine($"Files: {r.Files:N0}; rows: {r.Rows:N0}; unreadable rows skipped: {r.Unparsed:N0}");
        var by = r.Findings.GroupBy(f => f.Issue).ToDictionary(g => g.Key, g => g.Count());
        foreach (var issue in Enum.GetValues<AnvilLOD.Plugins.DynRefIssue>())
            if (by.TryGetValue(issue, out var n)) Console.WriteLine($"  {issue,-14} {n,7:N0}");
        int toggled = r.Findings.Count(f => f.Toggled);
        Console.WriteLine($"Sources that start disabled or are enable-parented (they switch at run time): {toggled:N0}; of those with a problem: {r.Problems.Count(f => f.Toggled):N0}");
        Console.WriteLine("  (DiffersNoOverride is not counted as a problem: no plugin overrides the source now, so the load order did not change it.)");
        var problems = r.Problems.ToList();
        if (problems.Count > 0)
        {
            Console.WriteLine("By worldspace:");
            foreach (var g in problems.GroupBy(f => f.Row.World).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"  {g.Key,-28} {g.Count(),6:N0}");
            Console.WriteLine("Changed by (the plugin that wins the source reference now):");
            foreach (var g in problems.Where(f => f.WinningPlugin is not null).GroupBy(f => f.WinningPlugin!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(15))
                Console.WriteLine($"  {g.Key,-48} {g.Count(),6:N0}");
            Console.WriteLine("First problems:");
            foreach (var f in problems.Take(12))
                Console.WriteLine($"  {f.Row.World} {f.Row.DynId:X6} <- {f.Row.SourceId:X6}:{f.Row.SourcePlugin}  {f.Issue}: {f.Detail}");
        }
        if (a.Get("csv") is { } csv) { File.WriteAllText(csv, r.ToCsv()); Console.WriteLine($"List: {csv}"); }
        if (a.Get("plan") is { } planFile)
        {
            var plan = r.Plan();
            File.WriteAllText(planFile, r.ToPlanCsv());
            Console.WriteLine();
            Console.WriteLine($"=== Plan for a copy of the DynDOLOD files (DRY RUN: nothing was written) ===");
            foreach (var g in plan.GroupBy(i => i.Action).OrderBy(g => g.Key))
                Console.WriteLine($"  {g.Key,-16} {g.Count(),5:N0}");
            Console.WriteLine($"Plan: {planFile}");
        }
        if (a.Get("apply-plan") is { } approvedPlan)
        {
            var fixOut = a.Get("fix-output") ?? throw new CliArgException("--apply-plan needs --fix-output <new folder>.");
            try
            {
                var res = AnvilLOD.Plugins.DynFixWriter.Apply(r, approvedPlan, dynOut, fixOut, progress);
                Console.WriteLine();
                Console.WriteLine("=== Write pass: edited COPIES written (originals untouched) ===");
                Console.WriteLine($"Folder: {res.Folder}");
                Console.WriteLine($"DynDOLOD.esp: {res.Repositioned} repositioned, {res.Sunk} sunk to z -30000");
                Console.WriteLine($"Data files edited: {res.DataRowsEdited} rows in {string.Join(", ", res.DataFiles)}");
                foreach (var v in res.Verification) Console.WriteLine("  verify: " + v);
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
        }
        if (a.Get("patch-output") is { } patchOut)
        {
            try
            {
                var res = AnvilLOD.Plugins.DynPatchWriter.Apply(game, r, patchOut, progress);
                Console.WriteLine();
                Console.WriteLine("=== Patch plugin for the moved/removed dynamic references (DynDOLOD.esp untouched) ===");
                Console.WriteLine($"Plugin: {res.Plugin ?? "(none)"}; {res.Repositioned} repositioned, {res.Sunk} sunk to z -30000; enable-state findings not written: {res.EnableStateSkipped}");
                foreach (var v in res.Verification) Console.WriteLine("  verify: " + v);
            }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
        }
        if (a.Get("csv-all") is { } csvAll) { File.WriteAllText(csvAll, r.ToCsv(all: true)); Console.WriteLine($"Every row, with the data columns and enable states: {csvAll}"); }

        var lr = AnvilLOD.Plugins.LargeRefCheck.Run(game, dynOut, progress);
        Console.WriteLine();
        Console.WriteLine("=== DynDOLOD large reference lists vs the load order now ===");
        foreach (var w in lr.Warnings) Console.WriteLine("WARNING: " + w);
        if (lr.Plugin is not null)
        {
            Console.WriteLine($"Entries: {lr.Findings.Count:N0}");
            foreach (var g in lr.Findings.GroupBy(f => f.Issue).OrderBy(g => g.Key))
                Console.WriteLine($"  {g.Key,-14} {g.Count(),7:N0}");
            var lp = lr.Problems.ToList();
            foreach (var g in lp.GroupBy(f => f.Worldspace).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"  {g.Key,-28} {g.Count(),6:N0} problems");
            foreach (var g in lp.Where(f => f.WinningPlugin is not null).GroupBy(f => f.WinningPlugin!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"  changed by {g.Key,-44} {g.Count(),6:N0}");
            foreach (var f in lp.Take(10))
                Console.WriteLine($"  {f.Worldspace} cell ({f.CellX},{f.CellY}) {f.Reference.ID:X6}:{f.Reference.ModKey.FileName}  {f.Issue}: {f.Detail}");
            if (a.Get("csv-large") is { } csvLarge) { File.WriteAllText(csvLarge, lr.ToCsv(all: a.Has("csv-large-all"))); Console.WriteLine($"List: {csvLarge}"); }
        }
        return 0;
    }

    private static AnvilLOD.Core.Incremental.BaselineComponents Comp(ScanSummary s) =>
        new(s.QuadAssets, s.WorldTerrain, s.WorldWater, s.SettingsCore, s.ContentHashed);

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
