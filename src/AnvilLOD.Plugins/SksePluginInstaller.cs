using System.Diagnostics;

namespace AnvilLOD.Plugins;

/// <summary>Which AnvilLOD SKSE plugin build Generate puts into the output folder.</summary>
public enum SkseDllChoice
{
    /// <summary>Read the game's SkyrimSE.exe (or SkyrimVR.exe) version and pick the matching build.</summary>
    Auto,
    /// <summary>SE 1.5.97 up to AE 1.6.1170.</summary>
    UpTo1170,
    /// <summary>Newer than 1.6.1170 (1.7.x).</summary>
    Newer,
    /// <summary>Don't put the DLL in the output (it's installed separately, e.g. from the FOMOD).</summary>
    None,
    /// <summary>Skyrim VR 1.4.15. Explicit value: settings files store the numbers above.</summary>
    Vr = 4,
}

/// <summary>
/// Copies the right AnvilLOD SKSE plugin build into <c>output\SKSE\Plugins\AnvilLOD.dll</c>, so the LOD output is a
/// single install. The tool ships all three builds next to itself:
/// <c>SKSE\1.5.97-1.6.1170\AnvilLOD.dll</c>, <c>SKSE\1.7.x\AnvilLOD.dll</c> and <c>SKSE\1.4.15-VR\AnvilLOD.dll</c>.
/// </summary>
public static class SksePluginInstaller
{
    public const string FolderUpTo1170 = "1.5.97-1.6.1170";
    public const string FolderNewer = "1.7.x";
    public const string FolderVr = "1.4.15-VR";
    /// <summary>Every Skyrim SE/AE runtime is 1.5 or newer; Skyrim VR is 1.4.15.</summary>
    public static readonly Version FirstSpecialEdition = new(1, 5, 0, 0);
    public static readonly Version LastOld = new(1, 6, 1170, 0);
    public const string RelativeDll = "SKSE\\Plugins\\AnvilLOD.dll";

    public sealed record Result(bool Installed, string Message, Version? GameVersion = null, SkseDllChoice Used = SkseDllChoice.None);

    /// <summary>File version of SkyrimSE.exe (or SkyrimVR.exe) next to the game's Data folder, or null if it can't be read.</summary>
    public static Version? GameVersion(string gameDataFolder)
    {
        var gameFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(gameDataFolder));
        if (gameFolder is null) return null;
        var exe = Path.Combine(gameFolder, "SkyrimSE.exe");
        if (!File.Exists(exe)) exe = Path.Combine(gameFolder, "SkyrimVR.exe");
        if (!File.Exists(exe)) return null;
        var info = FileVersionInfo.GetVersionInfo(exe);
        if (info.FileMajorPart == 0 && info.FileMinorPart == 0) return null;
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    public static SkseDllChoice ForVersion(Version v) =>
        v < FirstSpecialEdition ? SkseDllChoice.Vr : v <= LastOld ? SkseDllChoice.UpTo1170 : SkseDllChoice.Newer;

    public static string FolderFor(SkseDllChoice c) => c switch
    {
        SkseDllChoice.Newer => FolderNewer,
        SkseDllChoice.Vr => FolderVr,
        _ => FolderUpTo1170,
    };

    public static string Describe(SkseDllChoice c) => c switch
    {
        SkseDllChoice.UpTo1170 => "SE 1.5.97 - AE 1.6.1170",
        SkseDllChoice.Newer => "newer than 1.6.1170 (1.7.x)",
        SkseDllChoice.Vr => "Skyrim VR 1.4.15",
        SkseDllChoice.None => "not installed by AnvilLOD",
        _ => "auto-detect",
    };

    /// <param name="toolFolder">Where the bundled builds live (default: next to the running tool).</param>
    public static Result Install(string outputFolder, string gameDataFolder, SkseDllChoice choice, string? toolFolder = null)
    {
        var target = Path.Combine(outputFolder, RelativeDll.Replace('\\', Path.DirectorySeparatorChar));
        if (choice == SkseDllChoice.None)
        {
            // Don't leave an old copy behind that would fight the separately installed one.
            if (File.Exists(target)) File.Delete(target);
            return new Result(false, "SKSE plugin: not put in the output (install it separately, from the AnvilLOD SKSE Plugin FOMOD).");
        }

        Version? version = GameVersion(gameDataFolder);
        var use = choice;
        if (choice == SkseDllChoice.Auto)
        {
            if (version is null)
                return new Result(false, "SKSE plugin: couldn't read SkyrimSE.exe's or SkyrimVR.exe's version next to " + gameDataFolder
                    + ", so no DLL was put in the output. Pick the game version in the SKSE plugin option, or install it from the FOMOD.");
            use = ForVersion(version);
        }

        var root = toolFolder ?? AppContext.BaseDirectory;
        var source = Path.Combine(root, "SKSE", FolderFor(use), "AnvilLOD.dll");
        if (!File.Exists(source))
            return new Result(false, $"SKSE plugin: {source} is missing, so no DLL was put in the output (rebuild the tool with build.ps1, which bundles all the builds).", version, use);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);

        var warn = version is not null && ForVersion(version) != use
            ? $" WARNING: the game is {version}, which needs the {Describe(ForVersion(version))} build; SKSE will refuse this one."
            : "";
        return new Result(true, $"SKSE plugin: {Describe(use)} build installed to {RelativeDll}"
            + (version is not null ? $" (game {version})" : "") + "." + warn, version, use);
    }
}
