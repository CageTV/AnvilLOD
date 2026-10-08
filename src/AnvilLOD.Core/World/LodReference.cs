using System.Numerics;

namespace AnvilLOD.Core.World;

/// <summary>
/// One placed reference that should appear in object LOD, already resolved to its winning override.
/// Plain data so the mesh stage never has to touch Mutagen.
/// </summary>
public sealed record LodReference(
    string FormKey,              // "0001A2B3:Skyrim.esm"
    string BaseFormKey,
    string? BaseEditorId,
    string Worldspace,           // worldspace EditorID
    string WinningPlugin,        // plugin that provided the winning override of the REFR
    Vector3 Position,
    Vector3 RotationRadians,
    float Scale,
    LodMeshSet Meshes,
    LodReferenceFlags Flags)
{
    public CellCoord Cell => CellCoord.FromWorld(Position.X, Position.Y);
}

/// <summary>Data-relative LOD mesh paths for the four MNAM slots (null/empty = no LOD at that level).</summary>
public sealed record LodMeshSet(string? Lod4, string? Lod8, string? Lod16, string? Lod32)
{
    public string? For(LodLevel level) => level switch
    {
        LodLevel.Lod4 => Lod4,
        LodLevel.Lod8 => Lod8,
        LodLevel.Lod16 => Lod16,
        LodLevel.Lod32 => Lod32,
        _ => null,
    };

    public bool HasAny =>
        !string.IsNullOrEmpty(Lod4) || !string.IsNullOrEmpty(Lod8) ||
        !string.IsNullOrEmpty(Lod16) || !string.IsNullOrEmpty(Lod32);
}

[Flags]
public enum LodReferenceFlags
{
    None = 0,
    Persistent = 1 << 0,
    InitiallyDisabled = 1 << 1,
    HasEnableParent = 1 << 2,
    BaseHasDistantLodFlag = 1 << 3,
    IsFullLod = 1 << 4,
}
