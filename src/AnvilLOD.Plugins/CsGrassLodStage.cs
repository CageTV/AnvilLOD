using System.Collections.Concurrent;
using System.Diagnostics;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// LOD meshes for Community Shaders' Grass Optimizations feature. The feature draws the real grass itself and, when its mesh LODs are on, swaps a
/// grass model's mesh for <c>meshes\LOD\Grass\&lt;model name&gt;_LOD0.nif</c> (middle distance) and <c>_LOD1.nif</c> (far) while keeping the grass
/// shape's own shader and texture. It takes only the first triangle shape's vertex and index buffers from those files, so a LOD mesh must use the
/// texture and UVs of the full model and the same vertex layout. Made here by simplifying the installed grass model (UVs, normals and vertex colours
/// are kept, so the texture matches without an atlas), for the models whose layout is the standard grass one. A grass type that has no file keeps
/// its full mesh, so nothing here is ever required. DynDOLOD's own assets for the feature (a billboard atlas and rewritten full models) are a
/// different, heavier route; these are the simple one.
/// </summary>
public static class CsGrassLodStage
{
    /// <summary>The feature's shader folder ini; present when the Grass Optimizations mod (or a Community Shaders build that includes it) is installed.</summary>
    public const string FeatureIni = "shaders\\features\\grassoptimizations.ini";

    /// <summary>Where the feature looks for LOD meshes, under Data.</summary>
    public const string Folder = "meshes\\LOD\\Grass";

    // The vertex layouts AnvilLOD's NIF writer produces (with and without vertex colours). A model with any other layout is left alone, because
    // the mesh the feature swaps in must be readable by the same vertex shader input layout.
    private const ulong LayoutWithColors = 0x0003B00007650408UL;
    private const ulong LayoutWithoutColors = 0x0001B00000650407UL;

    /// <summary>Models with at most this many triangles are already as cheap as a LOD mesh would be.</summary>
    private const int MinTriangles = 12;

    public sealed record Result(
        int Models, int Middle, int Far, int Authored, int TooSmall, int OtherLayout, int LegacyFormat, int MultiShape, int DuplicateNames, int Failed,
        long TrianglesFull, long TrianglesMiddle, long TrianglesFar, TimeSpan Elapsed, IReadOnlyList<string> Errors);

    public static bool FeaturePresent(IAssetSource assets) => assets.Exists(FeatureIni);

    /// <summary>Deletes what this stage wrote (used when the option is off, or before a fresh run). Only the folder inside the output.</summary>
    public static void Remove(string output)
    {
        try
        {
            var dir = Path.Combine(output, Folder);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static Result Run(GameContext game, IAssetSource assets, string output, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Remove(output);

        // Each distinct model once; the feature finds the LOD files by the model's file name alone, so two models sharing a name can't both have one.
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0;
        foreach (var g in game.LoadOrder.PriorityOrder.Grass().WinningOverrides())
        {
            var file = g.Model?.File;
            if (file is null || file.IsNull) continue;
            var path = GamePath.Normalize(file.DataRelativePath.Path);
            if (!path.StartsWith("meshes\\", StringComparison.Ordinal)) path = "meshes\\" + path;
            var stem = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            if (byStem.TryGetValue(stem, out var existing))
            {
                if (!existing.Equals(path, StringComparison.OrdinalIgnoreCase)) duplicates++;
                continue;
            }
            byStem[stem] = path;
        }

        int middle = 0, far = 0, authored = 0, tooSmall = 0, otherLayout = 0, legacy = 0, multi = 0, failed = 0;
        long trisFull = 0, trisMiddle = 0, trisFar = 0;
        var errors = new ConcurrentBag<string>();
        var dir = Path.Combine(output, Folder);

        Parallel.ForEach(byStem, new ParallelOptions { CancellationToken = ct }, kv =>
        {
            var (stem, model) = (kv.Key, kv.Value);
            try
            {
                if (!assets.TryOpen(model, out var s)) return; // a grass record whose model is not installed (a DLC that is not there): nothing to make
                byte[] bytes;
                using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }

                LodMesh mesh;
                ulong? layout;
                try
                {
                    layout = NifGeometryReader.FirstShapeVertexDescriptor(bytes);
                    mesh = NifGeometryReader.Read(model, bytes, passthru: true);
                }
                catch (NotSupportedException) { Interlocked.Increment(ref legacy); return; } // a Skyrim LE (Oldrim) model: can't be read here
                if (mesh.Parts.Count == 0) { Interlocked.Increment(ref failed); return; }
                if (mesh.Parts.Count > 1) { Interlocked.Increment(ref multi); return; } // the feature gives every shape of a model the same LOD mesh
                if (layout is not (LayoutWithColors or LayoutWithoutColors)) { Interlocked.Increment(ref otherLayout); return; }

                var part = mesh.Parts[0];
                int tris = part.TriangleCount;
                if (tris < MinTriangles) { Interlocked.Increment(ref tooSmall); return; }
                Interlocked.Add(ref trisFull, tris);

                bool haveMiddle = assets.Exists($"{Folder}\\{stem}_LOD0.nif"), haveFar = assets.Exists($"{Folder}\\{stem}_LOD1.nif");
                if (haveMiddle || haveFar) Interlocked.Increment(ref authored); // the grass author made some: theirs win, only the missing tier is made

                // Middle distance keeps about half of the triangles, far about a fifth (at least a crossed pair of quads, at most 64).
                float diag = LodMeshAuthor.Diagonal(part);
                int capMiddle = Math.Max(6, tris * 55 / 100), capFar = Math.Clamp(tris / 5, 4, 64);
                var levels = LodMeshAuthor.Build(mesh, [new AuthorLevel(0, diag * 0.01f, 0f, capMiddle), new AuthorLevel(1, diag * 0.04f, 0f, capFar)]);
                Directory.CreateDirectory(dir);
                foreach (var lv in levels)
                {
                    bool isFar = lv.Level == 1;
                    if (isFar ? haveFar : haveMiddle) continue;
                    // A result that is barely smaller than the model, or has lost the shape, is not worth swapping in.
                    if (lv.Parts.Count != 1 || lv.Triangles == 0 || lv.Triangles > tris * 9 / 10) continue;
                    var name = $"{stem}_LOD{lv.Level}";
                    File.WriteAllBytes(Path.Combine(dir, name + ".nif"), LodNifWriter.Write(name, lv.Parts, "AnvilLOD Grass Optimizations LOD"));
                    if (isFar) { Interlocked.Increment(ref far); Interlocked.Add(ref trisFar, lv.Triangles); }
                    else { Interlocked.Increment(ref middle); Interlocked.Add(ref trisMiddle, lv.Triangles); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{model}: {ex.Message}");
            }
        });

        return new Result(byStem.Count, middle, far, authored, tooSmall, otherLayout, legacy, multi, duplicates, failed,
            trisFull, trisMiddle, trisFar, sw.Elapsed, [.. errors.Take(20)]);
    }
}
