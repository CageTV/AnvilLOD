using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes a plugin through Mutagen's builder with <c>WithAutoSplit()</c>: a load order whose records would need more masters
/// than one file may list is written as <c>Name.esp</c>, <c>Name_1.esp</c>, <c>Name_2.esp</c> ... with the records spread over
/// them, each file carrying only the masters of the records it holds, instead of the write failing the whole run.
/// The game gives a plugin 254 master slots (load order form IDs 0x00-0xFD; 0xFE is the light-plugin slot and 0xFF the save),
/// so a plugin that overrides a record from every plugin in a big list cannot exist on its own. Occlusion.esp is the one output
/// that does that: it overrides a CELL for every exterior cell it has data for, and those cells' winning plugins are its masters.
/// </summary>
public static class SplitPluginWriter
{
    /// <summary>The most masters a plugin can list: 0x00-0xFD, with 0xFE left for light plugins and 0xFF for the save.</summary>
    public const int MaxMasters = 254;

    /// <summary>
    /// Writes <paramref name="mod"/> to <paramref name="path"/> (and, if it does not fit, to <c>Name_1.esp</c>, ... next to it).
    /// Fragment files of an earlier run are removed first: when this run needs fewer plugins they would otherwise stay in the
    /// output folder and load the old data on top of the new.
    /// </summary>
    /// <returns>Every file written, the plugin itself first.</returns>
    public static IReadOnlyList<string> Write(SkyrimMod mod, string path, IEnumerable<ModKey> loadOrder)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        foreach (var old in Fragments(Path.GetDirectoryName(path) ?? ".", mod.ModKey)) File.Delete(old);
        mod.BeginWrite
            .ToPath(path)
            .WithLoadOrder(loadOrder)
            .WithNoDataFolder()
            .NoNextFormIDProcessing()   // keep the caller's header (Occlusion.esp starts at 0x800): every record is an override, so nothing is allocated
            .WithAutoSplit()
            .Write();
        return [path, .. Fragments(Path.GetDirectoryName(path) ?? ".", mod.ModKey)];
    }

    /// <summary>The split files of <paramref name="key"/> that are in <paramref name="folder"/> now: <c>Name_1.esp</c>, <c>Name_2.esp</c>, ...</summary>
    public static IEnumerable<string> Fragments(string folder, ModKey key)
    {
        if (!Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(key.FileName.String);
        var ext = Path.GetExtension(key.FileName.String);
        return Directory.GetFiles(folder, stem + "_*" + ext)
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name[(stem.Length + 1)..], out var n) && n > 0;
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
