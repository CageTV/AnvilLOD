using System.Collections.Concurrent;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Plugins;

/// <summary>
/// Whole ships in object LOD, built from their real models. The LOD meshes that come with the game and DynDOLOD Resources are a
/// few hundred triangles (the longboat is 254 against 18,000 in the full model), which turns a hull into a faceted lump. This
/// simplifies the installed full model with the LOD Mesh Maker's simplifier instead, with a triangle budget that grows with the
/// ship's size, for LOD4 and LOD8. Nothing is shipped: the meshes are made on the user's machine from the models they have, so a
/// ship replacer is followed automatically.
/// </summary>
public sealed class ShipLodSource : ISyntheticMeshSource
{
    public const string Prefix = ShipLodModels.Prefix;
    private const int Version = 1;

    /// <summary>LOD4 triangles per unit of the ship's longest dimension, and the limits of that budget (a longboat gets about 4,400, a Dainty Sload 8,900, a rowboat 1,350).</summary>
    private const float TrianglesPerUnit = 2.4f, MinTriangles = 800, MaxTriangles = 12000;
    /// <summary>LOD8 gets this share of the LOD4 budget.</summary>
    private const float Lod8Share = 0.5f;

    private readonly AssetIndex _assets;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<LodMesh?>?>> _levels = new(StringComparer.OrdinalIgnoreCase);

    public ShipLodSource(AssetIndex assets) => _assets = assets;

    /// <summary>What was made, for the log: model, LOD4 triangles, LOD8 triangles (models that were asked for and could be built).</summary>
    public IReadOnlyList<(string Model, int Lod4, int Lod8)> Built()
        => _levels.Where(kv => kv.Value.IsValueCreated && kv.Value.Value is { Count: > 0 } l && l[0] is not null)
            .Select(kv => (kv.Key, kv.Value.Value![0]!.Parts.Sum(p => p.TriangleCount), kv.Value.Value![1]?.Parts.Sum(p => p.TriangleCount) ?? 0))
            .OrderBy(t => t.Key, StringComparer.Ordinal).ToList();

    private readonly ConcurrentDictionary<string, byte> _withoutStock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ship models that had no LOD mesh at all and got one made from the real model.</summary>
    public IReadOnlyCollection<string> WithoutStockLod => [.. _withoutStock.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    public bool Owns(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    public bool MapTextures(string path) => true;

    public string? Fingerprint(string path)
        => Owns(path) ? $"s{Version}|{_assets.Fingerprint(ShipLodModels.ModelOf(path)!)}" : null;

    public LodMesh? Build(string path)
    {
        var model = ShipLodModels.ModelOf(path);
        if (model is null) return null;
        int level = ShipLodModels.LevelOf(path);
        var built = _levels.GetOrAdd(model, m => new Lazy<IReadOnlyList<LodMesh?>?>(() => BuildLevels(m))).Value;
        return built is not null && level >= 0 && level < built.Count ? built[level] : null;
    }

    private IReadOnlyList<LodMesh?>? BuildLevels(string modelPath)
    {
        try
        {
            if (!_assets.TryOpen(modelPath, out var s)) return null;
            byte[] bytes;
            using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            var full = NifGeometryReader.Read(modelPath, bytes);
            if (full.Parts.Count == 0) return null;

            var (min, max) = LodMeshAuthor.Bounds(full);
            var size = max - min;
            float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            if (longest < ShipLodModels.MinLength) return null;   // a prop that matched by name, not a ship
            int lod4 = (int)Math.Clamp(longest * TrianglesPerUnit, MinTriangles, MaxTriangles);
            int lod8 = Math.Max((int)(MinTriangles / 2), (int)(lod4 * Lod8Share));
            var builder = new ShipLodBuilder(full);
            var fits = new[] { builder.Fit(lod4, 16f), builder.Fit(lod8, 48f, dropOverlays: true) };
            return fits.Select((f, level) => f.Parts.Count > 0
                ? new LodMesh { Path = ShipLodModels.MeshPath(modelPath, level, ""), Parts = f.Parts, Warnings = [], NoShapesInFile = false }
                : null).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// For <see cref="LodMeshResolver.ModelUpgrade"/>: a ship's LOD4 and LOD8 meshes are replaced by the ones made from its model;
    /// LOD16 and LOD32 keep the hand-made ones. Anything that isn't a ship, or a ship whose model can't be read, is unchanged.
    /// </summary>
    public LodMeshSet Upgrade(string modelPath, LodMeshSet set)
    {
        if (!ShipLodModels.IsShip(modelPath)) return set;
        var model = LodMeshIndex.Normalize(modelPath);
        if (Build(ShipLodModels.MeshPath(model, 0, "")) is null) return set;
        return set with
        {
            Lod4 = string.IsNullOrEmpty(set.Lod4) ? set.Lod4 : ShipLodModels.MeshPath(model, 0, set.Lod4),
            Lod8 = string.IsNullOrEmpty(set.Lod8) ? set.Lod8 : ShipLodModels.MeshPath(model, 1, set.Lod8),
        };
    }

    /// <summary>
    /// For <see cref="LodMeshResolver.ModelFallback"/>: a whole ship with no LOD mesh anywhere (a ship a mod added without making LOD for it)
    /// gets LOD4 and LOD8 made from its model. Null for anything else.
    /// </summary>
    public LodMeshSet? Fallback(string modelPath, bool rulesWantLod)
    {
        // A ship the rules want LOD for, or one of Animated Ships' bobbing wrecks (they only move up and down, and DynDOLOD's rule for
        // that mod leaves them out of LOD altogether, so wrecks it replaced would vanish from the distance).
        if (!(rulesWantLod && ShipLodModels.IsShip(modelPath)) && !ShipLodModels.IsBobbingWreck(modelPath)) return null;
        var model = LodMeshIndex.Normalize(modelPath);
        if (Build(ShipLodModels.MeshPath(model, 0, "")) is null) return null;
        _withoutStock[model] = 0;
        return new LodMeshSet(ShipLodModels.MeshPath(model, 0, ""), ShipLodModels.MeshPath(model, 1, ""), null, null);
    }
}
