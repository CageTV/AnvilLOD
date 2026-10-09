namespace AnvilLOD.Meshes.Authoring;

/// <summary>How the triangle budget of a generated LOD mesh is chosen.</summary>
public enum BudgetMode
{
    /// <summary>No budget: the detail setting alone decides.</summary>
    Off,
    /// <summary>The recommended budget for the model's size (<see cref="LodBudgets.Recommended"/>).</summary>
    Recommended,
    /// <summary>Fixed maximums per level that the user typed (0 = no limit for that level).</summary>
    Custom,
}

/// <summary>
/// Triangle budgets for generated LOD meshes. The recommended values come from measuring the LOD meshes that SE/AE
/// lists already use (the DynDOLOD Resources SE and LOD Model Library sets, 2,554 meshes): they are lean, with a median of
/// about 80-120 triangles per level and a 90th percentile of about 430-540, and only big buildings go higher. The
/// recommended budget is that 90th percentile for the model's size class, rounded and kept decreasing with distance,
/// so a generated mesh stays within what a hand-made one of the same size would be.
/// </summary>
public static class LodBudgets
{
    /// <summary>Largest model dimension (game units) at which each size class ends.</summary>
    private static readonly (float Below, int Lod0, int Lod1, int Lod2)[] Classes =
    [
        (1000f, 250, 200, 150),     // props: barrels, signs, small ruins pieces
        (3000f, 600, 450, 300),     // houses, towers, big rocks
        (8000f, 2500, 1500, 650),   // large buildings, forts, shrines
        (float.MaxValue, 1400, 900, 600), // landmarks (few samples, kept modest)
    ];

    public const string Basis = "90th percentile of the 2,554 LOD meshes in DynDOLOD Resources SE and LOD Model Library, by model size";

    /// <summary>The budget for one level of a model of this size under the chosen mode, or null for none.</summary>
    public static int? For(BudgetMode mode, IReadOnlyList<int>? custom, int level, float size) => mode switch
    {
        BudgetMode.Recommended => Recommended(level, size),
        BudgetMode.Custom when custom is not null && level >= 0 && level < custom.Count && custom[level] > 0 => custom[level],
        _ => null,
    };

    /// <summary>The recommended maximum triangles for a model of this size at LOD level 0, 1 or 2.</summary>
    public static int Recommended(int level, float size)
    {
        foreach (var c in Classes)
            if (size < c.Below)
                return level switch { 0 => c.Lod0, 1 => c.Lod1, _ => c.Lod2 };
        return Classes[^1].Lod2;
    }

    /// <summary>Text for the UI and report: the recommended budgets per size class.</summary>
    public static string Describe() =>
        string.Join("; ", Classes.Select((c, i) =>
        {
            var from = i == 0 ? 0f : Classes[i - 1].Below;
            var range = c.Below == float.MaxValue ? $"over {from:N0}" : $"{from:N0}-{c.Below:N0}";
            return $"{range} units: {c.Lod0} / {c.Lod1} / {c.Lod2}";
        }));
}
