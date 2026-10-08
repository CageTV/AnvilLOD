using System.IO;
using System.Text.Json;

namespace AnvilLOD.App;

/// <summary>UI settings, saved to %AppData%\AnvilLOD\settings.json between sessions.</summary>
public sealed class AppSettings
{
    public bool UseMo2 { get; set; } = true;
    public bool UseVortex { get; set; }   // when UseMo2 is false: Vortex (true) or a plain Data folder (false)
    public string? Mo2Instance { get; set; }
    public string? Mo2Profile { get; set; }
    public string? DataFolder { get; set; }
    public string? PluginsTxt { get; set; }
    public string? OutputFolder { get; set; }
    public bool AllWorldspaces { get; set; }
    public string Worldspaces { get; set; } = "Tamriel";
    public bool Lod4 { get; set; } = true;
    public bool Lod8 { get; set; } = true;
    public bool Lod16 { get; set; } = true;
    public bool Lod32 { get; set; } = true;
    public string? DynDolodFolder { get; set; }
    public string Preset { get; set; } = "High";
    public bool RemoveBuried { get; set; } = true;
    public bool TreeLod { get; set; } = true;
    public bool GrassLod { get; set; } = true;
    public int TreeBrightness { get; set; } = 100;   // percent, 10-110
    public int ObjectBrightness { get; set; } = 100; // percent, 10-110
    public bool DynamicLod { get; set; } = true;
    public bool GridObjects { get; set; } = true;
    public bool Seasons { get; set; }   // experimental
    public int SkseDll { get; set; } = 0;   // 0 auto, 1 up to 1.6.1170, 2 newer, 3 installed separately
    public int GrassDensity { get; set; } = 8;   // percent of cached grass kept
    public bool IncludeDisabled { get; set; }
    public bool IncludeEnableParented { get; set; } = true;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnvilLOD", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
        }
        catch
        {
            return new(); // corrupt or unreadable settings shouldn't stop the app
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // best effort
        }
    }
}
