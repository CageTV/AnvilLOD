using System.Diagnostics;
using AnvilLOD.Core.World;

namespace AnvilLOD.Plugins.Mo2;

/// <summary>
/// Our own read-only stand-in for MO2's virtual Data folder: game Data, then every enabled mod in
/// priority order, then overwrite. Later layers win, exactly as in MO2.
/// <para>
/// Only what LOD needs is indexed: root-level files (plugins, BSAs) and the subtrees named in
/// <c>looseRoots</c> (e.g. meshes, lodsettings). Each layer is enumerated in parallel.
/// </para>
/// </summary>
public sealed class VirtualDataIndex
{
    /// <summary>Layers lowest priority first.</summary>
    public IReadOnlyList<string> Layers { get; }

    private readonly Dictionary<string, string> _root;   // "skyrim.esm" -> full path
    private readonly Dictionary<string, string> _loose;  // "meshes\\foo\\bar.nif" -> full path

    public int LooseFileCount => _loose.Count;
    public TimeSpan BuildTime { get; }
    public int MissingModFolders { get; }
    public string? ExcludedLayer { get; private init; }
    private IReadOnlyList<string> _indexedRoots = [];

    private VirtualDataIndex(IReadOnlyList<string> layers, Dictionary<string, string> root,
        Dictionary<string, string> loose, TimeSpan time, int missing)
    {
        Layers = layers;
        _root = root;
        _loose = loose;
        BuildTime = time;
        MissingModFolders = missing;
    }

    public static VirtualDataIndex Build(Mo2Profile profile, IReadOnlyCollection<string> looseRoots, string? excludeFolder = null)
    {
        string? exclude = string.IsNullOrWhiteSpace(excludeFolder) ? null : Path.GetFullPath(excludeFolder).TrimEnd('\\', '/');
        string? excluded = null;
        var sw = Stopwatch.StartNew();
        var inst = profile.Instance;

        var layers = new List<string> { inst.GameDataFolder };
        int missing = 0;
        foreach (var mod in profile.EnabledModsLowToHigh)
        {
            var dir = Path.Combine(inst.ModsFolder, mod);
            if (exclude is not null && string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), exclude, StringComparison.OrdinalIgnoreCase))
            {
                excluded = dir;
                continue;
            }
            if (Directory.Exists(dir)) layers.Add(dir);
            else missing++;
        }
        if (Directory.Exists(inst.OverwriteFolder)) layers.Add(inst.OverwriteFolder);

        // Enumerate every layer in parallel, then merge in priority order.
        var perLayer = new (List<KeyValuePair<string, string>> Root, List<KeyValuePair<string, string>> Loose)[layers.Count];
        Parallel.For(0, layers.Count, i => perLayer[i] = ScanLayer(layers[i], looseRoots, inst.SkipDirectories));

        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        var loose = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (r, l) in perLayer)
        {
            foreach (var kv in r) root[kv.Key] = kv.Value;
            foreach (var kv in l) loose[kv.Key] = kv.Value;
        }

        return new VirtualDataIndex(layers, root, loose, sw.Elapsed, missing)
        {
            ExcludedLayer = excluded,
            _indexedRoots = looseRoots.Select(r => r.Replace('/', '\\').Trim('\\').ToLowerInvariant() + "\\").ToList(),
        };
    }

    private static (List<KeyValuePair<string, string>>, List<KeyValuePair<string, string>>) ScanLayer(
        string layer, IReadOnlyCollection<string> looseRoots, IReadOnlySet<string> skipDirs)
    {
        var root = new List<KeyValuePair<string, string>>();
        var loose = new List<KeyValuePair<string, string>>();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System,
        };

        try
        {
            foreach (var f in Directory.EnumerateFiles(layer, "*", options))
            {
                if (f.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                root.Add(new(Path.GetFileName(f).ToLowerInvariant(), f));
            }

            // Roots may be nested ("textures\\terrain\\lodgen"), so only that subtree is walked.
            foreach (var root0 in looseRoots)
            {
                var rel = root0.Replace('/', '\\').Trim('\\').ToLowerInvariant();
                var dir = Path.Combine(layer, rel.Replace('\\', Path.DirectorySeparatorChar));
                if (Directory.Exists(dir)) WalkLoose(dir, rel, loose, skipDirs);
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException) { }

        return (root, loose);
    }

    private static void WalkLoose(string dir, string relPrefix, List<KeyValuePair<string, string>> into, IReadOnlySet<string> skipDirs)
    {
        var stack = new Stack<(string Dir, string Rel)>();
        stack.Push((dir, relPrefix));
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };

        while (stack.Count > 0)
        {
            var (d, rel) = stack.Pop();
            foreach (var f in Directory.EnumerateFiles(d, "*", options))
            {
                var file = Path.GetFileName(f);
                if (file.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                into.Add(new(rel + "\\" + file.ToLowerInvariant(), f));
            }
            foreach (var s in Directory.EnumerateDirectories(d, "*", options))
            {
                var n = Path.GetFileName(s);
                if (skipDirs.Contains(n) || n.EndsWith(".mohidden", StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push((s, rel + "\\" + n.ToLowerInvariant()));
            }
        }
    }

    /// <summary>Full path of a root-level file (plugin or BSA) as MO2 would resolve it.</summary>
    public string? ResolveRootFile(string fileName) =>
        _root.TryGetValue(fileName.ToLowerInvariant(), out var p) ? p : null;

    public IEnumerable<string> RootFileNames => _root.Keys;

    /// <summary>Normalized data-relative paths of indexed loose files under a prefix ("meshes\\").</summary>
    public IEnumerable<string> EnumerateLoose(string prefix)
    {
        var p = GamePath.Normalize(prefix);
        foreach (var k in _loose.Keys)
            if (k.StartsWith(p, StringComparison.Ordinal)) yield return k;
    }

    /// <summary>Full path of a loose file under an indexed root, or null.</summary>
    public string? ResolveLoose(string dataRelativePath) =>
        _loose.TryGetValue(GamePath.Normalize(dataRelativePath), out var p) ? p : null;

    /// <summary>
    /// A loose file outside the indexed roots (a texture, say), looked up on disk layer by layer, highest priority
    /// first. Inside an indexed root the index is the answer.
    /// </summary>
    public string? FindLoose(string dataRelativePath)
    {
        var key = GamePath.Normalize(dataRelativePath);
        if (_loose.TryGetValue(key, out var p)) return p;
        foreach (var r in _indexedRoots)
            if (key.StartsWith(r, StringComparison.Ordinal)) return null;
        var rel = key.Replace('\\', Path.DirectorySeparatorChar);
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            var full = Path.Combine(Layers[i], rel);
            if (File.Exists(full) && !File.Exists(full + ".mohidden")) return full;
        }
        return null;
    }
}
