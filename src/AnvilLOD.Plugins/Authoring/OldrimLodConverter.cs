using System.Diagnostics;
using System.Text;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace AnvilLOD.Plugins.Authoring;

public sealed record ConvertRequest(
    string SourceFolder,        // a mod folder: loose meshes and/or its BSAs
    string OutputFolder,        // a new, separate mod folder
    bool AllMeshes = false,     // off: only LOD meshes (a lod folder, or a name with "lod" in it); on: every .nif
    bool Overwrite = false);    // replace files already in the output folder

public sealed record ConvertResult(int Found, int Converted, int AlreadySse, int Skipped, string ReportFile, TimeSpan Elapsed);

/// <summary>
/// Skyrim LE (Oldrim) static LOD meshes -> Skyrim SE: reads each LE mesh (NiTriShape + NiTriShapeData) and writes the same geometry, materials and
/// textures as an SE mesh (BSTriShape) under the same relative path in a new mod folder. Static geometry only: collision is dropped (a LOD mesh has
/// no use for it) and meshes with animation, skinning or particles are left alone (Cathedral Assets Optimizer's job). Textures are not touched.
/// </summary>
public static class OldrimLodConverter
{
    public const string Warning =
        "These meshes are converted automatically from the mod's own LE meshes. They are a starting point: check them in NifSkope and in game before using them, " +
        "and only share them with the mod author's permission.";

    // Blocks a static LOD mesh does not have. A mesh with one of these is not converted.
    private static readonly string[] NotStatic =
    [
        "NiControllerManager", "NiControllerSequence", "NiSkinInstance", "BSDismemberSkinInstance", "NiSkinData", "NiSkinPartition",
        "NiParticle", "NiPSys", "BSStripParticleSystem", "BSPSys", "NiMultiTargetTransformController", "BSBehaviorGraph",
    ];

    public static ConvertResult Run(ConvertRequest req, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        if (!Directory.Exists(req.SourceFolder)) throw new DirectoryNotFoundException($"Source folder not found: {req.SourceFolder}");
        Directory.CreateDirectory(req.OutputFolder);
        if (string.Equals(Path.GetFullPath(req.SourceFolder).TrimEnd('\\'), Path.GetFullPath(req.OutputFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output folder must be a different folder from the source.");

        // Index: loose meshes first, then the folder's archives (loose files win, as in the game).
        var sources = new Dictionary<string, Func<byte[]>>(StringComparer.OrdinalIgnoreCase);
        var meshesDir = Path.Combine(req.SourceFolder, "meshes");
        foreach (var bsa in Directory.EnumerateFiles(req.SourceFolder, "*.bsa", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var reader = OpenArchive(bsa, progress);
            if (reader is null) continue;
            foreach (var f in reader.Files)
            {
                var key = GamePath.Normalize(f.Path);
                if (key.StartsWith("meshes\\", StringComparison.Ordinal) && key.EndsWith(".nif", StringComparison.Ordinal))
                {
                    var file = f;
                    sources[key] = () => file.GetBytes();
                }
            }
        }
        if (Directory.Exists(meshesDir))
            foreach (var full in Directory.EnumerateFiles(meshesDir, "*.nif", SearchOption.AllDirectories))
            {
                var path = full;
                sources[GamePath.Normalize(Path.GetRelativePath(req.SourceFolder, full))] = () => File.ReadAllBytes(path);
            }
        progress?.Report($"{sources.Count:N0} meshes in {req.SourceFolder}");

        var report = new StringBuilder();
        report.AppendLine($"AnvilLOD Oldrim -> SSE LOD mesh converter, {DateTime.Now:yyyy-MM-dd HH:mm}");
        report.AppendLine("Source: " + req.SourceFolder);
        report.AppendLine("WARNING: " + Warning);
        report.AppendLine();

        int found = 0, converted = 0, alreadySse = 0, skipped = 0;
        foreach (var (key, read) in sources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!req.AllMeshes && !IsLodPath(key)) continue;
            found++;
            byte[] bytes;
            try { bytes = read(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { skipped++; report.AppendLine($"SKIPPED  {key}: unreadable ({ex.Message})"); continue; }

            NifFile nif;
            try { nif = NifFile.Read(bytes); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException or EndOfStreamException)
            { skipped++; report.AppendLine($"SKIPPED  {key}: {ex.Message}"); continue; }

            if (nif.BsVersion == NifFile.BsVersionSse) { alreadySse++; continue; }

            var bad = nif.Blocks.Select(b => b.Type).FirstOrDefault(t => NotStatic.Any(n => t.StartsWith(n, StringComparison.Ordinal)));
            if (bad is not null) { skipped++; report.AppendLine($"SKIPPED  {key}: not a static mesh ({bad})"); continue; }

            var target = Path.Combine(req.OutputFolder, key);
            if (!req.Overwrite && File.Exists(target)) { skipped++; report.AppendLine($"KEPT     {key}: already in the output folder"); continue; }

            Meshes.LodMesh mesh;
            try { mesh = NifGeometryReader.Read(key, bytes, passthru: true); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException or EndOfStreamException)
            { skipped++; report.AppendLine($"SKIPPED  {key}: {ex.Message}"); continue; }
            if (mesh.Parts.Count == 0)
            {
                skipped++;
                report.AppendLine($"SKIPPED  {key}: no usable shapes" + (mesh.Warnings.Count > 0 ? " (" + string.Join("; ", mesh.Warnings.Distinct().Take(2)) + ")" : ""));
                continue;
            }

            var stem = Path.GetFileNameWithoutExtension(key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, LodNifWriter.Write(stem, mesh.Parts, "AnvilLOD Oldrim converter"));
            converted++;
            report.AppendLine($"CONVERTED {key}: {mesh.Parts.Count} shapes, {mesh.Parts.Sum(p => p.TriangleCount):N0} triangles" + (mesh.Warnings.Count > 0 ? "; " + string.Join("; ", mesh.Warnings.Distinct().Take(2)) : ""));
            if (converted % 200 == 0) progress?.Report($"Converted {converted:N0} meshes...");
        }

        var reportFile = Path.Combine(req.OutputFolder, "AnvilLOD Oldrim Converter Report.txt");
        report.Insert(0, $"{found:N0} meshes checked: {converted:N0} converted, {alreadySse:N0} already Skyrim SE, {skipped:N0} left alone{Environment.NewLine}{Environment.NewLine}");
        File.WriteAllText(reportFile, report.ToString());
        sw.Stop();
        progress?.Report($"Done: {converted:N0} converted, {alreadySse:N0} already Skyrim SE, {skipped:N0} left alone, in {sw.Elapsed.TotalSeconds:F0}s");
        return new ConvertResult(found, converted, alreadySse, skipped, reportFile, sw.Elapsed);
    }

    /// <summary>LOD meshes: anything in a folder called lod (or starting with it), or with "lod" in the file name.</summary>
    public static bool IsLodPath(string normalizedPath)
    {
        var parts = normalizedPath.Split('\\');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].StartsWith("lod", StringComparison.Ordinal) || parts[i].EndsWith("lod", StringComparison.Ordinal)) return true;
        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        return stem.Contains("lod", StringComparison.Ordinal);
    }

    private static IArchiveReader? OpenArchive(string path, IProgress<string>? progress)
    {
        // Skyrim LE archives are version 104, Skyrim SE ones 105; the reader is chosen by the game release.
        foreach (var release in new[] { GameRelease.SkyrimSE, GameRelease.SkyrimLE })
        {
            try
            {
                var reader = Archive.CreateReader(release, path);
                _ = reader.Files.Count();   // the name table is read lazily
                return reader;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (release == GameRelease.SkyrimLE) progress?.Report($"Could not read {Path.GetFileName(path)}: {ex.GetBaseException().Message}");
            }
        }
        return null;
    }
}
