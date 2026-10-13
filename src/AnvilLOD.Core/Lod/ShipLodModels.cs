using System.Text.RegularExpressions;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// Which full models are whole ships (hull, deck and rigging in one mesh) that object LOD should draw from the real model
/// instead of the very coarse hand-made LOD meshes: Skyrim's moored boats, from the rowboat to the Dainty Sload, and the ships
/// that ship replacers add. Interior pieces (the inside of a ship, doors, wheels, beams) are not ships.
/// </summary>
public static class ShipLodModels
{
    // Ship models live under meshes\dungeons\ship\ in the base game and under meshes\bstamriel\dungeons\ships\<set>\ in Ships and
    // Boats of Tamriel and the mods built on it (Red Wave Corsairs, DK's Realistic Nord Ships, ...). The file name starts with ship or boat.
    private static readonly Regex Ship = new(@"^meshes\\(bstamriel\\)?dungeons\\ships?\\([a-z0-9_ -]+\\)*(ship|boat)[a-z0-9_ -]*\.nif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PgPatcherDups = new(@"^meshes\\_pgpatcher_dups\\\d+\\",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Words in a file name that make it a piece of a ship or its interior, not a whole ship.</summary>
    private static readonly string[] NotWhole =
    [
        "inside", "interior", "door", "clutter", "grate", "window", "wall", "mast", "anchor", "wheel", "beam", "floor", "plank",
        "ladder", "throne", "trapdoor", "chain", "rope", "flag", "cannon", "crate", "barrel", "hatch", "stairs", "cabin", "lantern",
    ];

    /// <summary>A whole ship must be at least this long (game units); anything smaller that matched by name is a prop.</summary>
    public const float MinLength = 600f;

    /// <summary>Start of the mesh path that stands for a ship made from its model: <c>~anvillod-ship|level|model|stock mesh</c>.</summary>
    public const string Prefix = "~anvillod-ship|";

    /// <summary>The mesh path for a ship model at a LOD level (0 = LOD4, 1 = LOD8), remembering the stock LOD mesh it replaces.</summary>
    public static string MeshPath(string model, int level, string stock) => Prefix + level + "|" + model + "|" + stock;

    /// <summary>The model a ship mesh path was made from, or null if the path isn't one.</summary>
    public static string? ModelOf(string meshPath) => Parts(meshPath)?.Model;

    /// <summary>The stock LOD mesh a ship mesh path replaced, or the path itself for any other path. Things that hand the path to the game (the SKSE plugin's data) use this.</summary>
    public static string StockMesh(string meshPath) => Parts(meshPath)?.Stock ?? meshPath;

    private static (int Level, string Model, string Stock)? Parts(string meshPath)
    {
        if (meshPath is null || !meshPath.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = meshPath[Prefix.Length..].Split('|', 3);
        return rest.Length == 3 && int.TryParse(rest[0], out var level) ? (level, rest[1], rest[2]) : null;
    }

    public static int LevelOf(string meshPath) => Parts(meshPath)?.Level ?? 0;

    // Animated Ships (Vicn) puts wrecks that only bob up and down where the base game has static ones: meshes\clutter\vicn\animatedship\updown\.
    private static readonly Regex BobbingWreck = new(@"^meshes\\clutter\\vicn\\animatedship\\updown\\(ship|boat)[a-z0-9_ -]*wreck[a-z0-9_ -]*\.nif$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>One of Animated Ships' wrecks that move in place only (a static LOD copy is a fair stand-in for them).</summary>
    public static bool IsBobbingWreck(string modelPath)
        => BobbingWreck.IsMatch(PgPatcherDups.Replace(LodMeshIndex.Normalize(modelPath), "meshes\\"));

    public static bool IsShip(string modelPath)
    {
        // PGPatcher points base records at copies of a model under meshes\_pgpatcher_dups\<n>\: match by the original path.
        var p = PgPatcherDups.Replace(LodMeshIndex.Normalize(modelPath), "meshes\\");
        if (!Ship.IsMatch(p)) return false;
        var name = p[(p.LastIndexOf((char)92) + 1)..];
        foreach (var w in NotWhole) if (name.Contains(w, StringComparison.Ordinal)) return false;
        return true;
    }
}
