using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Pipeline;

/// <summary>
/// Resolves data-relative asset paths across loose files and BSAs, honoring load order.
/// Implemented in AnvilLOD.Plugins (Mutagen); swappable for tests or a VFS.
/// </summary>
public interface IAssetSource
{
    bool Exists(string dataRelativePath);
    bool TryOpen(string dataRelativePath, out Stream stream);

    /// <summary>Cheap identity for change detection (e.g. "loose:size:mtime" or "bsa-name:size").</summary>
    string? Fingerprint(string dataRelativePath);
}

/// <summary>Produces the reference list and grids for the requested worldspaces. Implemented in AnvilLOD.Plugins.</summary>
public interface IReferenceScanner
{
    ScanResult Scan(ScanOptions options, IProgress<string>? progress = null, CancellationToken ct = default);
}

public sealed record ScanOptions(
    IReadOnlyCollection<string>? Worldspaces = null,   // null = every worldspace with a .lod file
    bool IncludeInitiallyDisabled = false,
    bool IncludeEnableParented = true,
    bool TreeLod = true,                               // also collect TREE references with billboards
    bool DynamicLod = true,                            // switchable refs (quest-toggled) go to the SKSE controller instead of static LOD
    bool GridObjects = true,                           // DynDOLOD grid objects (water, waterfalls, fires, windmills): drawn animated by the SKSE plugin
    Tree3DSettings? Tree3D = null);                    // 3D tree LOD models in object LOD (null / Enabled = false: billboards only)

public sealed record ScanResult(
    IReadOnlyDictionary<string, LodGrid> Grids,
    IReadOnlyList<LodReference> References,
    ScanStats Stats,
    IReadOnlyList<TreeReference>? Trees = null,
    IReadOnlyDictionary<string, long>? MissingBillboards = null,   // "plugin\model_formid" -> placed count
    IReadOnlyList<DynamicLodReference>? Dynamic = null);

public sealed record ScanStats(
    int PluginsInLoadOrder,
    long PlacedObjectsVisited,
    long LodReferencesFound,
    long SkippedDisabled,
    long SkippedMissingMesh,
    IReadOnlyDictionary<string, long> MissingMeshes,
    TimeSpan Elapsed);

/// <summary>Writes one object-LOD block (.bto). Implemented in AnvilLOD.Meshes.</summary>
public interface IObjectLodWriter
{
    Task WriteAsync(QuadKey quad, IReadOnlyList<LodReference> refs, string outputDataFolder, CancellationToken ct = default);
}

/// <summary>Builds the LOD texture atlas (TexGen replacement). Implemented in AnvilLOD.Textures.</summary>
public interface IAtlasBuilder
{
    Task<AtlasResult> BuildAsync(IReadOnlyCollection<string> sourceTextures, string outputDataFolder, CancellationToken ct = default);
}

public sealed record AtlasResult(string AtlasDiffusePath, string? AtlasNormalPath, IReadOnlyDictionary<string, AtlasRect> Placements);

public readonly record struct AtlasRect(float U0, float V0, float U1, float V1);

/// <summary>
/// A reference whose enable state can change while the game runs (quest-built or -destroyed places such as
/// Helgen Reborn). It gets no static LOD; the SKSE controller shows its LOD mesh while the reference is enabled.
/// </summary>
public sealed record DynamicLodReference(
    LodReference Ref,
    string RefPlugin, uint RefLocalId,             // resolved in game with TESDataHandler::LookupFormID
    string WorldspacePlugin, uint WorldspaceLocalId,
    string? ParentPlugin = null, uint ParentLocalId = 0,   // enable parent (persistent, so the game can always look it up)
    bool ParentOpposite = false,
    bool InitiallyDisabled = false,
    DynamicGrid Grid = DynamicGrid.None)                   // set for grid objects (always-dynamic, animated), None for switchable refs
{
    public bool IsGridObject => Grid != DynamicGrid.None;
}
