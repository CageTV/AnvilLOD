namespace AnvilLOD.Core.World;

/// <summary>
/// Our own archive (.bsa) priority order for a plain Data folder, used when Mutagen's ordering throws (its archive-name
/// comparer is unimplemented for some names, which is how a 3,600-plugin Steam install failed on its first step).
/// The engine loads the base game's archives first, then every archive whose name starts with a plugin's name, in the
/// order of the load order; later archives win. Archives that belong to no plugin sort among the base game's, by name.
/// </summary>
public static class ArchiveOrder
{
    /// <param name="archiveFiles">Full paths of the .bsa files in the Data folder.</param>
    /// <param name="pluginOrder">Plugin file names in load order (lowest priority first).</param>
    /// <returns>The archive paths, lowest priority first.</returns>
    public static List<string> LowToHigh(IEnumerable<string> archiveFiles, IReadOnlyList<string> pluginOrder)
    {
        // Longest plugin base name first, so "Skyrim - Textures" can't be taken by a plugin called "Skyrim".
        var bases = pluginOrder
            .Select((name, index) => (Base: Path.GetFileNameWithoutExtension(name), Index: index))
            .Where(p => p.Base.Length > 0)
            .OrderByDescending(p => p.Base.Length)
            .ToList();

        int Rank(string archivePath)
        {
            var file = Path.GetFileNameWithoutExtension(archivePath);
            foreach (var (b, index) in bases)
                if (file.StartsWith(b, StringComparison.OrdinalIgnoreCase)
                    && (file.Length == b.Length || !char.IsLetterOrDigit(file[b.Length])))
                    return index + 1;
            return 0;   // no plugin: with the base game
        }

        return archiveFiles
            .Select(p => (Path: p, Rank: Rank(p)))
            .OrderBy(t => t.Rank)
            .ThenBy(t => Path.GetFileName(t.Path), StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Path)
            .ToList();
    }
}
