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
    /// <summary>Which version of the defaults the saved file has seen: 1 = mountain inset 12 (older files still hold the old 0 that nobody chose).</summary>
    public int SettingsRevision { get; set; }
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
    public int MapObjects { get; set; }              // LOD32 objects for the world map: 0 auto, 1 from the rules only, 2/3/4 = the level 0/1/2 LOD mesh
    public bool MapTrees { get; set; } = true;       // tree cards in LOD32 (3D tree LOD): off = no trees on the map
    public string? DynDolodFolder { get; set; }
    public int OcclusionTool { get; set; }           // the Occlusion page: 0 xLODGen, 1 DynDOLOD
    public bool OccAfterGenerate { get; set; } = true; // start the occlusion run when Generate has finished (skipped, with a note, when the program is not set up)
    public bool OccAutoClose { get; set; } = true;    // close xLODGen's window when its log says the work is done (you still confirm the save)
    public bool OccPreset { get; set; } = true;       // set xLODGen's occlusion options before it starts
    public int OccQuality { get; set; } = 2;          // xLODGen occlusion quality 1-3
    public int OccHeight { get; set; }                // extra observer height
    public int OccRadius { get; set; } = 100;         // cells
    public string OccMode { get; set; } = "-Flat +Border";
    public int OccThreads { get; set; }               // threads that read the object LOD meshes (memory!): 0 = by the installed RAM, 1, 2, 4 or -1 = no limit
    public string? XLodGenFolder { get; set; }       // folder with xLODGenx64.exe (DynDOLOD's folder is DynDolodFolder)
    public string Preset { get; set; } = "High";
    public bool Candles { get; set; }                // also load DynDOLOD's Candles rules
    public bool FxGlow { get; set; }                 // also load DynDOLOD's FXGlow rules
    public bool CustomRulesEnabled { get; set; }     // use the custom rule file below (off: DynDOLOD's own rule files only)
    public string? CustomRulesFile { get; set; }     // a DynDOLOD-format rule file; written by the rule editor or picked by the user
    public bool RemoveBuried { get; set; } = true;
    public bool RemoveUnderwater { get; set; } = true;
    public bool TreeLod { get; set; } = true;
    public string? MakerInputs { get; set; }        // LOD Mesh Maker: the models (one path per line)
    public bool MakerFolderMode { get; set; }       // false = a new mod in the MO2 mods folder, true = a folder the user chose
    public string? MakerModName { get; set; }
    public string? MakerOutput { get; set; }
    public bool LargeReferences { get; set; }   // list missing large references in AnvilLOD.esm (off by default)
    public bool CleanOutput { get; set; } = true;   // empty the output folder before generating (only if it is AnvilLOD's own)
    public bool LargeRefsEsl { get; set; } = true; // flag AnvilLOD.esm as ESL (no plugin slot); off = a normal ESM that uses a slot
    public bool PbrLod { get; set; }            // object LOD textures that match PBR full models (off by default)
    public int PbrLodBrightness { get; set; } = 100; // percent of DynDOLOD's PBR scale for the converted copies, 10-110
    public bool Underside { get; set; }         // terrain underside (NIFs + AnvilLOD.esp), off by default
    public int EslRange { get; set; }       // 0 auto-detect from the game, 1 = 2,048 records per ESL, 2 = 4,096 (game 1.6.1130 or newer only)
    public bool NativeEffects { get; set; } // fires, candles, torches, lava and streams as real references too, off by default
    public bool NativeGridObjects { get; set; } // waterfalls, windmill fans and water wheels as real references in AnvilLOD.esp, off by default
    public bool RenderTreeBillboards { get; set; }   // trees without a TexGen billboard get one drawn from their model (off by default)
    public bool Tree3D { get; set; }            // 3D tree LOD models in object LOD (off by default)
    public bool Tree3DLod8 { get; set; }        // use the 3D models at LOD8 as well
    public string? LastRunSizes { get; set; }   // JSON of the last generation's size by LOD level and layer (SizeCalibration), per preset, for the size estimate
    public bool Tree3DByName { get; set; } = true; // accept a model stored under the plain tree name when no CRC32-named one exists (DynDOLOD's own convention: CRC32 names are only for collisions)
    public bool GrassLod { get; set; } = true;
    public int TreeBrightness { get; set; } = 100;   // percent, 10-110
    public int ObjectBrightness { get; set; } = 100; // percent, 10-110
    public int TargetSizeGb { get; set; }             // the size target picked for "Fit my settings" (0 = none)
    public bool CsGrassLod { get; set; } = true;      // LOD meshes for Community Shaders' Grass Optimizations (only made when the feature is installed)
    public bool AnimatedShips { get; set; } = true;   // Animated Ships' sailing ships drawn beyond the loaded cells
    public bool DetailedShips { get; set; } = true;   // whole ships in object LOD from simplified copies of their real models
    public int IceInset { get; set; } = 6;           // units, 0-24: glacier/ice LOD pulled inside its surface (0 = off)
    public int MountainInset { get; set; } = 12;     // units, 0-24: the same for mountain LOD (12 = the ERM add-on and other mountain mods stop flickering)
    public int ObjectInset { get; set; } = 0;        // units, 0-24: the same for every other object in LOD4/LOD8 (experimental)
    public bool DynamicLod { get; set; } = true;
    public bool GridObjects { get; set; } = true;
    public bool ParentCopies { get; set; }
    public bool GlowWindows { get; set; }
    public bool Seasons { get; set; }   // Seasons of Skyrim seasonal LOD
    public int SkseDll { get; set; } = 0;   // 0 auto, 1 up to 1.6.1170, 2 newer, 3 installed separately, 4 Skyrim VR 1.4.15 (the numbers are SkseDllChoice's)
    public int GrassDensity { get; set; } = 8;   // percent of cached grass kept
    public bool WaterStandIns { get; set; }      // lit fake-water stand-ins for stream/creek/pond water planes
    public int GrassTop { get; set; } = 85;      // grass LOD brightness at the top of a tuft, percent (DynDOLOD GrassBrightnessTop)
    public int GrassBottom { get; set; } = 50;   // ... and at the roots (GrassBrightnessBottom)
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
        nameof(SettingsRevision), nameof(UseMo2), nameof(UseVortex), nameof(Mo2Instance), nameof(Mo2Profile), nameof(Mo2GamePath), nameof(Mo2ModsFolder),
        nameof(Mo2ProfilesFolder), nameof(Mo2OverwriteFolder), nameof(DataFolder), nameof(PluginsTxt),
        nameof(OcclusionTool), nameof(XLodGenFolder), nameof(OccPreset), nameof(OccAutoClose), nameof(OccAfterGenerate), nameof(OccQuality), nameof(OccHeight), nameof(OccRadius), nameof(OccMode), nameof(OccThreads), nameof(MakerInputs), nameof(MakerFolderMode), nameof(MakerModName), nameof(MakerOutput),
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

    public const int CurrentRevision = 1;

    /// <summary>Brings a saved file up to the current defaults once: a stored mountain inset of 0 was only ever the old default.</summary>
    public void Migrate()
    {
        if (SettingsRevision >= CurrentRevision) return;
        if (MountainInset == 0) MountainInset = 12;
        foreach (var slot in Presets)
            if (slot.Values is { } v && v.TryGetValue(nameof(MountainInset), out var el) && el.ValueKind == JsonValueKind.Number && el.GetInt32() == 0)
                v[nameof(MountainInset)] = JsonSerializer.SerializeToElement(12);
        SettingsRevision = CurrentRevision;
    }

    public static AppSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
            s.Migrate();
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
        SettingsRevision = CurrentRevision;   // what is saved has the current defaults applied (a file written here is never migrated again)
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
