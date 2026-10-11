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

    /// <summary>Object LOD colour multiplier (1 = as the textures are). See <see cref="BtoBuilder.Build"/>.</summary>
    public float Brightness { get; init; } = 1f;

    /// <summary>Names the kind of thing a mesh path is, for the size breakdown (null = everything is "objects").</summary>
    public Func<string, string>? Categorize { get; init; }

    /// <param name="terrain">Terrain heights for buried-triangle removal, or null to keep everything.</param>
    /// <param name="synthetic">Meshes built on the fly (grass patches). They're not cached: each is used by one block.</param>
    public LodGenerator(IAssetSource assets, TerrainHeights? terrain = null, ISyntheticMeshSource? synthetic = null)
    {
        _assets = assets;
        _terrain = terrain;
        _synthetic = synthetic;
    }

    public LodMesh? GetMesh(string path)
    {
        if (_synthetic is not null && _synthetic.Owns(path))
        {
            try { return _synthetic.Build(path); }
            catch (Exception ex) { _meshErrors[path] = ex.Message; return null; }
        }
        return _meshes.GetOrAdd(path, p => new Lazy<LodMesh?>(() => Load(p), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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
                    var result = BtoBuilder.Build(quad, quads[quad], GetMesh, test, Brightness, Categorize);
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
