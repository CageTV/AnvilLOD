using System.Globalization;
using System.Text;

namespace AnvilLOD.Core.Lod;

/// <summary>The occlusion options of xLODGen's window.</summary>
/// <param name="ObjectThreads">Threads xLODGen uses to read the object LOD meshes for their heights (each holds a mesh in memory): -1 = no limit, 1 = the least memory.</param>
public sealed record OcclusionOptions(int Quality = 2, int Height = 0, int Radius = 100, string Mode = "-Flat +Border", int ObjectThreads = 2);

/// <summary>
/// xLODGen has no command line switches for what it generates: its window remembers the choices in the <c>[SSE LOD Options]</c> section
/// of <c>%LOCALAPPDATA%\Skyrim Special Edition\Plugins.sseviewsettings</c>. Setting those keys before it starts is the same as
/// ticking the boxes. Only the occlusion keys and the "make only this" switches are touched; every other line stays as it was.
/// </summary>
public static class XLodGenSettings
{
    public const string SectionName = "SSE LOD Options";

    /// <summary>The modes DynDOLOD_SSE.ini lists for OcclusionMode (xLODGen's Mode drop-down uses the same words).</summary>
    public static readonly string[] Modes =
    [
        "All", "+Border", "-Flat", "-Flat +Border",
        "+TVDT", "+TVDT +Border", "+TVDT -Flat", "+TVDT -Flat +Border",
        "-TVDT", "-TVDT +Border", "-TVDT -Flat", "-TVDT -Flat +Border",
    ];

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "Plugins.sseviewsettings");

    /// <summary>The key=value lines xLODGen has saved for the occlusion options, for showing the current values.</summary>
    public static IReadOnlyDictionary<string, string> Read(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var inSection = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { inSection = line == "[" + SectionName + "]"; continue; }
            if (!inSection) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) result[line[..eq]] = line[(eq + 1)..];
        }
        return result;
    }

    /// <summary>Returns the file text with the options set for an occlusion-only run. Adds the section or a key when it is missing.</summary>
    public static string Apply(string text, OcclusionOptions o)
    {
        var crlf = text.Contains("\r\n");
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var wanted = new (string Key, string Value)[]
        {
            ("Occlusion", "1"),
            ("OcclusionNewPlugin", "1"),
            ("ObjectsLOD", "0"),
            ("TreesLOD", "0"),
            ("TerrainLOD", "0"),
            ("Chunk", "0"),                    // a left-over "specific chunk" would limit the run to a few cells
            ("LODX", ""), ("LODY", ""), ("LODX2", ""), ("LODY2", ""),
            ("OcclusionCreateImages", "0"),    // debug images, only wanted for experiments
            ("OcclusionQuality", Math.Clamp(o.Quality, 1, 3).ToString(CultureInfo.InvariantCulture)),
            ("OcclusionHeight", Math.Max(0, o.Height).ToString(CultureInfo.InvariantCulture)),
            ("OcclusionRadius", Math.Max(1, o.Radius).ToString(CultureInfo.InvariantCulture)),
            ("OcclusionMode", string.IsNullOrWhiteSpace(o.Mode) ? "-Flat +Border" : o.Mode.Trim()),
            ("OcclusionMaxThreadsObjectLOD", (o.ObjectThreads < 1 ? -1 : o.ObjectThreads).ToString(CultureInfo.InvariantCulture)),
        };

        var header = "[" + SectionName + "]";
        var start = lines.FindIndex(l => l.Trim() == header);
        if (start < 0)
        {
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.Add(header);
            start = lines.Count - 1;
        }
        var end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;

        foreach (var (key, value) in wanted)
        {
            var at = -1;
            for (var i = start + 1; i < end; i++)
                if (lines[i].StartsWith(key + "=", StringComparison.Ordinal)) { at = i; break; }
            if (at >= 0) lines[at] = key + "=" + value;
            else
            {
                lines.Insert(end, key + "=" + value);
                end++;
            }
        }

        var sb = string.Join(crlf ? "\r\n" : "\n", lines);
        return sb.EndsWith('\n') || sb.Length == 0 ? sb : sb + (crlf ? "\r\n" : "\n");
    }

    /// <summary>
    /// Applies the options to the settings file, keeping the old one as <c>.bak_anvillod_&lt;time&gt;</c> when anything changes.
    /// Returns false when there is no file yet (xLODGen has never been run: it then starts with its defaults).
    /// </summary>
    public static bool ApplyToFile(string path, OcclusionOptions o, DateTime? now = null)
    {
        if (!File.Exists(path)) return false;
        // Latin-1 maps every byte to one character and back, so whatever encoding the file has, the lines we don't touch come out byte for byte.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var changed = Apply(text, o);
        if (changed == text) return true;
        File.Copy(path, path + ".bak_anvillod_" + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), overwrite: true);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(changed));
        return true;
    }
}
