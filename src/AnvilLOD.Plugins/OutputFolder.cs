namespace AnvilLOD.Plugins;

/// <summary>
/// The output folder of a Generate run: where it goes when the user didn't pick one, creating it, and clearing what an earlier
/// run left in it so old files never mix with the new ones.
/// <para>Clearing deletes things, so it only happens in a folder that is AnvilLOD's own: one that is empty or has AnvilLOD's
/// manifest or log in it. Anything else (a mod folder with other content, the game folder, MO2's mods folder, a drive root) is left
/// alone and the run says so.</para>
/// </summary>
public static class OutputFolder
{
    public const string DefaultName = "AnvilLOD Output";

    /// <summary>MO2 writes this into a mod folder (name, notes, version); it is not LOD and stays.</summary>
    private static readonly string[] Kept = ["meta.ini"];

    /// <summary>Files that only AnvilLOD writes: a folder holding one of them is its output.</summary>
    private static readonly string[] OwnMarkers = ["AnvilLOD.manifest.json", "AnvilLOD.log"];

    /// <summary>Names that show a folder is a game, mod manager or mods folder, never somewhere to empty.</summary>
    private static readonly string[] ProtectedMarkers =
        ["ModOrganizer.ini", "SkyrimSE.exe", "SkyrimVR.exe", "SkyrimSELauncher.exe", "Skyrim.esm", "modlist.txt", "plugins.txt", "loadorder.txt", "mods", "profiles", "Data"];

    public sealed record Result(bool Created, bool Cleared, int Deleted, string? Warning);

    /// <summary>
    /// The folder to use when none was chosen: <c>AnvilLOD Output</c> in the MO2 mods folder (it shows up as a new mod),
    /// in Vortex's staging folder, or in Documents.
    /// </summary>
    public static string DefaultFolder(GameContextOptions game)
    {
        if (game.Mode == GameSourceMode.Mo2Instance && !string.IsNullOrWhiteSpace(game.Mo2InstanceFolder))
        {
            var mods = !string.IsNullOrWhiteSpace(game.Mo2Locations?.ModsFolder) ? game.Mo2Locations!.ModsFolder!.Trim()
                : Mo2ModsFolder(game.Mo2InstanceFolder.Trim());
            return Path.Combine(mods, DefaultName);
        }
        if (game.Mode == GameSourceMode.Vortex && VortexInstall.StagingFolder() is { } staging)
            return Path.Combine(staging, VortexInstall.OutputModName);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DefaultName);
    }

    private static string Mo2ModsFolder(string instance)
    {
        try { return Mo2.Mo2Instance.Open(instance).ModsFolder; }
        catch (Exception)
        { return Path.Combine(instance, "mods"); }
    }

    /// <summary>
    /// Creates the folder if it doesn't exist and, if <paramref name="clear"/> is set, empties it when it is AnvilLOD's own.
    /// Never throws for a folder it won't clear: that comes back as <see cref="Result.Warning"/>.
    /// </summary>
    public static Result Prepare(string folder, bool clear)
    {
        var full = Path.GetFullPath(folder.Trim());
        bool created = false;
        if (!Directory.Exists(full))
        {
            Directory.CreateDirectory(full);
            created = true;
        }
        if (!clear || created) return new Result(created, false, 0, null); // a folder that was just made has nothing to clear

        if (WhyNotToClear(full) is { } why)
            return new Result(created, false, 0, $"Output folder not cleared: {why} Pick an empty folder (or use \"{DefaultName}\") to get a clean output on every run.");

        int deleted = 0;
        var failed = new List<string>();
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (Kept.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                if (entry is DirectoryInfo dir)
                {
                    deleted += dir.EnumerateFiles("*", SearchOption.AllDirectories).Count();
                    DeleteTree(dir);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(entry.Name + " (" + ex.Message + ")");
            }
        }
        return new Result(created, true, deleted, failed.Count == 0 ? null
            : $"Could not remove {string.Join(", ", failed)} from the output folder (is the game running?). Old files may remain.");
    }

    /// <summary>Why the folder must not be emptied, or null if it may be.</summary>
    public static string? WhyNotToClear(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(full) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
            return "it is a drive root.";
        if (!Directory.Exists(full)) return null;

        var names = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).ToList();
        if (names.FirstOrDefault(n => ProtectedMarkers.Contains(n, StringComparer.OrdinalIgnoreCase)) is { } marker)
            return $"it looks like a game, mod manager or mods folder (it contains \"{marker}\").";
        if (names.All(n => Kept.Contains(n, StringComparer.OrdinalIgnoreCase))) return null; // empty (or only MO2's meta.ini)
        if (OwnMarkers.Any(m => names.Contains(m, StringComparer.OrdinalIgnoreCase))) return null;
        return "it has files in it that AnvilLOD didn't write.";
    }

    private static void DeleteTree(DirectoryInfo dir)
    {
        // Read-only files (copied from elsewhere) would stop a plain recursive delete.
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
        dir.Delete(recursive: true);
    }
}
