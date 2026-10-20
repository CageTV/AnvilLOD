namespace AnvilLOD.Core.Lod;

/// <summary>
/// The kinds of thing an object LOD block is made of. Everything except plain objects is written into its own shape
/// whose parent node carries a name from <see cref="NodeName"/>, so the AnvilLOD SKSE plugin can find it in the
/// loaded block and hide or show it in game (quality settings). Plain objects keep the unnamed node LODGen writes.
/// </summary>
public static class LodLayers
{
    public const string Objects = "objects";
    public const string Grass = "grass";
    public const string Tree3D = "tree3d";
    public const string TreeCard = "treecard";

    public static readonly IReadOnlyList<string> All = [Objects, Grass, Tree3D, TreeCard];

    /// <summary>The prefix of the node names the SKSE plugin looks for. Keep in sync with skse/src/LodLayers.h.</summary>
    public const string NodePrefix = "AL:";

    /// <summary>
    /// How many density tiers the grass of a block is written in. Each tier is every 4th tuft along a space-filling curve, so any first n of
    /// them is an even n/4 of the grass, and the SKSE plugin (<c>iGrassTiers</c>) can show 25, 50, 75 or 100% in game. Keep in sync with
    /// skse/src/LodLayers.h (<c>kGrassTiers</c>); the first tier keeps the plain node name, so older plugin builds still work.
    /// </summary>
    public const int GrassTiers = 4;

    /// <summary>
    /// The name of the node of a layer in a block of the given LOD level (4, 8, 16 or 32), "AL:grass:4", or null for
    /// plain objects (no name, as before). A tier above 0 adds it as a last part, "AL:grass:4:2".
    /// </summary>
    public static string? NodeName(string layer, int level, int tier = 0)
        => layer == Objects ? null : tier > 0 ? $"{NodePrefix}{layer}:{level}:{tier}" : $"{NodePrefix}{layer}:{level}";

    public static string Display(string layer) => layer switch
    {
        Grass => "grass",
        Tree3D => "3D trees",
        TreeCard => "tree cards",
        _ => "objects",
    };
}
