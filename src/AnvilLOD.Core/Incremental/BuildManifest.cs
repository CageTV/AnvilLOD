using System.Text.Json;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Incremental;

/// <summary>
/// Persisted record of the last build: block file → input fingerprint.
/// Stored next to the output as <c>AnvilLOD.manifest.json</c>.
/// </summary>
public sealed class BuildManifest
{
    public const string FileName = "AnvilLOD.manifest.json";

    public int AlgorithmVersion { get; set; } = QuadHasher.AlgorithmVersion;
    public DateTimeOffset BuiltAt { get; set; }
    public Dictionary<string, string> Quads { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tree LOD: worldspace → fingerprint of its trees, billboards and the vanilla blocks it overrides.</summary>
    public Dictionary<string, string> Trees { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BuildManifest LoadOrEmpty(string outputFolder)
    {
        var path = Path.Combine(outputFolder, FileName);
        if (!File.Exists(path)) return new BuildManifest();
        try
        {
            var m = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(path), Json) ?? new();
            // Rebuild everything if the algorithm changed.
            return m.AlgorithmVersion == QuadHasher.AlgorithmVersion ? m : new BuildManifest();
        }
        catch (JsonException)
        {
            return new BuildManifest();
        }
    }

    public void Save(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        BuiltAt = DateTimeOffset.Now;
        var path = Path.Combine(outputFolder, FileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <param name="outputFolder">If given, blocks whose file is missing on disk are rebuilt even if unchanged.</param>
    /// <param name="inScope">Which manifest entries this run is responsible for (e.g. only the scanned worldspaces
    /// and selected levels). Entries outside the scope are never reported as stale.</param>
    public BuildPlan Plan(IReadOnlyDictionary<QuadKey, string> currentHashes, string? outputFolder = null, Func<string, bool>? inScope = null)
    {
        var rebuild = new List<QuadKey>();
        var unchanged = new List<QuadKey>();
        foreach (var (quad, hash) in currentHashes)
        {
            bool same = Quads.TryGetValue(quad.RelativePath, out var old) && old == hash;
            if (same && outputFolder is not null && !File.Exists(FullPath(outputFolder, quad.RelativePath))) same = false;
            (same ? unchanged : rebuild).Add(quad);
        }

        var live = currentHashes.Keys.Select(q => q.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = Quads.Keys.Where(k => !live.Contains(k) && (inScope?.Invoke(k) ?? true)).ToList();
        return new BuildPlan(rebuild, unchanged, stale);
    }

    /// <summary>Records a finished build: successful blocks get their new hash, failed ones are dropped
    /// (so they rebuild next time), and stale entries are removed.</summary>
    public void Commit(IReadOnlyDictionary<QuadKey, string> currentHashes, IEnumerable<QuadKey> succeeded, IEnumerable<QuadKey> failed, IEnumerable<string> removed)
    {
        foreach (var q in succeeded) Quads[q.RelativePath] = currentHashes[q];
        foreach (var q in failed) Quads.Remove(q.RelativePath);
        foreach (var r in removed) Quads.Remove(r);
    }

    public static string FullPath(string outputFolder, string relativePath) =>
        Path.Combine(outputFolder, relativePath.Replace('\\', Path.DirectorySeparatorChar));

    /// <summary>Parses "meshes\\terrain\\WS\\objects\\WS.L.X.Y.bto" back into worldspace and level.</summary>
    public static bool TryParseBlockPath(string relativePath, out string worldspace, out int level)
    {
        worldspace = ""; level = 0;
        var name = Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/').Split('/').Last());
        var parts = name.Split('.');
        if (parts.Length < 4 || !int.TryParse(parts[^3], out level)) return false;
        worldspace = string.Join('.', parts[..^3]);
        return true;
    }
}

/// <summary>What a build needs to do: write <see cref="Rebuild"/>, keep <see cref="Unchanged"/>, delete <see cref="StaleFiles"/>.</summary>
public sealed record BuildPlan(
    IReadOnlyList<QuadKey> Rebuild,
    IReadOnlyList<QuadKey> Unchanged,
    IReadOnlyList<string> StaleFiles);
