using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes <c>SKSE\Plugins\AnvilLOD\AnvilLOD.dyn</c>, the data file the AnvilLOD SKSE plugin reads at startup.
/// <code>
/// char[4] "ALDY", u32 version (2; version 1 had no grid objects)
/// u32 stringCount, then per string: u16 byteLength + UTF-8 bytes   (plugin names and mesh paths)
/// u32 entryCount, then per entry (60 bytes):
///   u32 refPlugin (string index), u32 refLocalId,
///   u32 worldspacePlugin (string index), u32 worldspaceLocalId,
///   u32 mesh (string index, relative to Data\meshes), f32 pos[3], f32 rot[3] (radians), f32 scale,
///   u32 parentPlugin (string index, 0xFFFFFFFF = none), u32 parentLocalId,
///   u32 flags (1 = enable state opposite of parent, 2 = initially disabled,
///              4 = grid object: water / waterfall / fire / windmill, always dynamic and animated,
///              8 = near grid (drawn only near the loaded cells), 16 = never fade (drawn at any distance))
/// </code>
/// </summary>
public static class DynamicLodWriter
{
    public const string RelativePath = "SKSE\\Plugins\\AnvilLOD\\AnvilLOD.dyn";
    public const uint Version = 2;

    /// <param name="meshMap">Optional replacements for mesh paths (relative to Data\meshes, case-insensitive), e.g. water stand-ins.</param>
    public static int Write(string outputFolder, IReadOnlyList<DynamicLodReference> refs, IReadOnlyDictionary<string, string>? meshMap = null)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint Str(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = (uint)strings.Count;
                strings.Add(s);
                index[s] = i;
            }
            return i;
        }

        var entries = new List<(uint P, uint Id, uint WP, uint WId, uint Mesh, LodReference R, DynamicLodReference D)>();
        foreach (var d in refs.OrderBy(d => d.Ref.FormKey, StringComparer.Ordinal))
        {
            var r = d.Ref;
            var mesh = r.Meshes.Lod4 ?? r.Meshes.Lod8 ?? r.Meshes.Lod16 ?? r.Meshes.Lod32;
            if (string.IsNullOrEmpty(mesh)) continue;
            var rel = GamePath.Normalize(mesh);
            if (rel.StartsWith("meshes\\", StringComparison.Ordinal)) rel = rel["meshes\\".Length..];
            if (meshMap is not null && meshMap.TryGetValue(rel, out var replacement)) rel = replacement;
            if (d.ParentPlugin is { } parentPlugin) Str(parentPlugin);
            entries.Add((Str(d.RefPlugin), d.RefLocalId, Str(d.WorldspacePlugin), d.WorldspaceLocalId, Str(rel), r, d));
        }

        uint Str2(string s) => index[s];

        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path + ".tmp"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("ALDY"u8);
            w.Write(Version);
            w.Write((uint)strings.Count);
            foreach (var s in strings)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write((ushort)b.Length);
                w.Write(b);
            }
            w.Write((uint)entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.P); w.Write(e.Id); w.Write(e.WP); w.Write(e.WId); w.Write(e.Mesh);
                w.Write(e.R.Position.X); w.Write(e.R.Position.Y); w.Write(e.R.Position.Z);
                w.Write(e.R.RotationRadians.X); w.Write(e.R.RotationRadians.Y); w.Write(e.R.RotationRadians.Z);
                w.Write(e.R.Scale);
                w.Write(e.D.ParentPlugin is { } pp ? Str2(pp) : 0xFFFF_FFFFu);
                w.Write(e.D.ParentLocalId);
                w.Write(Flags(e.D));
            }
        }
        File.Move(path + ".tmp", path, overwrite: true);

        // Default settings for the SKSE plugin, only if the user doesn't have their own yet.
        var ini = Path.Combine(outputFolder, "SKSE", "Plugins", "AnvilLOD.ini");
        if (!File.Exists(ini))
            File.WriteAllText(ini, DefaultIni);
        return entries.Count;
    }

    public static uint Flags(DynamicLodReference d) =>
        (d.ParentOpposite ? 1u : 0u) | (d.InitiallyDisabled ? 2u : 0u)
        | (d.IsGridObject ? 4u : 0u) | (d.Grid == DynamicGrid.Near ? 8u : 0u) | (d.Grid == DynamicGrid.NeverFade ? 16u : 0u);

    public const string DefaultIni = """
        ; AnvilLOD SKSE plugin settings
        [Dynamic]
        ; Draw LOD for references that quests enable/disable (written by AnvilLOD into AnvilLOD\AnvilLOD.dyn)
        bEnabled=1
        ; How far from the player dynamic LOD is drawn, in game units (4096 = one cell)
        fMaxDistance=120000
        ; How often enable states are checked, in milliseconds
        iUpdateIntervalMs=500
        ; Safety cap on how many dynamic LOD objects are drawn at once
        iMaxShown=4000
        ; Water planes, waterfalls, fires, windmills and other DynDOLOD "grid" objects, drawn beyond the loaded cells
        bGridObjects=1
        ; How far "Near LOD" grid objects (water planes, creeks, fires) and "Far LOD" ones (waterfalls, windmills, ships) are drawn
        fNearGridDistance=40000
        fFarGridDistance=120000
        ; EXPERIMENTAL: keep them animated (waterfall flow, windmill blades). Can crash; off by default
        bAnimateExperimental=0
        iAnimationFps=30
        ; Log every object shown/hidden to Documents\My Games\Skyrim Special Edition\SKSE\AnvilLOD.log
        bVerboseLog=0

        [LOD]
        ; LOD, fade and grass distances, applied in game like DynDOLOD DLL NG does. Values from DynDOLOD's MCM files
        ; (MCM\Config\DynDOLOD\settings.ini, MCM\Settings\DynDOLOD.ini) are used first; anything set here wins.
        ; Saving this file while the game runs applies the change within a couple of seconds.
        bApplyLodDistances=1
        ; If DynDOLOD.dll is still installed it manages these; set to 1 to let AnvilLOD take over anyway
        bOverrideDynDOLOD=0
        ;fBlockLevel0Distance=60000
        ;fBlockLevel1Distance=90000
        ;fBlockMaximumDistance=250000
        ;fSplitDistanceMult=1.0
        ;fTreeLoadDistance=75000
        ;fSkyCellRefFadeDistance=150000
        ;fLODFadeOutMultObjects=15
        ;fLODFadeOutMultItems=10
        ;fLODFadeOutMultActors=15
        ;fLODFadeOutMultSkyCell=1
        ;fGrassStartFadeDistance=7000
        ;fGrassMaxStartFadeDistance=7000
        ;fGrassMinStartFadeDistance=0
        ;fGrassFadeRange=10000

        """;
}
