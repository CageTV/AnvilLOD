using AnvilLOD.Plugins.Mo2;

namespace AnvilLOD.Plugins;

/// <summary>One thing to fix before generating. <see cref="Code"/> is stable (for tests and scripts), <see cref="Message"/> is for the user.</summary>
public sealed record PreflightIssue(string Code, string Message);

/// <summary>
/// Cheap checks that run before a Generate, from the MO2 profile alone (no plugins are opened): the mistakes that cost a test cycle.
/// <list type="bullet">
/// <item>The output mod is ticked in MO2. Its AnvilLOD plugins from the last run are then part of the load order the new run reads, and
/// the files the run writes into it are what the game loads on top of them (stale or doubled files: huge dark slabs in the sky).</item>
/// <item>DynDOLOD's output is enabled: its objects stand on the same places as AnvilLOD's.</item>
/// </list>
/// </summary>
public static class Preflight
{
    public const string OutputModEnabled = "output-mod-enabled";
    public const string DynDolodEnabled = "dyndolod-enabled";

    /// <summary>Issues for a game source and output folder. Anything it cannot read (not MO2, no profile, no folder) gives no issues.</summary>
    public static IReadOnlyList<PreflightIssue> Check(GameContextOptions game, string? outputFolder)
    {
        if (game.Mode != GameSourceMode.Mo2Instance || string.IsNullOrWhiteSpace(game.Mo2InstanceFolder)) return [];
        try
        {
            var instance = Mo2Instance.Open(game.Mo2InstanceFolder.Trim(), game.Mo2Locations);
            var profile = instance.OpenProfile(string.IsNullOrWhiteSpace(game.Mo2Profile) ? null : game.Mo2Profile.Trim());
            return Check(profile.EnabledModsLowToHigh, profile.EnabledPlugins, instance.ModsFolder, outputFolder);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            return [];
        }
    }

    public static IReadOnlyList<PreflightIssue> Check(IReadOnlyList<string> enabledMods, IReadOnlyList<string> enabledPlugins, string modsFolder, string? outputFolder)
    {
        var issues = new List<PreflightIssue>();

        if (!string.IsNullOrWhiteSpace(outputFolder) && OutputModName(modsFolder, outputFolder) is { } mod
            && enabledMods.Contains(mod, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new PreflightIssue(OutputModEnabled,
                $"The output mod \"{mod}\" is ticked in MO2. Untick it (the checkbox in the left pane) before you generate: a ticked output mod puts the AnvilLOD plugins " +
                "and files of the last run into the load order this run reads, and the new files are written over what the game still loads from it. " +
                "That shows up in game as stale or doubled LOD, such as huge dark slabs in the sky. Tick it again when the run is done."));
        }

        var dyn = enabledPlugins.Where(p => p.StartsWith("DynDOLOD", StringComparison.OrdinalIgnoreCase)
                                            && (p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))).ToList();
        if (dyn.Count > 0)
        {
            issues.Add(new PreflightIssue(DynDolodEnabled,
                $"{string.Join(", ", dyn)} {(dyn.Count == 1 ? "is" : "are")} enabled. AnvilLOD replaces DynDOLOD: its output doubles the dynamic objects and its LOD meshes get " +
                "picked up. Disable DynDOLOD's output mod and plugins before you generate."));
        }

        return issues;
    }

    /// <summary>The name of the MO2 mod the output folder is (a direct child of the mods folder), or null if it is somewhere else.</summary>
    public static string? OutputModName(string modsFolder, string outputFolder)
    {
        var mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsFolder.Trim()));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder.Trim()));
        var parent = Path.GetDirectoryName(output);
        if (parent is null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), mods, StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetFileName(output);
    }
}
