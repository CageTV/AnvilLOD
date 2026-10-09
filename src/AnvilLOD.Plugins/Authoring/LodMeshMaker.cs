using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Plugins.Authoring;

/// <param name="Inputs">Model files (.nif) or folders: a mod folder, its <c>meshes</c> folder, or any folder of models.</param>
/// <param name="OutputFolder">Where the LOD meshes go: a new mod folder or an existing one. Never the LOD output.</param>
/// <param name="Group">Name of the subfolder under <c>meshes\lod</c> and of the rule file.</param>
/// <param name="Levels">Which of LOD 0, 1, 2 (= LOD4, LOD8, LOD16) to make; null = all three.</param>
/// <param name="Detail">Scales how much is removed: below 1 keeps more detail, above 1 less.</param>
/// <param name="MinModelSize">Models whose largest dimension is smaller than this (game units) are skipped.</param>
/// <param name="Budget">Off, the recommended triangle budget for the model's size (default), or the custom numbers.</param>
/// <param name="CustomBudget">With <see cref="BudgetMode.Custom"/>: the most triangles at LOD 0, 1 and 2 (0 = no limit).</param>
public sealed record LodMakerRequest(
    IReadOnlyList<string> Inputs,
    string OutputFolder,
    string Group,
    IReadOnlyList<int>? Levels = null,
    float Detail = 1f,
    float MinModelSize = 0f,
    bool RuleFile = true,
    bool Overwrite = false,
    BudgetMode Budget = BudgetMode.Recommended,
    IReadOnlyList<int>? CustomBudget = null);

public sealed record LodMakerItem(string Model, string Status, float Size, int SourceTriangles, string Action);

public sealed record LodMakerResult(IReadOnlyList<LodMakerItem> Items, int ModelsDone, int MeshesWritten, string? RuleFile, string ReportFile, TimeSpan Elapsed);

/// <summary>
/// Makes object LOD meshes (<c>name_lod_0/1/2.nif</c>) from full models you point it at: small parts are dropped and the rest is
/// simplified, the model's own textures are kept, and collision is never carried over (the LOD files are written from the
/// triangles alone). It needs no load order: it only reads the model files. A DynDOLOD-format rule file makes AnvilLOD and
/// DynDOLOD use the new meshes for the models they were made from.
/// </summary>
public static class LodMeshMaker
{
    public static readonly string Warning = ModAuthorTool.Warning;

    private static readonly string[] RuleLevelNames = ["Level0", "Level1", "Level2"];

    public static LodMakerResult Run(LodMakerRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.OutputFolder)) throw new ArgumentException("Choose an output folder.");
        using var log = new FileLogProgress(Path.Combine(req.OutputFolder, "AnvilLOD LOD Maker.log"), progress, "AnvilLOD LOD Mesh Maker");
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

    /// <summary>One model found on disk: the file and the path the game would know it by.</summary>
    public sealed record Source(string File, string Relative);

    /// <summary>Finds the models under the given files and folders (without duplicates), skipping existing LOD meshes.</summary>
    public static List<Source> Collect(IEnumerable<string> inputs, string? outputFolder, IList<string>? notes = null)
    {
        var found = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var outFull = outputFolder is null ? null : Path.GetFullPath(outputFolder).TrimEnd('\\') + "\\";
        foreach (var raw in inputs.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            var input = Path.GetFullPath(raw.Trim().Trim('"'));
            if (File.Exists(input))
            {
                Add(input, RelativeOf(input));
            }
            else if (Directory.Exists(input))
            {
                var meshesRoot = Directory.Exists(Path.Combine(input, "meshes")) ? Path.Combine(input, "meshes")
                    : Path.GetFileName(input).Equals("meshes", StringComparison.OrdinalIgnoreCase) ? input
                    : null;
                var root = meshesRoot ?? input;
                foreach (var file in Directory.EnumerateFiles(root, "*.nif", SearchOption.AllDirectories))
                    Add(file, GamePath.Join("meshes", Path.GetRelativePath(root, file)));
            }
            else notes?.Add($"Not found: {raw}");
        }
        return [.. found.Values.OrderBy(s => s.Relative, StringComparer.OrdinalIgnoreCase)];

        void Add(string file, string relative)
        {
            if (!file.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) return;
            if (outFull is not null && Path.GetFullPath(file).StartsWith(outFull, StringComparison.OrdinalIgnoreCase)) return; // our own output
            found.TryAdd(GamePath.Normalize(relative), new Source(file, GamePath.Normalize(relative)));
        }
    }

    /// <summary>For a loose file: the part from the last "meshes" folder on, or "meshes\name.nif".</summary>
    public static string RelativeOf(string file)
    {
        var parts = file.Split('\\', '/');
        for (int i = parts.Length - 2; i >= 0; i--)
            if (parts[i].Equals("meshes", StringComparison.OrdinalIgnoreCase))
                return string.Join('\\', parts[i..]);
        return GamePath.Join("meshes", Path.GetFileName(file));
    }

    public static List<AuthorLevel> LevelsFor(IReadOnlyList<int>? wanted, float detail)
    {
        var scale = float.IsFinite(detail) && detail > 0 ? Math.Clamp(detail, 0.1f, 10f) : 1f;
        return LodMeshAuthor.DefaultLevels
            .Where(l => wanted is null || wanted.Contains(l.Level))
            .Select(l => l with { MaxError = l.MaxError * scale, MinPartSize = l.MinPartSize * scale })
            .ToList();
    }

    /// <summary>The rule line for a model: LOD4, LOD8, LOD16 and LOD32 take the nearest level that was made.</summary>
    public static string RuleLine(string meshRelative, IReadOnlyCollection<int> made)
    {
        int Pick(int column)
        {
            var below = made.Where(l => l <= column).DefaultIfEmpty(-1).Max();
            return below >= 0 ? below : made.Min();
        }
        var mask = meshRelative.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? meshRelative["meshes\\".Length..] : meshRelative;
        return $"{mask},{RuleLevelNames[Pick(0)]},{RuleLevelNames[Pick(1)]},{RuleLevelNames[Pick(2)]},{RuleLevelNames[Pick(2)]},FarLOD,Unchanged,0,AnvilLOD LOD Maker";
    }

    /// <summary>The triangle budget for one level of a model of this size, or null for none.</summary>
    public static int? BudgetFor(LodMakerRequest req, int level, float size) => LodBudgets.For(req.Budget, req.CustomBudget, level, size);

    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var t = new string(s.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return t.Length == 0 ? "AnvilLOD" : t;
    }

    private static LodMakerResult RunLogged(LodMakerRequest req, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var group = Safe(req.Group);
        var levels = LevelsFor(req.Levels, req.Detail);
        if (levels.Count == 0) throw new ArgumentException("Choose at least one LOD level (0, 1 or 2).");

        var notes = new List<string>();
        var sources = Collect(req.Inputs, req.OutputFolder, notes);
        foreach (var n in notes) progress?.Report(n);
        progress?.Report($"{sources.Count:N0} models found");

        var items = new LodMakerItem?[sources.Count];
        var rules = new ConcurrentDictionary<int, string>();
        // Names decide which LOD file belongs to which model, so of several models with the same file name only the first
        // (in path order) is done. Decided up front so the result doesn't depend on thread timing.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sources)
            owner.TryAdd(Path.GetFileNameWithoutExtension(s.File), s.Relative);
        int meshesWritten = 0, done = 0, processed = 0;

        Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, index =>
        {
            var src = sources[index];
            var stem = Path.GetFileNameWithoutExtension(src.File);
            items[index] = Make(src, stem, index);
            var n = Interlocked.Increment(ref processed);
            if (n % 50 == 0 || n == sources.Count) progress?.Report($"{n:N0} of {sources.Count:N0} models...");
        });

        LodMakerItem Make(Source src, string stem, int index)
        {
            if (stem.Contains("_lod", StringComparison.OrdinalIgnoreCase) || src.Relative.StartsWith("meshes\\lod\\", StringComparison.OrdinalIgnoreCase))
                return new LodMakerItem(src.Relative, "Skipped: already a LOD mesh", 0, 0, "");
            if (!owner[stem].Equals(src.Relative, StringComparison.OrdinalIgnoreCase))
                return new LodMakerItem(src.Relative, $"Skipped: same file name as {owner[stem]}", 0, 0, "");

            LodMesh mesh;
            try
            {
                mesh = NifGeometryReader.Read(src.Relative, File.ReadAllBytes(src.File));
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException or EndOfStreamException or IOException)
            {
                return new LodMakerItem(src.Relative, "Unreadable: " + ex.Message, 0, 0, "");
            }
            if (mesh.Parts.Count == 0)
                return new LodMakerItem(src.Relative, "Nothing drawable in this model", 0, 0, "");

            var (min, max) = LodMeshAuthor.Bounds(mesh);
            var ext = max - min;
            float size = MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z));
            int sourceTris = mesh.Parts.Sum(p => p.TriangleCount);
            if (size < req.MinModelSize)
                return new LodMakerItem(src.Relative, "Skipped: smaller than the minimum size", size, sourceTris, "");

            var modelLevels = levels.Select(l => l with { MaxTriangles = BudgetFor(req, l.Level, size) }).ToList();
            var built = LodMeshAuthor.Build(mesh, modelLevels);
            if (built.Count == 0)
                return new LodMakerItem(src.Relative, "Nothing usable to simplify (only blended/effect shapes)", size, sourceTris, "");

            var action = new List<string>();
            foreach (var lv in built)
            {
                var path = Path.Combine(req.OutputFolder, GamePath.Join("meshes", "lod", group, $"{stem}_lod_{lv.Level}.nif"));
                if (!req.Overwrite && File.Exists(path)) { action.Add($"L{lv.Level} kept"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, LodNifWriter.Write($"{stem}_lod_{lv.Level}", lv.Parts, "AnvilLOD LOD Maker"));
                Interlocked.Increment(ref meshesWritten);
                action.Add($"L{lv.Level} {lv.Triangles:N0} tris" + (lv.OverBudget ? $" (over the budget of {lv.Budget:N0})" : ""));
            }
            Interlocked.Increment(ref done);
            rules[index] = RuleLine(src.Relative, built.Select(b => b.Level).ToList());
            return new LodMakerItem(src.Relative, "Done", size, sourceTris, string.Join(", ", action));
        }

        var list = items.Select(i => i!).ToList();
        string? ruleFile = null;
        if (req.RuleFile && !rules.IsEmpty)
        {
            ruleFile = Path.Combine(req.OutputFolder, "DynDOLOD", $"DynDOLOD_SSE_{group}.ini");
            WriteRuleFile(ruleFile, group, rules.OrderBy(r => r.Key).Select(r => r.Value).ToList(), req.Overwrite);
        }

        Directory.CreateDirectory(req.OutputFolder);
        var reportFile = Path.Combine(req.OutputFolder, $"AnvilLOD LOD Maker Report - {group}.txt");
        File.WriteAllText(reportFile, Report(group, req, list, meshesWritten, ruleFile));
        sw.Stop();
        progress?.Report($"Done: {done:N0} models, {meshesWritten:N0} LOD meshes{(ruleFile is null ? "" : ", rule file")} in {sw.Elapsed.TotalSeconds:F0}s");
        return new LodMakerResult(list, done, meshesWritten, ruleFile, reportFile, sw.Elapsed);
    }

    /// <summary>Writes the rule file. An existing one is kept and only gets lines for models it doesn't have yet (unless overwriting).</summary>
    private static void WriteRuleFile(string path, string group, IReadOnlyList<string> lines, bool overwrite)
    {
        var all = new List<string>();
        if (!overwrite && File.Exists(path))
            foreach (var l in File.ReadAllLines(path))
            {
                var eq = l.IndexOf('=');
                if (l.StartsWith("LODGen", StringComparison.OrdinalIgnoreCase) && eq > 0) all.Add(l[(eq + 1)..].Trim());
            }
        var have = new HashSet<string>(all.Select(l => l.Split(',')[0]), StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
            if (have.Add(l.Split(',')[0])) all.Add(l);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine($"; LOD rules for the meshes in meshes\\lod\\{group}, generated by AnvilLOD LOD Mesh Maker on {DateTime.Now:yyyy-MM-dd}.");
        sb.AppendLine("; " + Warning);
        sb.AppendLine("; Columns: mesh mask, LOD4, LOD8, LOD16, LOD32, grid, reference, flags, description (DynDOLOD format).");
        sb.AppendLine("[Skyrim LODGen]");
        for (int i = 0; i < all.Count; i++) sb.AppendLine($"LODGen{i + 1}={all[i]}");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Report(string group, LodMakerRequest req, IReadOnlyList<LodMakerItem> items, int meshes, string? ruleFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AnvilLOD LOD Mesh Maker report: {group}");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("WARNING: " + Warning);
        sb.AppendLine();
        sb.AppendLine($"Models: {items.Count}, LOD meshes written: {meshes}{(ruleFile is null ? "" : $", rule file {Path.GetFileName(ruleFile)}")}");
        sb.AppendLine($"Levels: {string.Join(", ", (req.Levels ?? [0, 1, 2]).Order().Select(l => "LOD " + l))}, detail x{req.Detail:0.##}");
        sb.AppendLine(req.Budget switch
        {
            BudgetMode.Recommended => $"Triangle budget: recommended for the model size (LOD 0 / 1 / 2): {LodBudgets.Describe()}. Basis: {LodBudgets.Basis}.",
            BudgetMode.Custom => $"Triangle budget: custom, LOD 0 / 1 / 2 = {string.Join(" / ", Enumerable.Range(0, 3).Select(i => req.CustomBudget is { } c && i < c.Count && c[i] > 0 ? c[i].ToString("N0") : "no limit"))}",
            _ => "Triangle budget: off",
        });
        sb.AppendLine();
        foreach (var g in items.GroupBy(i => i.Status.Split(':')[0]).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key,-48} {g.Count(),5}");
        sb.AppendLine();
        foreach (var i in items.OrderBy(i => i.Status == "Done" ? 1 : 0).ThenByDescending(i => i.Size))
        {
            sb.AppendLine(i.Model);
            sb.AppendLine($"    status: {i.Status}");
            if (i.SourceTriangles > 0) sb.AppendLine($"    size {i.Size:F0}, {i.SourceTriangles:N0} source triangles");
            if (i.Action.Length > 0) sb.AppendLine($"    action: {i.Action}");
        }
        return sb.ToString();
    }
}
