using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;

namespace AnvilLOD.Plugins;

/// <summary>
/// Grid objects the engine itself can animate. A model the SKSE plugin attaches to the scene is never updated by the engine (its
/// controllers keep start and last time at -FLT_MAX), so waterfall flow, windmill blades and water wheels stand still. DynDOLOD makes them
/// real references: a STAT base for the effect LOD meshes (waterfall bodies and skirts), a MSTT base for models that animate through a
/// controller manager (fans, rotors, wheels), and a persistent, full-LOD reference per placement (<c>DynDOLOD.esm</c> / <c>.esp</c>, measured in
/// docs\dyndolod-plugin-inventory.md). This picks the grid objects to turn into the same thing in <c>AnvilLOD.esp</c>; the SKSE plugin then
/// leaves them out of <c>AnvilLOD.dyn</c>.
/// </summary>
public static class NativeGridObjects
{
    /// <summary>
    /// Worldspaces they are written for. Tamriel and Solstheim have their persistent cell overridden for the underside anyway, and
    /// their cells are not shared with a parent (a persistent full-LOD reference of a child worldspace with the same cell coordinates is
    /// drawn in the parent too).
    /// </summary>
    public static readonly IReadOnlySet<string> Worlds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tamriel", "DLC2SolstheimWorld" };

    public sealed record Pick(DynamicLodReference Source, string Model, bool Movable);

    /// <summary>The model the SKSE plugin would draw for this grid object, relative to Data\meshes, normalized.</summary>
    public static string? ModelOf(DynamicLodReference d)
    {
        var r = d.Ref;
        var mesh = r.Meshes.Lod4 ?? r.Meshes.Lod8 ?? r.Meshes.Lod16 ?? r.Meshes.Lod32;
        if (string.IsNullOrEmpty(mesh)) return null;
        var rel = GamePath.Normalize(mesh);
        return rel.StartsWith("meshes\\", StringComparison.Ordinal) ? rel["meshes\\".Length..] : rel;
    }

    /// <summary>Waterfall effect meshes, windmill fans, rotors and water wheels in the supported worldspaces.</summary>
    public static IReadOnlyList<Pick> Select(IReadOnlyList<DynamicLodReference> dynamic)
    {
        var picks = new List<Pick>();
        foreach (var d in dynamic)
        {
            if (!d.IsGridObject || d.Glow || !Worlds.Contains(d.Ref.Worldspace)) continue;
            if (ModelOf(d) is not { } model) continue;
            var lower = model.ToLowerInvariant();
            bool waterfall = lower.Contains("waterfall");
            bool animated = lower.Contains("windmill") || lower.Contains("waterwheel") || lower.Contains("rotor");
            if (!waterfall && !animated) continue;
            // Effect LOD meshes (dyndolod\lod\...) are plain STAT in DynDOLOD; a full model that animates through sequences is MSTT.
            bool movable = animated || !lower.StartsWith("dyndolod\\lod\\", StringComparison.Ordinal);
            picks.Add(new Pick(d, model, movable));
        }
        return picks;
    }
}
