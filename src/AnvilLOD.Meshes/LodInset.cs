using System.Numerics;

namespace AnvilLOD.Meshes;

/// <summary>
/// Pulls LOD geometry a few units inside its own surface, so that where the LOD and the full model are both drawn
/// (the ring where the full model has loaded and the LOD is still visible) the full model is always the nearer
/// surface. Glacier replacers ship LOD meshes that are decimated copies of the full model, so the two surfaces
/// cross each other and z-fight; light ice and snow make that very visible.
/// </summary>
public static class LodInset
{
    /// <summary>Default inset in game units for glacier and ice LOD.</summary>
    public const float DefaultIce = 6f;

    /// <summary>Default inset in game units for ship LOD built from the real models: the simplified deck must never poke through the real, bumpier one.</summary>
    public const float DefaultShip = 3f;

    /// <summary>Only the levels that overlap loaded cells need it; LOD16/32 are never drawn over a full model.</summary>
    public const int MaxLevel = 8;

    /// <summary>Faces looking down (buried in the ground or underneath) stay where they are: pulling them up opens a gap to the terrain.</summary>
    private const float DownFacing = -0.3f;

    /// <summary>A generic object's shape whose height is under this many times the inset is treated as flat and left alone.</summary>
    public const float FlatExtentFactor = 4f;

    /// <summary>True when a mesh path gets the generic object inset: not ice, glacier, mountain or ship LOD, which have their own.</summary>
    public static bool IsGenericObject(string meshPath)
        => For(meshPath, 1f, 1f, 1f, 0f) == 0f;

    /// <summary>
    /// True for a flat piece (a road, a floor slab, a plank on the ground): it has nothing to pull inside, and moving it down would sink it
    /// under the terrain. Only the generic object inset skips these.
    /// <paramref name="positions"/> are in the model's own space and <paramref name="scale"/> is the reference's scale: a road chunk laid
    /// on a hillside is still flat, but its world height range grows with the slope (2 degrees over a 900-unit chunk is already more than
    /// 4 x 8 units), so a world-space test pulled most roads on sloped ground under the terrain.
    /// </summary>
    public static bool IsFlat(Vector3[] positions, float distance, float scale = 1f)
    {
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in positions) { minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z); }
        return (maxZ - minZ) * scale < FlatExtentFactor * distance;
    }

    /// <summary>Share of a ground sheet's area that must face up (within 60 degrees of level).</summary>
    public const float GroundSheetUpShare = 0.95f;

    /// <summary>
    /// How evenly a ground sheet must face one way: the length of its area-weighted normal sum over its area. Measured on real LOD
    /// meshes: TMD Dirt Roads' road parts and ramps 0.965 and up, vanilla road chunks 0.998 and up, rock piles mostly 0.83-0.90.
    /// </summary>
    public const float GroundSheetCoherence = 0.96f;

    /// <summary>
    /// True for a piece lying on the ground that is too tall for <see cref="IsFlat"/>: a road ramp climbing a hill, a road's mound, a
    /// city's ground slab. Its surface faces up, evenly (unlike a rock pile, which faces every way), and it starts at the model's ground
    /// level (unlike a roof). Pulling such a piece down sinks it under the terrain just like a flat one. Positions are in model space.
    /// </summary>
    public static bool IsGroundSheet(Vector3[] positions, ushort[] triangles, float distance, float scale = 1f)
    {
        float minZ = float.MaxValue;
        foreach (var p in positions) minZ = MathF.Min(minZ, p.Z);
        if (minZ * scale > FlatExtentFactor * distance) return false;

        float total = 0f, up = 0f;
        var sum = Vector3.Zero;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            var a = positions[triangles[t]];
            var n = Vector3.Cross(positions[triangles[t + 1]] - a, positions[triangles[t + 2]] - a);   // length = twice the area
            float area = n.Length();
            if (area <= 0f) continue;
            total += area;
            sum += n;
            if (n.Z > 0.5f * area) up += area;
        }
        return total > 0f && up >= GroundSheetUpShare * total && sum.Length() >= GroundSheetCoherence * total;
    }

    /// <summary>The inset in units for a mesh path (0 = none): <paramref name="ice"/> for glacier/ice LOD, <paramref name="mountains"/> for mountain LOD.</summary>
    public static float For(string meshPath, float ice, float mountains, float ships = 0f, float objects = 0f)
    {
        if (ice <= 0f && mountains <= 0f && ships <= 0f && objects <= 0f) return 0f;
        if (ships > 0f && meshPath.StartsWith(AnvilLOD.Core.Lod.ShipLodModels.Prefix, StringComparison.Ordinal)) return ships;
        var p = meshPath.Replace('\\', '/').ToLowerInvariant();
        if (ice > 0f && (p.Contains("/lod/ice/") || p.Contains("/lod/glaciers/") || p.Contains("/lod/icecastle/") || p.StartsWith("lod/ice/", StringComparison.Ordinal)))
            return ice;
        if (mountains > 0f && (p.Contains("/lod/mountains/") || p.StartsWith("lod/mountains/", StringComparison.Ordinal)))
            return mountains;
        // Every other object: the same few units, so the ring where the loaded full model and the LOD are both drawn shows the full model.
        return objects > 0f ? objects : 0f;
    }

    /// <summary>
    /// Moves each vertex <paramref name="distance"/> units against its outward normal. Normals are averaged over every
    /// vertex that shares a position, so the hard edges of a LOD mesh (split vertices) move together and no crack opens.
    /// Both arrays are in the same space (world), <paramref name="normals"/> being unit outward normals.
    /// </summary>
    public static void Apply(Vector3[] positions, Vector3[] normals, float distance)
    {
        if (distance <= 0f || positions.Length == 0) return;

        var sums = new Dictionary<(int, int, int), Vector3>(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            var key = Key(positions[i]);
            sums[key] = sums.GetValueOrDefault(key) + normals[i];
        }

        var delta = new Vector3[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var n = sums[Key(positions[i])];
            float len = n.Length();
            if (len < 1e-4f) continue; // opposite faces cancel (a thin sheet): nothing to move along
            n /= len;
            if (n.Z < DownFacing) continue;
            delta[i] = -n * distance;
        }
        for (int i = 0; i < positions.Length; i++) positions[i] += delta[i];
    }

    // Positions that differ by less than a hundredth of a unit are the same point.
    private static (int, int, int) Key(Vector3 p) => ((int)MathF.Round(p.X * 100f), (int)MathF.Round(p.Y * 100f), (int)MathF.Round(p.Z * 100f));
}
