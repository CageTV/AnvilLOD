using AnvilLOD.Core.Lod;
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

    /// <summary>Which kinds of grid object become real references. The first is the original set; the rest are the "effects" option.</summary>
    [Flags]
    public enum Kinds { None = 0, Waterworks = 1, Fire = 2, Candle = 4, Torch = 8, Lava = 16, Stream = 32, Ships = 64, Effects = Fire | Candle | Torch | Lava | Stream, All = Waterworks | Effects | Ships }

    /// <param name="Near">DynDOLOD's "Near LOD" grid column: the SKSE plugin only enables it within the near grid distance of the player (fires, candles, streams).</param>
    public sealed record Pick(DynamicLodReference Source, string Model, bool Movable, bool Near = false, bool Effect = false);

    /// <summary>DynDOLOD's own plugins and AnvilLOD's (a plugin from an earlier Generate that is still in the load order): never a source.</summary>
    private static bool IsDynDolod(string? plugin) =>
        plugin is not null && (plugin.StartsWith("DynDOLOD", StringComparison.OrdinalIgnoreCase)
                               || plugin.Equals("AnvilLOD.esp", StringComparison.OrdinalIgnoreCase) || plugin.Equals("AnvilLOD.esm", StringComparison.OrdinalIgnoreCase));

    /// <summary>The model the SKSE plugin would draw for this grid object, relative to Data\meshes, normalized.</summary>
    public static string? ModelOf(DynamicLodReference d)
    {
        var r = d.Ref;
        var mesh = r.Meshes.Lod4 ?? r.Meshes.Lod8 ?? r.Meshes.Lod16 ?? r.Meshes.Lod32;
        if (string.IsNullOrEmpty(mesh)) return null;
        var rel = GamePath.Normalize(AnvilLOD.Core.Lod.ShipLodModels.StockMesh(mesh));
        return rel.StartsWith("meshes\\", StringComparison.Ordinal) ? rel["meshes\\".Length..] : rel;
    }

    /// <summary>
    /// How far a LOD water plane is lowered below its placement, in game units (the SKSE plugin does the same for the planes it draws).
    /// A plane at exactly the height of the real water flickers where they overlap; just below it the real water covers it.
    /// </summary>
    public const float WaterDrop = 28f;

    /// <summary>One of Animated Ships' sailing ships (the "Distant" models), relative to Data\meshes, lower case.</summary>
    public static bool IsAnimatedShip(string lowerModel) => lowerModel.Contains("vicn\\animatedship\\distant\\", StringComparison.Ordinal);

    /// <summary>A shader-only water plane (stream, creek, pond LOD mesh).</summary>
    public static bool IsWaterPlane(string model)
    {
        var lower = model.ToLowerInvariant();
        return lower.Contains("\\water\\") || lower.Contains("creek");
    }

    /// <summary>The kind of an effect model, or None. Order matters: a candle fire is a candle, a lava stream is lava.</summary>
    public static Kinds KindOf(string lowerModel)
    {
        if (lowerModel.Contains("lava")) return Kinds.Lava;
        if (lowerModel.Contains("torch")) return Kinds.Torch;
        if (lowerModel.Contains("candle") || lowerModel.Contains("lantern")) return Kinds.Candle;
        if (lowerModel.Contains("fire") || lowerModel.Contains("brazier")) return Kinds.Fire;
        if (lowerModel.Contains("stream") || lowerModel.Contains("creek") || lowerModel.Contains("rapids")
            || lowerModel.Contains("dyndolod\\lod\\water\\")) return Kinds.Stream;
        return Kinds.None;
    }

    /// <summary>Waterfall effect meshes, windmill fans, rotors and water wheels (and, when asked, fires, candles, torches, lava and streams) in the supported worldspaces.</summary>
    /// <param name="waterStandIns">The lit fake-water stand-ins are on: water planes stay with the SKSE plugin, which swaps them.</param>
    /// <param name="otherWorlds">Further worldspaces to write them for: the ones with no parent worldspace (a child worldspace shares its cell
    /// coordinates with its parent, and a persistent full-LOD reference of it would be drawn in the parent too).</param>
    public static IReadOnlyList<Pick> Select(IReadOnlyList<DynamicLodReference> dynamic, Kinds kinds = Kinds.Waterworks, bool waterStandIns = false,
        IReadOnlySet<string>? otherWorlds = null)
    {
        var picks = new List<Pick>();
        foreach (var d in dynamic)
        {
            if (!d.IsGridObject || d.Glow || !(Worlds.Contains(d.Ref.Worldspace) || otherWorlds?.Contains(d.Ref.Worldspace) == true)) continue;
            // DynDOLOD's own copies (DynDOLOD.esp / .esm) of these references are not ours to copy again; only the vanilla ones are.
            if (IsDynDolod(d.RefPlugin) || IsDynDolod(d.Ref.WinningPlugin)) continue;
            if (ModelOf(d) is not { } model) continue;
            var lower = model.ToLowerInvariant();
            // Animated Ships' sailing ships: models that move themselves through a controller sequence, so a real (movable static) reference.
            if (IsAnimatedShip(lower))
            {
                if ((kinds & Kinds.Ships) == 0) continue;
                picks.Add(new Pick(d, model, true));
                continue;
            }
            bool waterfall = lower.Contains("waterfall");
            bool animated = lower.Contains("windmill") || lower.Contains("waterwheel") || lower.Contains("rotor");
            if (waterfall || animated)
            {
                if ((kinds & Kinds.Waterworks) == 0) continue;
                // Effect LOD meshes (dyndolod\lod\...) are plain STAT in DynDOLOD; a full model that animates through sequences is MSTT.
                bool staticOrMovable = animated || !lower.StartsWith("dyndolod\\lod\\", StringComparison.Ordinal);
                picks.Add(new Pick(d, model, staticOrMovable));
                continue;
            }
            var kind = KindOf(lower);
            if (kind == Kinds.None || (kinds & kind) == 0) continue;
            // Candles and lanterns are small lights nobody can see from a distance (a DynDOLOD list has about 580 of them): leaving them out keeps
            // the persistent references down, and the real ones are there inside the loaded cells anyway.
            if (kind == Kinds.Candle) continue;
            // A water plane (stream, creek, pond) is a shader-only STAT; the stand-in option replaces them in the plugin instead.
            bool plane = lower.Contains("\\water\\") || lower.Contains("creek");
            if (plane && waterStandIns) continue;
            // Fires, candles and torches animate through particle and controller sequences (MSTT, as in DynDOLOD). Lava and streams are
            // shader scrolls: STAT for a DynDOLOD LOD mesh, MSTT for a full vanilla model.
            bool movable = kind is Kinds.Fire or Kinds.Candle or Kinds.Torch || !lower.StartsWith("dyndolod\\lod\\", StringComparison.Ordinal);
            picks.Add(new Pick(d, model, movable, Near: d.Grid == DynamicGrid.Near, Effect: true));
        }
        return picks;
    }
}
