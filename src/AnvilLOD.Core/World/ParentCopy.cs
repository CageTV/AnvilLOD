using System.Numerics;

namespace AnvilLOD.Core.World;

/// <summary>
/// A reference of a parent worldspace (Tamriel) that DynDOLOD's "Parent &gt; Child" copy would put into a walled city's child
/// worldspace: the objects around the city that the child leaves out. AnvilLOD doesn't write a plugin for them; the SKSE plugin
/// draws the model while the player is in the child worldspace (<c>AnvilLOD.copies</c>).
/// </summary>
public sealed record ParentCopy(
    string ChildWorldspace,                 // EditorID, e.g. WhiterunWorld
    string WorldPlugin, uint WorldLocalId,  // the child worldspace, resolved in game with TESDataHandler::LookupFormID
    string SourcePlugin, uint SourceLocalId,// the parent reference (identity only)
    string? BaseEditorId,
    string Model,                           // the base record's full model, relative to Data
    Vector3 Position,
    Vector3 RotationRadians,
    float Scale,
    int CellX, int CellY,                   // the parent cell the reference is in
    bool ChildCellExists,                   // the child worldspace has a cell at these coordinates
    bool HasAlternateTextures);             // the base record swaps textures (snow / moss / tundra variants): the SKSE copy shows the default ones

/// <summary>What the Parent &gt; Child pass found and why it left things out, for the log and the report.</summary>
public sealed record ParentCopyStats(
    IReadOnlyDictionary<string, long> CopiesByChild,
    long InRegion,           // references of the parent worldspace inside a child's copy region
    long NoLod,              // ... whose base has no LOD mesh / tree billboard
    long Switchable,         // ... that start disabled, have an enable parent or are persistent
    long IgnoredByRule,      // ... skipped by IgnoreParentEDID
    long InNavmeshCells,     // ... in a child cell with a navmesh (NoCellsWithNAVM=1)
    IReadOnlyList<ParentCopyRegion>? Regions = null,
    IReadOnlyDictionary<(string Ws, int X, int Y), float>? WaterCells = null);   // parent cells with an explicit water height, inside the copy regions

/// <summary>A child worldspace's copy region: where grass LOD (which lives in the parent's blocks, hidden there) is copied too.</summary>
public sealed record ParentCopyRegion(
    string ChildWorldspace, string Parent,
    string WorldPlugin, uint WorldLocalId,
    AnvilLOD.Core.Lod.ParentCopyWorld Config,
    IReadOnlySet<(int X, int Y)> ChildCells,    // the cells the child worldspace has
    bool UsesParentWater = false,
    IReadOnlySet<(int X, int Y)>? LandCells = null);   // the cells where the child has its own terrain: its own ground and its own grass rules apply there, the parent's grass LOD must not be drawn              // the child has no water of its own and shows its parent's (Whiterun, Windhelm, Solitude); Riften has its own
