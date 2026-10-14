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
    Tree3DSettings? Tree3D = null,                     // 3D tree LOD models in object LOD (null / Enabled = false: billboards only)
    bool MatchTextures = true,                         // LOD mesh textures follow the full model (alternate textures, snow variants), like DynDOLOD
    bool ParentCopies = false,                         // DynDOLOD's Parent > Child copies around walled cities, drawn by the SKSE plugin
    bool GlowWindows = false,                          // DynDOLOD's window glow meshes (<model>glow_lod_N.nif), drawn by the SKSE plugin and lit at night
    bool PbrWaterfalls = false,                        // a waterfall whose full model is PBR-patched (textures\pbr\...) is drawn with that model instead of the plain LOD one
    bool RenderTreeBillboards = false);                // trees without a TexGen billboard get one drawn from their model (TexGen not needed for them); TexGen-skipped small plants stay out

public sealed record ScanResult(
    IReadOnlyDictionary<string, LodGrid> Grids,
    IReadOnlyList<LodReference> References,
    ScanStats Stats,
    IReadOnlyList<TreeReference>? Trees = null,
    IReadOnlyDictionary<string, long>? MissingBillboards = null,   // "plugin\model_formid" -> placed count
    IReadOnlyList<DynamicLodReference>? Dynamic = null,
    IReadOnlyDictionary<string, TextureVariant>? TextureVariants = null,   // by LodReference.TextureVariant
    IReadOnlyList<ParentCopy>? ParentCopies = null,
    ParentCopyStats? ParentCopyStats = null,
    IReadOnlyDictionary<string, long>? MissingBillboardsByWorld = null,   // worldspace -> placed trees that have no billboard
    IReadOnlyDictionary<string, long>? MissingBillboardsByWorldType = null,
    IReadOnlySet<string>? TooSmallTreeKeys = null);   // "plugin;formid" of tree types the billboard renderer left out as too small (same key form as TexGen's lists)   // "worldspace|plugin\model_formid" -> placed count

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
    DynamicGrid Grid = DynamicGrid.None,                   // set for grid objects (always-dynamic, animated), None for switchable refs
    bool Glow = false,                                     // a window glow mesh: the SKSE plugin lights it by the hour instead of leaving it on
    bool StandIn = false,                                  // ChildworldMatches lists it as a stand-in for a real object of a city worldspace: hidden inside the loaded cells while the player is in that city
    bool NativeBacked = false)                             // a real reference in AnvilLOD.esp stands for it in its own worldspace; the plugin draws the model only while the player is in another one (a city)
{
    public bool IsGridObject => Grid != DynamicGrid.None;
}
