using System.Text.Json;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// PGPatcher (ParallaxGen) rewrites meshes and records, for every mesh it changed, the CRC32 of the file before and after in
/// <c>ParallaxGen_Diff.json</c> (<c>"meshes\\path\\tree.nif": { "crc32original": ..., "crc32patched": ... }</c>). DynDOLOD looks 3D tree LOD
/// models up by the ORIGINAL checksum, so a LOD pack made for the unpatched tree mod keeps working after PGPatcher. AnvilLOD does the same.
/// </summary>
public static class ParallaxGenDiff
{
    public const string FileName = "ParallaxGen_Diff.json";

    public readonly record struct Entry(uint Original, uint Patched);

    /// <summary>The checksum pairs by mesh path (lower-case, backslashes). An unreadable file gives an empty map and a note.</summary>
    public static Dictionary<string, Entry> Parse(Stream json, out string? problem)
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        problem = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { problem = "not a list of meshes"; return map; }
            foreach (var e in doc.RootElement.EnumerateObject())
                if (e.Value.ValueKind == JsonValueKind.Object
                    && e.Value.TryGetProperty("crc32original", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetUInt32(out var original)
                    && e.Value.TryGetProperty("crc32patched", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out var patched))
                    map[Normalize(e.Name)] = new Entry(original, patched);
        }
        catch (JsonException ex) { problem = ex.Message; map.Clear(); }
        return map;
    }

    public static string Normalize(string meshPath) => meshPath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>
    /// The checksums to look a tree's LOD model up by, best first: the original one when this exact file is the one PGPatcher wrote
    /// (the LOD was made for the mesh before patching), then the file's own.
    /// </summary>
    public static IReadOnlyList<uint> LodCrcs(IReadOnlyDictionary<string, Entry>? map, string meshPath, uint actual)
    {
        if (map is not null && map.TryGetValue(Normalize(meshPath), out var e) && e.Patched == actual && e.Original != actual)
            return [e.Original, actual];
        return [actual];
    }
}
