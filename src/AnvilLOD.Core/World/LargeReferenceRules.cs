using System.Numerics;

namespace AnvilLOD.Core.World;

/// <summary>
/// Which references the engine's large reference grid can show. The grid lists references, per cell, in the worldspace record of
/// an ESM-flagged plugin; it then loads their full models beyond the normally loaded cells (<c>uLargeRefLODGridSize</c>).
/// Conditions, from the DynDOLOD documentation and Skyrim.esm: the base record is a STAT, or a MSTT with record flag 0x4; the
/// bounds magnitude times the reference scale is more than the game setting <c>fLargeRefMinSize</c> (512 in Skyrim.esm); the
/// reference is defined in, and not overridden outside of, ESM-flagged plugins; and it doesn't start disabled.
/// <para>
/// The "magnitude" is the half diagonal of the object bounds (the radius of the box), which is what reproduces the list Bethesda
/// shipped: in Skyrim.esm's Tamriel all 28,567 STAT references whose full diagonal times scale is at least 1,024 are listed, and
/// 1 percent of the smaller ones are. Using the full diagonal would list about twice as many references.
/// </para>
/// </summary>
public static class LargeReferenceRules
{
    /// <summary>Skyrim.esm's value of fLargeRefMinSize.</summary>
    public const float DefaultMinSize = 512f;

    /// <summary>Record flag a MSTT needs to be a large reference.</summary>
    public const int MoveableStaticFlag = 0x4;

    /// <summary>Record flag: initially disabled.</summary>
    public const int InitiallyDisabled = 0x800;

    /// <summary>Half the length of the object bounds diagonal (game units) times the reference scale.</summary>
    public static float Magnitude(Vector3 boundsMin, Vector3 boundsMax, float scale) =>
        Vector3.Distance(boundsMin, boundsMax) * 0.5f * (scale > 0 ? scale : 1f);

    public static bool IsLargeEnough(Vector3 boundsMin, Vector3 boundsMax, float scale, float minSize = DefaultMinSize) =>
        Magnitude(boundsMin, boundsMax, scale) > minSize;

    public static bool MoveableStaticQualifies(int recordFlags) => (recordFlags & MoveableStaticFlag) != 0;

    public static bool StartsDisabled(int recordFlags) => (recordFlags & InitiallyDisabled) != 0;

    /// <summary>Why a reference was not listed (for the report).</summary>
    public enum Reason
    {
        Listed,
        AlreadyListed,
        TooSmall,
        WrongBase,
        StartsDisabled,
        DefinedOutsideEsm,
        OverriddenOutsideEsm,
    }
}
