using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Dds;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins.Authoring;

public sealed record AuthorRequest(
    GameContextOptions Game,
    string Plugin,                      // "The Archwood.esp"
    string OutputFolder,                // a new, separate mod folder
    bool LodMeshes = true,
    bool Billboards = true,
    bool RuleFile = true,
    float MinObjectSize = 400f,         // objects smaller than this (largest dimension) don't need object LOD
    float MinTreeHeight = 256f,         // trees/plants lower than this don't need a billboard
    bool Overwrite = false,             // replace files already in the output folder (off: keep hand-edited ones)
    string? DynDolodFolder = null,
    LodPreset Preset = LodPreset.High,
    BudgetMode Budget = BudgetMode.Recommended,       // triangle budget for the generated LOD meshes (see LodBudgets)
    IReadOnlyList<int>? CustomBudget = null);          // with Custom: the most triangles at LOD 0, 1 and 2 (0 = no limit)

/// <summary>One row of the author report.</summary>
public sealed record AuthorItem(
    string Kind,          // "Object" or "Tree"
    string FormKey,
    string? EditorId,
    string Model,
    int Placed,           // references in worldspaces with LOD
    float Size,           // largest dimension (objects) or height (trees), game units
    string Status,
    string Action);

public sealed record AuthorResult(
    IReadOnlyList<AuthorItem> Items,
    int MeshesWritten,
    int BillboardsWritten,
    string? RuleFile,
    string ReportFile,
    TimeSpan Elapsed);

/// <summary>
/// Mod author tools: for one plugin, finds the objects and trees that would be seen from afar but have no LOD
/// (no LOD mesh, a broken MNAM path, missing LOD textures, no tree billboard), and optionally generates them:
/// simplified <c>name_lod_0/1/2.nif</c>, TexGen-style billboards and a DynDOLOD-format rule file.
/// Everything goes into its own folder, never into the normal LOD output. Generated files are a starting point
/// for the mod author to review, not a finished product.
/// </summary>
public static class ModAuthorTool
{
    public const string Warning =
        "These files are generated automatically from the mod's own models and textures. They are a starting point: " +
        "check them in NifSkope and in game before using them, and only share them with the mod author's permission.";

    public static AuthorResult Run(AuthorRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var log = new FileLogProgress(Path.Combine(req.OutputFolder, "AnvilLOD Author.log"), progress, $"AnvilLOD Mod Author tools: {req.Plugin}");
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

    private static AuthorResult RunLogged(AuthorRequest req, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report("Opening load order...");
        using var game = GameContext.Open(req.Game with { ExtraLooseRoots = ["textures"], ExcludeFolder = req.OutputFolder }, progress);
        var listing = game.LoadOrder.ListedOrder.FirstOrDefault(l => string.Equals(l.ModKey.FileName.String, req.Plugin.Trim(), StringComparison.OrdinalIgnoreCase));
        if (listing?.Mod is not { } mod)
            throw new InvalidOperationException($"{req.Plugin} is not in the load order (or could not be loaded).");
        var pluginName = listing.ModKey.FileName.String;

        progress?.Report("Indexing meshes, textures and LOD settings...");
        var assets = game.BuildAssets(p => p.StartsWith("meshes\\", StringComparison.Ordinal) || p.StartsWith("textures\\", StringComparison.Ordinal)
                                           || p.StartsWith("lodsettings\\", StringComparison.Ordinal) || p.StartsWith("dyndolod\\", StringComparison.Ordinal));
        var lodIndex = LodMeshIndex.Build(assets.EnumeratePaths("meshes\\").Where(p => p.Contains("_lod", StringComparison.Ordinal)));
        var rules = LoadRules(req, assets, game);
        var resolver = new LodMeshResolver(lodIndex, rules, assets.Exists);
        var linkCache = game.LinkCache;

        // 1) The plugin's base records (new or overridden), resolved to the winning version.
        var bases = new Dictionary<FormKey, IMajorRecordGetter>();
        void Add<T>(IEnumerable<T> records) where T : class, IMajorRecordGetter
        {
            foreach (var r in records)
                if (linkCache.TryResolve<T>(r.FormKey, out var win)) bases[r.FormKey] = win;
        }
        Add(mod.Statics); Add(mod.MoveableStatics); Add(mod.Activators); Add(mod.Furniture);
        Add(mod.Doors); Add(mod.Containers); Add(mod.Trees);
        progress?.Report($"{pluginName}: {bases.Count:N0} object and tree records");

        // 2) Where they're placed: worldspaces with LOD only.
        var lodWorlds = new ConcurrentDictionary<FormKey, bool>();
        var placed = new Dictionary<FormKey, int>();
        long visited = 0;
        foreach (var ctx in game.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(linkCache))
        {
            ct.ThrowIfCancellationRequested();
            if ((++visited & 0x3FFFF) == 0) progress?.Report($"Counting placements: {visited:N0} references...");
            var r = ctx.Record;
            if (r.IsDeleted || r.Base.FormKeyNullable is not { } bk || !bases.ContainsKey(bk)) continue;
            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var ws)) continue;
            if (!lodWorlds.GetOrAdd(ws.Record.FormKey, _ => HasLod(ws.Record, assets, linkCache))) continue;
            placed[bk] = placed.GetValueOrDefault(bk) + 1;
        }
        progress?.Report($"{placed.Count:N0} of them are placed in worldspaces with LOD");

        // 3) Check each one, generate what's missing.
        var items = new List<AuthorItem>();
        var ruleLines = new List<string>();
        int meshesWritten = 0, billboardsWritten = 0;
        var textureCache = new ConcurrentDictionary<string, (byte[], int, int)?>(StringComparer.OrdinalIgnoreCase);
        var pluginKey = LodRules.PluginKey(pluginName);

        foreach (var (fk, rec) in bases.OrderBy(kv => (rec: kv.Value is ITreeGetter ? 1 : 0, kv.Value.EditorID ?? "")))
        {
            ct.ThrowIfCancellationRequested();
            int count = placed.GetValueOrDefault(fk);
            if (count == 0) continue;
            var modelPath = (rec as IModeledGetter)?.Model?.File is { IsNull: false } f ? f.DataRelativePath.Path : null;
            if (modelPath is null)
            {
                items.Add(new AuthorItem(rec is ITreeGetter ? "Tree" : "Object", fk.ToString(), rec.EditorID, "", count, 0, "No model", ""));
                continue;
            }
            var full = GamePath.Normalize(modelPath.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? modelPath : "meshes\\" + modelPath);
            var (mesh, problem) = ReadMeshChecked(assets, full);
            if (mesh is null)
            {
                items.Add(new AuthorItem(rec is ITreeGetter ? "Tree" : "Object", fk.ToString(), rec.EditorID, full, count, 0, problem!, ""));
                continue;
            }

            if (rec is ITreeGetter)
                items.Add(CheckTree(fk, rec, full, mesh, count));
            else
                items.Add(CheckObject(fk, rec, full, mesh, count));
        }

        // 4) Rule file + report.
        string? ruleFile = null;
        if (req.RuleFile && ruleLines.Count > 0)
        {
            ruleFile = Path.Combine(req.OutputFolder, "DynDOLOD", $"DynDOLOD_SSE_{pluginKey}.ini");
            if (req.Overwrite || !File.Exists(ruleFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ruleFile)!);
                var sb = new StringBuilder();
                sb.AppendLine($"; LOD rules for {pluginName}, generated by AnvilLOD Mod Author tools on {DateTime.Now:yyyy-MM-dd}.");
                sb.AppendLine("; " + Warning);
                sb.AppendLine("; Columns: mesh mask, LOD4, LOD8, LOD16, LOD32, grid, reference, flags, description (DynDOLOD format).");
                sb.AppendLine("[Skyrim LODGen]");
                for (int i = 0; i < ruleLines.Count; i++) sb.AppendLine($"LODGen{i + 1}={ruleLines[i]}");
                File.WriteAllText(ruleFile, sb.ToString());
            }
        }

        var reportFile = Path.Combine(req.OutputFolder, $"AnvilLOD Author Report - {Path.GetFileNameWithoutExtension(pluginName)}.txt");
        Directory.CreateDirectory(req.OutputFolder);
        File.WriteAllText(reportFile, Report(pluginName, req, items, meshesWritten, billboardsWritten, ruleFile));
        sw.Stop();
        progress?.Report($"Done: {meshesWritten} LOD meshes, {billboardsWritten} billboards{(ruleFile is null ? "" : ", rule file")} in {sw.Elapsed.TotalSeconds:F0}s");
        return new AuthorResult(items, meshesWritten, billboardsWritten, ruleFile, reportFile, sw.Elapsed);

        // ---------- objects ----------
        AuthorItem CheckObject(FormKey fk, IMajorRecordGetter rec, string full, LodMesh? mesh, int count)
        {
            if (mesh is null) return new AuthorItem("Object", fk.ToString(), rec.EditorID, full, count, 0, "Model missing or unreadable", "");
            var (min, max) = LodMeshAuthor.Bounds(mesh);
            var ext = max - min;
            float size = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));

            string?[]? mnam = null;
            var problems = new List<string>();
            if (rec is IStaticGetter stat && stat.Lod is { } lod)
            {
                mnam = [P(lod.Level0), P(lod.Level1), P(lod.Level2), P(lod.Level3)];
                foreach (var m in mnam)
                    if (m is not null && !assets.Exists(m)) problems.Add($"MNAM mesh missing: {m}");
            }
            var res = resolver.Resolve(LodRules.FormIdKey(fk.ModKey.FileName.String, fk.ID), LodRules.FormIdKey(fk.ModKey.FileName.String, fk.ID), full, mnam);
            var used = new[] { res.Meshes.Lod4, res.Meshes.Lod8, res.Meshes.Lod16 }.Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var lodPath in used)
            {
                var lodMesh = ReadMesh(assets, lodPath!);
                if (lodMesh is null) { problems.Add($"LOD mesh unreadable: {lodPath}"); continue; }
                foreach (var tex in lodMesh.Parts.SelectMany(p => p.Material.Textures.Take(2)).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!assets.Exists(TexturePath(tex))) problems.Add($"LOD texture missing: {TexturePath(tex)}");
            }

            bool hasLod = used.Count > 0;
            bool rulesSayNone = !hasLod && res.Rule.SourceFile != LodRules.Default.SourceFile
                                && res.Rule.Lod4.Kind == LodChoiceKind.None && res.Rule.Lod8.Kind == LodChoiceKind.None;
            string status = hasLod
                ? (problems.Count == 0 ? $"Has LOD ({(res.UsedMnam ? "MNAM" : "by name")})" : "Has LOD, with problems")
                : rulesSayNone ? $"No LOD on purpose (rule in {res.Rule.SourceFile})"
                : size < req.MinObjectSize ? "Small, no LOD needed"
                : "Missing LOD";
            if (problems.Count > 0) status += ": " + string.Join("; ", problems.Distinct());

            string action = "";
            bool needs = !rulesSayNone && size >= req.MinObjectSize && (!hasLod || problems.Any(p => p.StartsWith("LOD mesh unreadable", StringComparison.Ordinal)));
            if (needs && req.LodMeshes)
                action = WriteLodMeshes(full, mesh, size);
            return new AuthorItem("Object", fk.ToString(), rec.EditorID, full, count, size, status, action);
        }

        string WriteLodMeshes(string full, LodMesh mesh, float size)
        {
            var stem = Path.GetFileNameWithoutExtension(full);
            var levels = LodMeshAuthor.Build(mesh, LodMeshAuthor.DefaultLevels
                .Select(l => l with { MaxTriangles = LodBudgets.For(req.Budget, req.CustomBudget, l.Level, size) }).ToList());
            if (levels.Count == 0) return "Nothing usable to simplify (only blended/effect shapes)";
            var written = new List<string>();
            int sourceTris = mesh.Parts.Sum(p => p.TriangleCount);
            foreach (var lv in levels)
            {
                var rel = GamePath.Join("meshes", "lod", pluginKey, $"{stem}_lod_{lv.Level}.nif");
                var path = Path.Combine(req.OutputFolder, rel);
                if (!req.Overwrite && File.Exists(path)) { written.Add($"L{lv.Level} kept"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, LodNifWriter.Write($"{stem}_lod_{lv.Level}", lv.Parts));
                meshesWritten++;
                written.Add($"L{lv.Level} {lv.Triangles:N0} tris" + (lv.OverBudget ? $" (over the budget of {lv.Budget:N0})" : ""));
            }
            var mask = full["meshes\\".Length..];
            ruleLines.Add($"{mask},Level0,Level1,Level2,Level2,FarLOD,Unchanged,0,AnvilLOD author");
            return $"Generated name_lod_0-{levels.Count - 1} from {sourceTris:N0} tris: {string.Join(", ", written)}";
        }

        // ---------- trees ----------
        AuthorItem CheckTree(FormKey fk, IMajorRecordGetter rec, string full, LodMesh? mesh, int count)
        {
            if (mesh is null) return new AuthorItem("Tree", fk.ToString(), rec.EditorID, full, count, 0, "Model missing or unreadable", "");
            var (min, max) = LodMeshAuthor.Bounds(mesh);
            float height = max.Z - min.Z;
            var folder = GamePath.Join("textures", "terrain", "lodgen", fk.ModKey.FileName.String.ToLowerInvariant());
            var stem = Path.GetFileNameWithoutExtension(full).ToLowerInvariant();
            var name = $"{stem}_{fk.ID:x8}";
            bool has = (assets.Exists(GamePath.Join(folder, name + ".dds")) || assets.Exists(GamePath.Join(folder, name + "_1.dds")))
                       && assets.Exists(GamePath.Join(folder, name + ".txt"));
            if (has) return new AuthorItem("Tree", fk.ToString(), rec.EditorID, full, count, height, "Has billboard", "");
            if (height < req.MinTreeHeight)
                return new AuthorItem("Tree", fk.ToString(), rec.EditorID, full, count, height, "Small, no billboard needed", "");
            string action = "";
            if (req.Billboards) action = WriteBillboard(folder, name, full, mesh);
            return new AuthorItem("Tree", fk.ToString(), rec.EditorID, full, count, height, "Missing billboard", action);
        }

        string WriteBillboard(string folder, string name, string full, LodMesh mesh)
        {
            var dir = Path.Combine(req.OutputFolder, folder);
            var main = Path.Combine(dir, name + ".dds");
            if (!req.Overwrite && File.Exists(main)) return "Kept existing billboard";
            var img = BillboardRenderer.Render(mesh, LoadTexture);
            if (img is null) return "Could not render (no visible pixels)";
            Directory.CreateDirectory(dir);
            foreach (var file in new[] { main, Path.Combine(dir, name + "_1.dds") })
                using (var fs = File.Create(file)) BillboardRenderer.WriteDds(fs, img.Rgba, img.Width, img.Height);
            using (var fs = File.Create(Path.Combine(dir, name + "_1_n.dds"))) BillboardRenderer.WriteFlatNormalDds(fs, img.Width, img.Height);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllText(Path.Combine(dir, name + ".txt"), string.Join("\r\n",
                "[LOD]",
                string.Create(inv, $"Width={img.WorldWidth:F6}"),
                string.Create(inv, $"Height={img.WorldHeight:F6}"),
                "ShiftX=0.000000", "ShiftY=0.000000",
                string.Create(inv, $"ShiftZ={img.ShiftZ:F6}"),
                "Scale=1.000000",
                string.Create(inv, $"Width_1={img.WorldWidth:F6}"),
                string.Create(inv, $"Height_1={img.WorldHeight:F6}"),
                $"Model={full}",
                "Generator=AnvilLOD Mod Author tools", ""));
            billboardsWritten++;
            return $"Rendered {img.Width}x{img.Height} billboard ({img.Coverage:P0} coverage)";
        }

        (byte[], int, int)? LoadTexture(string texture) => textureCache.GetOrAdd(TexturePath(texture), p =>
        {
            if (!assets.TryOpen(p, out var st)) return null;
            byte[] bytes;
            using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }
            try
            {
                var (rgba, w, h, _) = DdsFile.DecodeTopMip(bytes);
                while (w > 1024 && h > 4) (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h);
                return (rgba, w, h);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException) { return null; }
        });

        string? P(Mutagen.Bethesda.Plugins.Assets.AssetLinkGetter<Mutagen.Bethesda.Skyrim.Assets.SkyrimModelAssetType> link) =>
            link.IsNull ? null : GamePath.Normalize("meshes\\" + link.DataRelativePath.Path.TrimStart('\\'))
                .Replace("meshes\\meshes\\", "meshes\\", StringComparison.Ordinal);
    }

    private static string TexturePath(string t)
    {
        var p = GamePath.Normalize(t);
        return p.StartsWith("textures\\", StringComparison.Ordinal) ? p : "textures\\" + p;
    }

    private static LodMesh? ReadMesh(AssetIndex assets, string path) => ReadMeshChecked(assets, path).Mesh;

    private static (LodMesh? Mesh, string? Problem) ReadMeshChecked(AssetIndex assets, string path)
    {
        if (!assets.TryOpen(path, out var st)) return (null, "Model file missing");
        byte[] bytes;
        using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }
        try
        {
            var m = NifGeometryReader.Read(path, bytes);
            if (m.Parts.Count > 0) return (m, null);
            var why = m.Warnings.Count > 0 ? string.Join("; ", m.Warnings.Distinct().Take(3)) : $"no drawable shapes ({bytes.Length:N0} bytes, an empty or placeholder file?)";
            return (null, "Model unusable: " + why);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException or EndOfStreamException)
        {
            return (null, "Model unreadable: " + ex.Message);
        }
    }

    private static bool HasLod(IWorldspaceGetter ws, AssetIndex assets, Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache)
    {
        if (ws.EditorID is { } e && assets.Exists(LodSettings.RelativePathFor(e))) return true;
        // Child worldspaces that use their parent's LOD (walled cities) show up there too.
        if (ws.Parent is { } parent && parent.Flags.HasFlag(WorldspaceParent.Flag.UseLodData)
            && linkCache.TryResolve<IWorldspaceGetter>(parent.Worldspace.FormKey, out var pw) && pw.EditorID is { } pe)
            return assets.Exists(LodSettings.RelativePathFor(pe));
        return false;
    }

    private static LodRules LoadRules(AuthorRequest req, AssetIndex assets, GameContext game)
    {
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
        foreach (var (name, full) in game.RootFiles())
            if (name.StartsWith("dyndolod_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !dataRuleFiles.ContainsKey(name))
            {
                var f = full;
                dataRuleFiles[name] = () => File.ReadAllText(f);
            }
        string? rulesFolder = null;
        if (!string.IsNullOrWhiteSpace(req.DynDolodFolder))
            foreach (var c in new[] { Path.Combine(req.DynDolodFolder, "Edit Scripts", "DynDOLOD", "Rules"), Path.Combine(req.DynDolodFolder, "Rules"), req.DynDolodFolder })
                if (Directory.Exists(c) && Directory.EnumerateFiles(c, "DynDOLOD_*.ini").Any()) { rulesFolder = c; break; }
        var plugins = game.LoadOrder.ListedOrder.Where(l => l.Mod is not null).Select(l => l.ModKey.ToString()).ToList();
        return LodRules.Load(rulesFolder, dataRuleFiles, plugins, req.Preset);
    }

    private static string Report(string plugin, AuthorRequest req, IReadOnlyList<AuthorItem> items, int meshes, int billboards, string? ruleFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AnvilLOD Mod Author report: {plugin}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("WARNING: " + Warning);
        sb.AppendLine();
        sb.AppendLine($"Objects and trees placed in worldspaces with LOD: {items.Count}");
        foreach (var g in items.GroupBy(i => (i.Kind, Status: i.Status.Split(':')[0])).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Status))
            sb.AppendLine($"  {g.Key.Kind,-7} {g.Key.Status,-45} {g.Count(),5}");
        sb.AppendLine();
        sb.AppendLine($"Generated: {meshes} LOD meshes, {billboards} tree billboards{(ruleFile is null ? "" : $", rule file {Path.GetFileName(ruleFile)}")}");
        sb.AppendLine($"Thresholds: objects >= {req.MinObjectSize:F0} units need LOD, trees >= {req.MinTreeHeight:F0} units need a billboard");
        sb.AppendLine(req.Budget switch
        {
            BudgetMode.Recommended => $"LOD mesh triangle budget: recommended for the model size (LOD 0 / 1 / 2): {LodBudgets.Describe()}. Basis: {LodBudgets.Basis}.",
            BudgetMode.Custom => $"LOD mesh triangle budget: custom, LOD 0 / 1 / 2 = {string.Join(" / ", Enumerable.Range(0, 3).Select(i => LodBudgets.For(BudgetMode.Custom, req.CustomBudget, i, 0f) is { } n ? n.ToString("N0") : "no limit"))}",
            _ => "LOD mesh triangle budget: off",
        });
        sb.AppendLine();
        foreach (var kind in new[] { "Object", "Tree" })
        {
            var rows = items.Where(i => i.Kind == kind).OrderBy(i => i.Status.StartsWith("Has", StringComparison.Ordinal) || i.Status.StartsWith("Small", StringComparison.Ordinal)).ThenByDescending(i => i.Size).ToList();
            if (rows.Count == 0) continue;
            sb.AppendLine(kind == "Object" ? "=== OBJECTS ===" : "=== TREES / PLANTS ===");
            foreach (var i in rows)
            {
                sb.AppendLine($"{i.EditorId ?? "(no EDID)"} [{i.FormKey}]  placed {i.Placed}x, size {i.Size:F0}");
                sb.AppendLine($"    model:  {i.Model}");
                sb.AppendLine($"    status: {i.Status}");
                if (i.Action.Length > 0) sb.AppendLine($"    action: {i.Action}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
