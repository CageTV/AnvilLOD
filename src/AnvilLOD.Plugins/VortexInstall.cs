namespace AnvilLOD.Plugins;

/// <summary>
/// Vortex: mods are deployed into the game's real Data folder (hard links) and the load order is the normal
/// plugins.txt, so the game is read like a plain install. The output goes into Vortex's staging folder as its own mod.
/// </summary>
public static class VortexInstall
{
    public const string OutputModName = "AnvilLOD Output";

    /// <summary>Default staging folder for Skyrim SE (%APPDATA%\Vortex\skyrimse\mods), if it exists.</summary>
    public static string? StagingFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        var path = Path.Combine(appData, "Vortex", "skyrimse", "mods");
        return Directory.Exists(path) ? path : null;
    }

    public static bool IsInstalled()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !string.IsNullOrEmpty(appData) && Directory.Exists(Path.Combine(appData, "Vortex"));
    }
}
