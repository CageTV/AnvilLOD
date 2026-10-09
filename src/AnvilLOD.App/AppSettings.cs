using System.IO;
using System.Text.Json;
using AnvilLOD.Core.Settings;

namespace AnvilLOD.App;

/// <summary>One of the preset tabs: a name and the saved values of the settings column (null until first used).</summary>
public sealed class PresetSlot
{
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement>? Values { get; set; }
}

/// <summary>UI settings, saved to %AppData%\AnvilLOD\settings.json between sessions.</summary>
public sealed class AppSettings
{
    public bool UseMo2 { get; set; } = true;
    public bool UseVortex { get; set; }   // when UseMo2 is false: Vortex (true) or a plain Data folder (false)
    public string? Mo2Instance { get; set; }
    public string? Mo2Profile { get; set; }
    public string? Mo2GamePath { get; set; }        // typed over ModOrganizer.ini's gamePath (empty = auto-detect)
    public string? Mo2ModsFolder { get; set; }      // typed over the ini's mods folder
    public string? Mo2ProfilesFolder { get; set; }  // typed over the ini's profiles folder
    public string? Mo2OverwriteFolder { get; set; } // typed over the ini's overwrite folder
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
    public string? MakerInputs { get; set; }        // LOD Mesh Maker: the models (one path per line)
    public bool MakerFolderMode { get; set; }       // false = a new mod in the MO2 mods folder, true = a folder the user chose
    public string? MakerModName { get; set; }
    public string? MakerOutput { get; set; }
    public bool LargeReferences { get; set; }   // list missing large references in AnvilLOD.esm (off by default)
    public bool LargeRefsEsl { get; set; } = true; // flag AnvilLOD.esm as ESL (no plugin slot); off = a normal ESM that uses a slot
    public bool PbrLod { get; set; }            // object LOD textures that match PBR full models (off by default)
    public int PbrLodBrightness { get; set; } = 100; // percent of DynDOLOD's PBR scale for the converted copies, 10-110
    public bool Underside { get; set; }         // terrain underside (NIFs + AnvilLOD.esp), off by default
    public bool Tree3D { get; set; }            // 3D tree LOD models in object LOD (off by default)
    public bool Tree3DLod8 { get; set; }        // use the 3D models at LOD8 as well
    public bool Tree3DByName { get; set; }      // accept a model stored under the plain tree name when the CRC32 doesn't match
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

    // ----- presets: the three tabs above the settings column -----

    public const int PresetCount = 3;

    /// <summary>The preset tabs. The settings above are always the working copy of the active one.</summary>
    public List<PresetSlot> Presets { get; set; } = [];
    public int ActivePreset { get; set; }

    /// <summary>Settings that belong to the game source in the top bar, the other tabs, or the presets themselves, not to a preset.</summary>
    private static readonly HashSet<string> NotPerPreset =
    [
        nameof(UseMo2), nameof(UseVortex), nameof(Mo2Instance), nameof(Mo2Profile), nameof(Mo2GamePath), nameof(Mo2ModsFolder),
        nameof(Mo2ProfilesFolder), nameof(Mo2OverwriteFolder), nameof(DataFolder), nameof(PluginsTxt),
        nameof(MakerInputs), nameof(MakerFolderMode), nameof(MakerModName), nameof(MakerOutput),
        nameof(Presets), nameof(ActivePreset),
    ];

    /// <summary>Makes sure there are three named slots and a valid active one (the first run after an update turns the saved settings into preset 1).</summary>
    public void EnsurePresets()
    {
        while (Presets.Count < PresetCount) Presets.Add(new PresetSlot());
        if (Presets.Count > PresetCount) Presets.RemoveRange(PresetCount, Presets.Count - PresetCount);
        for (int i = 0; i < PresetCount; i++)
            if (string.IsNullOrWhiteSpace(Presets[i].Name)) Presets[i].Name = $"Preset {i + 1}";
        ActivePreset = Math.Clamp(ActivePreset, 0, PresetCount - 1);
        Presets[ActivePreset].Values ??= PresetSnapshot.Capture(this, NotPerPreset);
    }

    /// <summary>Saves the settings column into the active slot.</summary>
    public void StoreActivePreset() => Presets[ActivePreset].Values = PresetSnapshot.Capture(this, NotPerPreset);

    /// <summary>
    /// Makes another slot the active one: its saved values become the settings. A slot that was never used starts as a copy of
    /// what is on screen, so the first click on an empty tab doesn't blank the column.
    /// </summary>
    public void ActivatePreset(int index)
    {
        StoreActivePreset();
        ActivePreset = Math.Clamp(index, 0, PresetCount - 1);
        if (Presets[ActivePreset].Values is { } values) PresetSnapshot.Apply(this, values, NotPerPreset);
        else StoreActivePreset();
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnvilLOD", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
            s.EnsurePresets();
            return s;
        }
        catch
        {
            var s = new AppSettings(); // corrupt or unreadable settings shouldn't stop the app
            s.EnsurePresets();
            return s;
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
