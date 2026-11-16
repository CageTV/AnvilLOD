using System.Numerics;
using AnvilLOD.Core.Pipeline;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AnvilLOD.Plugins;

/// <summary>
/// Gives static records that have empty object bounds (0,0,0 to 0,0,0) the bounds of their model, as overrides inside Occlusion.esp.
/// Why: with occlusion data in the cells the engine culls an object by its base record's bounds, and a record with none is a point at the
/// ground, below the cell's max-height surface, so the object is never shown. Hand-made records and Base Object Swapper replacements
/// are the usual ones (Unique Farmhouse Architecture's 96 farmhouse records: whole houses missing at Falkreath and Pinewatch, doors and
/// fences fine, back as soon as the records had bounds; 2026-11-16). The base game's and Creation Club's own records are left alone:
/// they work as shipped.
/// </summary>
public static class BoundsFixStage
{
    /// <summary>A model smaller than this on every axis is left alone (markers, tiny clutter).</summary>
    public const float MinExtent = 24f;

    private static readonly HashSet<string> BaseGame = new(StringComparer.OrdinalIgnoreCase)
        { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

    public static bool IsBaseGameOrCreationClub(ModKey key) =>
        BaseGame.Contains(key.FileName.String) || key.FileName.String.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    public sealed record Result(int Checked, int Fixed, int NoModel, int TooSmall);

    public static Result Run(GameContext game, IAssetSource assets, SkyrimMod mod, IProgress<string>? progress, CancellationToken ct)
    {
        var cache = new Dictionary<string, (Vector3 Min, Vector3 Max)?>(StringComparer.OrdinalIgnoreCase);
        int total = 0, fixedCount = 0, noModel = 0, tooSmall = 0;
        foreach (var ctx in game.LoadOrder.PriorityOrder.Static().WinningContextOverrides())
        {
            ct.ThrowIfCancellationRequested();
            var rec = ctx.Record;
            if (!IsEmpty(rec.ObjectBounds) || IsBaseGameOrCreationClub(rec.FormKey.ModKey)) continue;
            total++;
            var file = rec.Model?.File;
            if (string.IsNullOrWhiteSpace(file)) { noModel++; continue; }
            var b = ModelBounds(assets, file, cache);
            if (b is null) { noModel++; continue; }
            var (min, max) = b.Value;
            if (max.X - min.X < MinExtent && max.Y - min.Y < MinExtent && max.Z - min.Z < MinExtent) { tooSmall++; continue; }
            var s = ctx.GetOrAddAsOverride(mod);
            s.ObjectBounds.First = ToShort(min, floor: true);
            s.ObjectBounds.Second = ToShort(max, floor: false);
            fixedCount++;
        }
        progress?.Report($"Occlusion: gave {fixedCount:N0} of {total:N0} static records with empty bounds their model's bounds ({noModel:N0} had no readable model, {tooSmall:N0} are tiny)");
        return new Result(total, fixedCount, noModel, tooSmall);
    }

    private static bool IsEmpty(IObjectBoundsGetter b) =>
        b.First.X == 0 && b.First.Y == 0 && b.First.Z == 0 && b.Second.X == 0 && b.Second.Y == 0 && b.Second.Z == 0;

    private static P3Int16 ToShort(Vector3 v, bool floor)
    {
        static short C(float f) => (short)Math.Clamp((int)f, short.MinValue, short.MaxValue);
        return floor
            ? new P3Int16(C(MathF.Floor(v.X)), C(MathF.Floor(v.Y)), C(MathF.Floor(v.Z)))
            : new P3Int16(C(MathF.Ceiling(v.X)), C(MathF.Ceiling(v.Y)), C(MathF.Ceiling(v.Z)));
    }

    /// <summary>The box around every vertex of a model, in model space. Null when the model is missing, unreadable or has no geometry.</summary>
    private static (Vector3 Min, Vector3 Max)? ModelBounds(IAssetSource assets, string path, Dictionary<string, (Vector3 Min, Vector3 Max)?> cache)
    {
        if (cache.TryGetValue(path, out var known)) return known;
        (Vector3 Min, Vector3 Max)? result = null;
        try
        {
            var rel = path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? path : "meshes\\" + path;
            if (assets.TryOpen(rel, out var stream))
            {
                byte[] bytes;
                using (stream) { using var ms = new MemoryStream(); stream.CopyTo(ms); bytes = ms.ToArray(); }
                var mesh = AnvilLOD.Meshes.Nif.NifGeometryReader.Read(rel, bytes);
                var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
                bool any = false;
                foreach (var part in mesh.Parts)
                    foreach (var p in part.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true; }
                if (any) result = (min, max);
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException or IOException) { }
        cache[path] = result;
        return result;
    }
}
