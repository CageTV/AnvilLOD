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
    TimeSpan Elapsed);

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
            if (mesh.Parts.Count == 0)
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
        return new LodMesh { Path = mesh.Path, Parts = parts, Warnings = mesh.Warnings };
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
                    var result = BtoBuilder.Build(quad, quads[quad], GetMesh, test, Brightness);
                    var rel = seasonSuffix is null ? quad.RelativePath : AnvilLOD.Core.Lod.SeasonSwaps.SeasonalPath(quad.RelativePath, seasonSuffix);
                    var path = Path.Combine(outputFolder, rel.Replace('\\', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var tmp = path + ".tmp";
                    File.WriteAllBytes(tmp, result.Bytes);
                    File.Move(tmp, path, overwrite: true);

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
            sw.Elapsed);
    }
}
