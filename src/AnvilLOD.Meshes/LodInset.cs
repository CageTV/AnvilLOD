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

    /// <summary>The inset in units for a mesh path (0 = none): <paramref name="ice"/> for glacier/ice LOD, <paramref name="mountains"/> for mountain LOD.</summary>
    public static float For(string meshPath, float ice, float mountains, float ships = 0f)
    {
        if (ice <= 0f && mountains <= 0f && ships <= 0f) return 0f;
        if (ships > 0f && meshPath.StartsWith(AnvilLOD.Core.Lod.ShipLodModels.Prefix, StringComparison.Ordinal)) return ships;
        var p = meshPath.Replace('\\', '/').ToLowerInvariant();
        if (ice > 0f && (p.Contains("/lod/ice/") || p.Contains("/lod/glaciers/") || p.Contains("/lod/icecastle/") || p.StartsWith("lod/ice/", StringComparison.Ordinal)))
            return ice;
        if (mountains > 0f && (p.Contains("/lod/mountains/") || p.StartsWith("lod/mountains/", StringComparison.Ordinal)))
            return mountains;
        return 0f;
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
