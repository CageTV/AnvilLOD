using System.Collections.Concurrent;
using System.Diagnostics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Meshes;

/// <summary>Produces LOD meshes that don't exist as files (e.g. grass patches built from the grass cache).</summary>
public interface ISyntheticMeshSource
{
    bool Owns(string path);
    LodMesh? Build(string path);

    /// <summary>True for meshes made from real models, whose textures should go through the PBR and opaque-alpha mapping like any LOD mesh's.</summary>
    bool MapTextures(string path) => false;
}

public sealed record GenerateStats(
    int BlocksWritten,
    int BlocksEmpty,
    int BlocksFailed,
    long Triangles,
    long TrianglesCulled,
    int MeshesLoaded,
    IReadOnlyDictionary<string, string> MeshErrors,
    IReadOnlyDictionary<QuadKey, string> BlockErrors,
    TimeSpan Elapsed,
    IReadOnlyDictionary<int, long>? BytesByLevel = null,                                  // actual .bto bytes written, per LOD level
    IReadOnlyDictionary<(int Level, string Category), (long Triangles, long Vertices)>? SizeBreakdown = null); // source geometry placed, before buried-triangle removal

/// <summary>
/// Writes .bto files for a set of LOD blocks in parallel. Each LOD mesh is read once and
/// shared by every block that uses it.
/// </summary>
public sealed class LodGenerator
{
    private readonly IAssetSource _assets;
    private readonly ConcurrentDictionary<string, Lazy<LodMesh?>> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _meshErrors = new(StringComparer.OrdinalIgnoreCase);

    private readonly TerrainHeights? _terrain;
    private readonly ISyntheticMeshSource? _synthetic;

    /// <summary>When set, textures that the full models replace with PBR ones are swapped for matching LOD textures.</summary>
    public PbrLodTextures? Pbr { get; init; }

    /// <summary>When set, shapes without an alpha property whose diffuse texture has a translucent alpha channel point at an opaque copy.</summary>
    public OpaqueLodTextures? Opaque { get; init; }

    /// <summary>Object LOD colour multiplier (1 = as the textures are). See <see cref="BtoBuilder.Build"/>.</summary>
    public float Brightness { get; init; } = 1f;

    /// <summary>
    /// Texture variants from the scan, by id. A reference with one gets a copy of its LOD mesh whose textures follow the full model
    /// (including alternate textures such as snow or ice), shape by shape, the way DynDOLOD corrects LOD textures.
    /// </summary>
    public IReadOnlyDictionary<string, TextureVariant>? TextureVariants { get; init; }

    /// <summary>What the texture matching did, for the log.</summary>
    public TextureMatchStats Matching { get; } = new();

    public sealed class TextureMatchStats
    {
        public int Meshes, MeshesChanged, Shapes, ShapesChanged, ShapesStitched, FullModelsRead, FullModelsMissing;
    }

    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<FullShape>?>> _fullShapes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The texture slots (diffuse, normal) a shape of the full model names, before any alternate texture.</summary>
    private sealed record FullShape(string? Name, string? Diffuse, string? Normal);

    /// <summary>Names the kind of thing a mesh path is, for the size breakdown (null = everything is "objects").</summary>
    public Func<string, string>? Categorize { get; init; }

    /// <summary>Units a mesh path's geometry is pulled inside its own surface in LOD4/LOD8 (<see cref="LodInset"/>); null = none.</summary>
    public Func<string, float>? Inset { get; init; }

    /// <param name="terrain">Terrain heights for buried-triangle removal, or null to keep everything.</param>
    /// <param name="synthetic">Meshes built on the fly (grass patches). They're not cached: each is used by one block.</param>
    public LodGenerator(IAssetSource assets, TerrainHeights? terrain = null, ISyntheticMeshSource? synthetic = null)
    {
        _assets = assets;
        _terrain = terrain;
        _synthetic = synthetic;
    }

    /// <param name="key">A mesh path, or a path plus a texture variant (<see cref="LodMeshKey"/>).</param>
    public LodMesh? GetMesh(string key)
    {
        var (path, variantId) = LodMeshKey.Split(key);
        if (variantId is not null && TextureVariants is not null && TextureVariants.TryGetValue(variantId, out var variant)
            && !(_synthetic is not null && _synthetic.Owns(path)))
            return _meshes.GetOrAdd(key, _ => new Lazy<LodMesh?>(() => MatchTextures(path, variant), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        if (_synthetic is not null && _synthetic.Owns(path))
        {
            if (_synthetic.MapTextures(path))
                return _meshes.GetOrAdd(path, p => new Lazy<LodMesh?>(() => BuildMapped(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            try { return _synthetic.Build(path); }
            catch (Exception ex) { _meshErrors[path] = ex.Message; return null; }
        }
        return _meshes.GetOrAdd(path, p => new Lazy<LodMesh?>(() => Load(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>A synthetic mesh made from a real model, with the textures mapped the way a LOD mesh's are.</summary>
    private LodMesh? BuildMapped(string path)
    {
        try
        {
            var mesh = _synthetic!.Build(path);
            if (mesh is null) { _meshErrors[path] = "could not be built"; return null; }
            if (Pbr is not null) mesh = ApplyPbr(mesh, Pbr);
            if (Opaque is not null) mesh = ApplyOpaque(mesh, Opaque);
            return mesh;
        }
        catch (Exception ex) { _meshErrors[path] = ex.Message; return null; }
    }

    private LodMesh? Load(string path)
    {
        try
        {
            if (!_assets.TryOpen(path, out var s)) { _meshErrors[path] = "not found"; return null; }
            byte[] bytes;
            using (s)
            {
                if (s is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 && seg.Count == seg.Array!.Length)
                    bytes = seg.Array;
                else
                {
                    using var copy = new MemoryStream();
                    s.CopyTo(copy);
                    bytes = copy.ToArray();
                }
            }
            var mesh = NifGeometryReader.Read(path, bytes);
            if (Pbr is not null) mesh = ApplyPbr(mesh, Pbr);
            if (Opaque is not null) mesh = ApplyOpaque(mesh, Opaque);
            if (mesh.Parts.Count == 0 && !mesh.NoShapesInFile)
                _meshErrors[path] = mesh.Warnings.Count > 0 ? string.Join("; ", mesh.Warnings.Distinct()) : "no usable shapes";
            return mesh;
        }
        catch (Exception ex)
        {
            _meshErrors[path] = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The LOD mesh with its textures made to follow the base record's full model. LOD models are cut from full models and keep their
    /// shape names, so a shape whose texture differs from the full model's (a snow variant of a rock, an ice or dirt texture set)
    /// gets the full model's texture, with the alternate textures of the base record applied. Shapes that use stitched LOD
    /// textures (textures\lod\...) can't be compared with the full texture and are left as they are.
    /// </summary>
    private LodMesh? MatchTextures(string path, TextureVariant variant)
    {
        var lod = GetMesh(path);
        if (lod is null || lod.Parts.Count == 0) return lod;
        Interlocked.Increment(ref Matching.Meshes);

        var full = _fullShapes.GetOrAdd(variant.FullModel, p => new Lazy<IReadOnlyList<FullShape>?>(() => ReadFullShapes(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (full is null || full.Count == 0) return lod;

        bool changed = false;
        var parts = new List<LodMeshPart>(lod.Parts.Count);
        foreach (var p in lod.Parts)
        {
            Interlocked.Increment(ref Matching.Shapes);
            var part = p;
            var m = p.Material;
            var shape = FindShape(full, p.Name);
            if (shape is not null && m.Textures.Count > 0)
            {
                if (IsStitchedLodTexture(m.Textures[0])) Interlocked.Increment(ref Matching.ShapesStitched);
                else
                {
                    string? diffuse = shape.Diffuse, normal = shape.Normal;
                    foreach (var alt in variant.Alternates)
                    {
                        if (!ShapeNameMatches(alt.ShapeName, shape.Name)) continue;
                        if (!string.IsNullOrEmpty(alt.Diffuse)) diffuse = alt.Diffuse;
                        if (!string.IsNullOrEmpty(alt.Normal)) normal = alt.Normal;
                    }
                    // A texture the full model or its texture set names but that exists nowhere (a PBR path from a patch whose textures
                    // aren't installed) would draw purple on the LOD mesh: use its non-PBR twin, or leave the LOD mesh as it is.
                    diffuse = ExistingTexture(diffuse);
                    normal = ExistingTexture(normal);
                    // Same texture mapping the LOD mesh went through (a full texture that has a PBR twin points at the converted copy).
                    if (!string.IsNullOrWhiteSpace(diffuse) && Pbr?.MapDiffuse(diffuse) is { } mapped)
                    {
                        if (!string.IsNullOrWhiteSpace(normal) && Pbr.MapNormal(diffuse, normal) is { } mappedNormal) normal = mappedNormal;
                        diffuse = mapped;
                    }
                    // A shape without an alpha property is drawn opaque-or-holey by its texture's alpha: same opaque copy the LOD mesh got.
                    if (!m.HasAlpha && !string.IsNullOrWhiteSpace(diffuse) && Opaque?.MapDiffuse(diffuse) is { } opaque) diffuse = opaque;
                    if (!string.IsNullOrWhiteSpace(diffuse) && !SamePath(diffuse, m.Textures[0]))
                    {
                        var tex = m.Textures.ToList();
                        tex[0] = diffuse;
                        if (tex.Count > 1 && !string.IsNullOrWhiteSpace(normal)) tex[1] = normal;
                        part = new LodMeshPart
                        {
                            Name = p.Name, Material = m with { Textures = tex }, Positions = p.Positions, UVs = p.UVs, Normals = p.Normals,
                            Tangents = p.Tangents, Bitangents = p.Bitangents, Colors = p.Colors, Triangles = p.Triangles,
                        };
                        changed = true;
                        Interlocked.Increment(ref Matching.ShapesChanged);
                    }
                }
            }
            parts.Add(part);
        }
        if (!changed) return lod;
        Interlocked.Increment(ref Matching.MeshesChanged);
        return new LodMesh { Path = lod.Path, Parts = parts, Warnings = lod.Warnings, NoShapesInFile = lod.NoShapesInFile };
    }

    private IReadOnlyList<FullShape>? ReadFullShapes(string modelPath)
    {
        try
        {
            if (!_assets.TryOpen(modelPath, out var s)) { Interlocked.Increment(ref Matching.FullModelsMissing); return null; }
            byte[] bytes;
            using (s) { using var copy = new MemoryStream(); s.CopyTo(copy); bytes = copy.ToArray(); }
            var mesh = NifGeometryReader.Read(modelPath, bytes);
            Interlocked.Increment(ref Matching.FullModelsRead);
            return mesh.Parts.Select(p => new FullShape(p.Name,
                p.Material.Textures.Count > 0 ? p.Material.Textures[0] : null,
                p.Material.Textures.Count > 1 ? p.Material.Textures[1] : null)).ToList();
        }
        catch (Exception) { Interlocked.Increment(ref Matching.FullModelsMissing); return null; }
    }

    /// <summary>The texture if it exists, else the same path without a leading textures\pbr\ if that exists, else null.</summary>
    private string? ExistingTexture(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return null;
        var t = texture.Replace('/', '\\').Trim();
        if (_assets.Exists(t)) return t;
        const string pbr = "textures\\pbr\\";
        if (t.StartsWith(pbr, StringComparison.OrdinalIgnoreCase))
        {
            var plain = "textures\\" + t[pbr.Length..];
            if (_assets.Exists(plain)) return plain;
        }
        return null;
    }

    private static bool SamePath(string? a, string? b) =>
        string.Equals((a ?? "").Replace('/', '\\').Trim(), (b ?? "").Replace('/', '\\').Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ShapeNameMatches(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The full model's shape with the LOD shape's name; merged LOD shapes are named "a - b", so any of their parts will do.</summary>
    private static FullShape? FindShape(IReadOnlyList<FullShape> full, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var f in full) if (ShapeNameMatches(f.Name, name)) return f;
        if (name.Contains(" - ", StringComparison.Ordinal))
            foreach (var piece in name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                foreach (var f in full) if (ShapeNameMatches(f.Name, piece)) return f;
        return null;
    }

    /// <summary>textures\lod\..., or a file named like xxx_lod / xxxlod: stitched or reduced LOD textures that have no full-texture twin to compare with.</summary>
    private static bool IsStitchedLodTexture(string? texture)
    {
        if (string.IsNullOrWhiteSpace(texture)) return false;
        var t = texture.Replace('/', '\\').ToLowerInvariant();
        if (t.Contains("\\lod\\", StringComparison.Ordinal) || t.StartsWith("lod\\", StringComparison.Ordinal)) return true;
        var name = Path.GetFileNameWithoutExtension(t);
        if (name.EndsWith("_n", StringComparison.Ordinal)) name = name[..^2];
        return name.EndsWith("lod", StringComparison.Ordinal) || name.Contains("_lod", StringComparison.Ordinal);
    }

    private static LodMesh ApplyOpaque(LodMesh mesh, OpaqueLodTextures opaque)
    {
        var cache = new Dictionary<string, LodMaterial>(StringComparer.Ordinal);
        var parts = new List<LodMeshPart>(mesh.Parts.Count);
        foreach (var p in mesh.Parts)
        {
            var m = p.Material;
            if (!cache.TryGetValue(m.Key, out var swapped))
            {
                swapped = m;
                if (!m.HasAlpha && m.Textures.Count > 0 && opaque.MapDiffuse(m.Textures[0]) is { } diffuse)
                {
                    var tex = m.Textures.ToList();
                    tex[0] = diffuse;
                    swapped = m with { Textures = tex };
                }
                cache[m.Key] = swapped;
            }
            parts.Add(ReferenceEquals(swapped, m) ? p : new LodMeshPart
            {
                Name = p.Name, Material = swapped, Positions = p.Positions, UVs = p.UVs, Normals = p.Normals,
                Tangents = p.Tangents, Bitangents = p.Bitangents, Colors = p.Colors, Triangles = p.Triangles,
            });
        }
        return new LodMesh { Path = mesh.Path, Parts = parts, Warnings = mesh.Warnings, NoShapesInFile = mesh.NoShapesInFile };
    }

    private static LodMesh ApplyPbr(LodMesh mesh, PbrLodTextures pbr)
    {
        var cache = new Dictionary<string, LodMaterial>(StringComparer.Ordinal);
        var parts = new List<LodMeshPart>(mesh.Parts.Count);
        foreach (var p in mesh.Parts)
        {
            var m = p.Material;
            if (!cache.TryGetValue(m.Key, out var swapped))
            {
                swapped = m;
                if (m.Textures.Count > 0 && pbr.MapDiffuse(m.Textures[0]) is { } diffuse)
                {
                    var tex = m.Textures.ToList();
                    tex[0] = diffuse;
                    if (tex.Count > 1 && !string.IsNullOrWhiteSpace(tex[1]) && pbr.MapNormal(m.Textures[0], tex[1]) is { } normal) tex[1] = normal;
                    swapped = m with { Textures = tex };
                }
                cache[m.Key] = swapped;
            }
            parts.Add(ReferenceEquals(swapped, m) ? p : new LodMeshPart
            {
                Name = p.Name, Material = swapped, Positions = p.Positions, UVs = p.UVs, Normals = p.Normals,
                Tangents = p.Tangents, Bitangents = p.Bitangents, Colors = p.Colors, Triangles = p.Triangles,
            });
        }
        return new LodMesh { Path = mesh.Path, Parts = parts, Warnings = mesh.Warnings, NoShapesInFile = mesh.NoShapesInFile };
    }

    /// <summary>
    /// Textures (diffuse and normal slots) that the loaded LOD meshes name but that exist nowhere: not loose, not in a
    /// BSA, not written into the output. A texture the engine can't find draws as purple (DynDOLOD's "File Not Found
    /// Textures"). Call after everything that writes textures into the output has run. Key = texture, value = first
    /// mesh that uses it and how many meshes do.
    /// </summary>
    public IReadOnlyDictionary<string, (string FirstMesh, int Meshes)> AuditTextures(string outputFolder) =>
        FindMissingTextures(_meshes.Values.Where(l => l.IsValueCreated && l.Value is not null).Select(l => l.Value!),
            tex => _assets.Exists(tex) || File.Exists(Path.Combine(outputFolder, tex)));

    /// <summary>
    /// Opens every texture the loaded LOD meshes name (all nine slots) and checks its DDS header. A texture the engine can't create is
    /// worse than a missing one: the shader that uses it dereferences a null texture and the game crashes. Key = texture, value = the
    /// problem and the first mesh that uses it.
    /// </summary>
    public IReadOnlyDictionary<string, (string Problem, string FirstMesh)> AuditTextureFiles(string outputFolder)
    {
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in _meshes.Values)
        {
            if (!l.IsValueCreated || l.Value is not { } mesh) continue;
            foreach (var part in mesh.Parts)
                foreach (var raw in part.Material.Textures)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var tex = raw.Replace('/', '\\').TrimStart('\\');
                    if (!tex.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) tex = "textures\\" + tex;
                    used.TryAdd(tex, mesh.Path);
                }
        }
        var bad = new System.Collections.Concurrent.ConcurrentDictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(used, kv =>
        {
            try
            {
                Stream? stream = null;
                var local = Path.Combine(outputFolder, kv.Key);
                if (File.Exists(local)) stream = File.OpenRead(local);
                else if (_assets.TryOpen(kv.Key, out var s)) stream = s;
                if (stream is null) return;   // missing textures are AuditTextures' business
                using (stream)
                {
                    var head = new byte[148];
                    int n = 0;
                    while (n < head.Length) { int r = stream.Read(head, n, head.Length - n); if (r <= 0) break; n += r; }
                    long length = stream.CanSeek ? stream.Length : n;
                    var problem = DdsHeader.Problem(head.AsSpan(0, n), length);
                    if (problem is not null) bad[kv.Key] = (problem, kv.Value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                bad[kv.Key] = ("could not be read: " + ex.Message, kv.Value);
            }
        });
        return bad.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The pure part of <see cref="AuditTextures"/>: slots 0 (diffuse) and 1 (normal) of every part's material.</summary>
    public static IReadOnlyDictionary<string, (string FirstMesh, int Meshes)> FindMissingTextures(IEnumerable<LodMesh> meshes, Func<string, bool> exists)
    {
        var missing = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        var seenPerMesh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshes)
        {
            seenPerMesh.Clear();
            foreach (var part in mesh.Parts)
                foreach (var raw in part.Material.Textures.Take(2))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var tex = raw.Replace('/', '\\').TrimStart('\\');
                    if (!tex.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) tex = "textures\\" + tex;
                    if (!seenPerMesh.Add(tex) || exists(tex)) continue;
                    missing[tex] = missing.TryGetValue(tex, out var m) ? (m.Item1, m.Item2 + 1) : (mesh.Path, 1);
                }
        }
        return missing.ToDictionary(kv => kv.Key, kv => (kv.Value.Item1, kv.Value.Item2), StringComparer.OrdinalIgnoreCase);
    }

    public GenerateStats Generate(
        IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> quads,
        IReadOnlyCollection<QuadKey> toBuild,
        string outputFolder,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        string? seasonSuffix = null)   // "WIN" → <world>.<L>.<X>.<Y>.WIN.bto (Seasons of Skyrim)
    {
        var sw = Stopwatch.StartNew();
        int written = 0, empty = 0, failed = 0, done = 0;
        long tris = 0, culledTris = 0;
        var blockErrors = new ConcurrentDictionary<QuadKey, string>();
        var bytesByLevel = new ConcurrentDictionary<int, long>();
        var breakdown = new ConcurrentDictionary<(int, string), (long, long)>();
        int total = toBuild.Count;
        var lastReport = Stopwatch.StartNew();

        Parallel.ForEach(
            toBuild.OrderByDescending(q => quads[q].Count), // big blocks first for better load balance
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            quad =>
            {
                try
                {
                    var test = _terrain is null ? null
                        : new BtoBuilder.TerrainTest((x, y) => _terrain.HeightAt(quad.Worldspace, x, y), TerrainHeights.MarginFor(quad.Level));
                    var result = BtoBuilder.Build(quad, quads[quad], GetMesh, test, Brightness, Categorize, Inset);
                    var rel = seasonSuffix is null ? quad.RelativePath : AnvilLOD.Core.Lod.SeasonSwaps.SeasonalPath(quad.RelativePath, seasonSuffix);
                    var path = Path.Combine(outputFolder, rel.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, result.Bytes);
                    File.Move(tmp, path, overwrite: true);

                    bytesByLevel.AddOrUpdate((int)quad.Level, result.Bytes.Length, (_, v) => v + result.Bytes.Length);
                    if (result.ByCategory is not null)
                        foreach (var (cat, st) in result.ByCategory)
                            breakdown.AddOrUpdate(((int)quad.Level, cat), (st.Triangles, st.Vertices), (_, v) => (v.Item1 + st.Triangles, v.Item2 + st.Vertices));
                    if (result.Shapes == 0) Interlocked.Increment(ref empty);
                    Interlocked.Increment(ref written);
                    Interlocked.Add(ref tris, result.Triangles);
                    Interlocked.Add(ref culledTris, result.TrianglesCulled);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    blockErrors[quad] = ex.Message;
                    Interlocked.Increment(ref failed);
                }

                int n = Interlocked.Increment(ref done);
                if (progress is not null && (n == total || lastReport.ElapsedMilliseconds > 500))
                {
                    lock (lastReport)
                    {
                        if (n == total || lastReport.ElapsedMilliseconds > 500)
                        {
                            lastReport.Restart();
                            progress.Report($"Written {n:N0} / {total:N0} blocks ({_meshes.Count:N0} meshes loaded)");
                        }
                    }
                }
            });

        return new GenerateStats(written, empty, failed, tris, culledTris, _meshes.Count,
            _meshErrors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            blockErrors.ToDictionary(kv => kv.Key, kv => kv.Value),
            sw.Elapsed,
            bytesByLevel.ToDictionary(kv => kv.Key, kv => kv.Value),
            breakdown.ToDictionary(kv => (kv.Key.Item1, kv.Key.Item2), kv => (kv.Value.Item1, kv.Value.Item2)));
    }
}
