using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes a plugin through Mutagen's builder with <c>WithAutoSplit()</c>: a load order whose records would need more masters
/// than one file may list is written as <c>Name.esp</c>, <c>Name_1.esp</c>, <c>Name_2.esp</c> ... with the records spread over
/// them, each file carrying only the masters of the records it holds, instead of the write failing the whole run.
/// A load order has 254 slots (form IDs 0x00-0xFD) plus 0xFE for every light plugin. An ESL-flagged plugin keeps its own records
/// at 0xFE, so it may master all 254; a full plugin takes one of the 254 and may list only 253. Mutagen refuses a 255th master.
/// Occlusion.esp is the output that gets near it: it overrides a CELL for every exterior cell it has data for, and those cells'
/// winning plugins are its masters.
/// </summary>
public static class SplitPluginWriter
{
    /// <summary>The most masters a plugin can list: 0x00-0xFD. Only reachable by an ESL-flagged plugin (its own records are at
    /// 0xFE); a full plugin has 253, and Mutagen refuses a 255th master.</summary>
    public const int MaxMasters = 254;

    /// <summary>
    /// Writes <paramref name="mod"/> to <paramref name="path"/> (and, if it does not fit, to <c>Name_1.esp</c>, ... next to it).
    /// The split files of an earlier run that needed more of them are deleted afterwards: left in the folder they would load old
    /// data on top of the new. Only files that were there before this write, and that this write did not touch, are deleted, and
    /// only once it has succeeded, so a failed run does not take the previous run's output with it.
    /// </summary>
    /// <returns>Every file written, the plugin itself first.</returns>
    public static IReadOnlyList<string> Write(SkyrimMod mod, string path, IEnumerable<ModKey> loadOrder)
    {
        var folder = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(path);
        // The split files of an earlier run. One this write rewrites gets a fresh time stamp, so an old stamp means it was left
        // behind; the two-second slack covers file system time stamp granularity.
        var started = DateTime.UtcNow.AddSeconds(-2);
        var older = Fragments(folder, name).Where(f => File.GetLastWriteTimeUtc(f) < started).ToList();
        mod.BeginWrite
            .ToPath(path)
            .WithLoadOrder(loadOrder)
            .WithNoDataFolder()
            .NoNextFormIDProcessing()   // keep the caller's header (Occlusion.esp starts at 0x800): every record is an override, so nothing is allocated
            .WithAutoSplit()
            .Write();
        foreach (var stale in older) if (File.Exists(stale)) File.Delete(stale);
        return [path, .. Fragments(folder, name)];
    }

    /// <summary>The split files of <paramref name="fileName"/> that are in <paramref name="folder"/> now: <c>Name_1.esp</c>,
    /// <c>Name_2.esp</c>, ... in the order Mutagen numbers them (not the order the names sort in, which puts _10 before _2).</summary>
    public static IEnumerable<string> Fragments(string folder, string fileName)
    {
        if (!Directory.Exists(folder)) return [];
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return Directory.GetFiles(folder, stem + "_*" + ext)
            .Select(f => (File: f, Index: Number(f, stem)))
            .Where(x => x.Index > 0)
            .OrderBy(x => x.Index)
            .Select(x => x.File)
            .ToList();
    }

    /// <summary>The N of <c>Name_N.ext</c>, or 0 when the name is not one of the split files.</summary>
    private static int Number(string file, string stem)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.Length > stem.Length + 1
            && name.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name[(stem.Length + 1)..], out var n)
                ? n
                : 0;
    }
}
