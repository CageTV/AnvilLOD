using System.Collections.Concurrent;
using System.Diagnostics;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace AnvilLOD.Plugins;

/// <summary>
/// Loose files + BSAs resolved once, with load-order priority (loose beats any archive,
/// later archives beat earlier ones).
/// <para>
/// Mutagen's built-in <c>ArchiveAssetProvider</c> re-opens every BSA on every lookup, which is
/// far too slow for LOD work (tens of thousands of lookups). This builds the archive index
/// once up front and checks loose files lazily, so it also works through MO2's VFS without
/// enumerating the whole Data folder.
/// </para>
/// </summary>
public sealed class AssetIndex : IAssetSource
{
    private readonly Func<string, FileInfo?> _looseResolver;
    private readonly Func<string, IEnumerable<string>> _looseEnumerator;
    private readonly Dictionary<string, ArchiveEntry> _archived;
    private readonly ConcurrentDictionary<string, FileInfo?> _looseCache = new(StringComparer.Ordinal);

    /// <summary>Archives that were read.</summary>
    public int ArchiveCount { get; }
    public int ArchivedFileCount => _archived.Count;

    /// <summary>Archives that could not be read (a corrupt file, or names Mutagen cannot decode), each as "name: reason". They are left out.</summary>
    public IReadOnlyList<string> SkippedArchives { get; }
    public TimeSpan IndexTime { get; }

    private sealed record ArchiveEntry(string ArchiveName, IArchiveFile File);

    private AssetIndex(Func<string, FileInfo?> looseResolver, Func<string, IEnumerable<string>> looseEnumerator,
        Dictionary<string, ArchiveEntry> archived, int archiveCount, TimeSpan time, IReadOnlyList<string> skipped)
    {
        SkippedArchives = skipped;
        _looseResolver = looseResolver;
        _looseEnumerator = looseEnumerator;
        _archived = archived;
        ArchiveCount = archiveCount;
        IndexTime = time;
    }

    /// <summary>Plain Data folder (or MO2 VFS): Mutagen decides which BSAs load; loose files are checked on demand.</summary>
    /// <param name="pathFilter">Optional filter on normalized paths (e.g. "meshes\\") to keep the index small.</param>
    /// <param name="pluginOrder">Plugin file names in load order; only used to order the archives if Mutagen can't.</param>
    public static AssetIndex Build(GameRelease release, string dataFolder, Func<string, bool>? pathFilter = null,
        IReadOnlyList<string>? pluginOrder = null)
    {
        List<string> archives;
        try
        {
            archives = Archive.GetApplicableArchivePaths(release, dataFolder).Select(p => p.Path).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException)
        {
            // Mutagen's archive ordering throws for some archive names (seen on a 3,600-plugin Steam install).
            // Order them ourselves: base game first, then each plugin's archives in load order.
            archives = ArchiveOrder.LowToHigh(Directory.EnumerateFiles(dataFolder, "*.bsa"), pluginOrder ?? []);
        }
        IEnumerable<string> EnumerateLoose(string prefix)
        {
            var dir = Path.Combine(dataFolder, prefix.TrimEnd('\\').Replace('\\', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir)) yield break;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
                yield return GamePath.Normalize(Path.GetRelativePath(dataFolder, f));
        }

        return Create(release, archives, EnumerateLoose, key =>
        {
            var fi = new FileInfo(Path.Combine(dataFolder, key.Replace('\\', Path.DirectorySeparatorChar)));
            return fi.Exists ? fi : null;
        }, pathFilter);
    }

    /// <summary>
    /// Explicit archive list (lowest priority first) and loose-file resolver — used for MO2 instances
    /// read straight from disk.
    /// </summary>
    public static AssetIndex Create(GameRelease release, IReadOnlyList<string> archivesLowToHigh,
        Func<string, IEnumerable<string>> looseEnumerator,
        Func<string, FileInfo?> looseResolver, Func<string, bool>? pathFilter = null)
    {
        var sw = Stopwatch.StartNew();
        var map = new Dictionary<string, ArchiveEntry>(StringComparer.Ordinal);
        var skipped = new List<string>();

        foreach (var archivePath in archivesLowToHigh)
        {
            var name = Path.GetFileName(archivePath);
            // The name table is read lazily, so a bad archive (e.g. "Strings section was not able to be read: did not end all
            // of its strings in null bytes", seen with a mod whose BSA holds non-ASCII file names) throws while the files are
            // listed. Collect them first so a half-read archive adds nothing, and skip the archive rather than the whole run.
            List<(string Key, IArchiveFile File)> entries = [];
            try
            {
                var reader = Archive.CreateReader(release, archivePath);
                foreach (var f in reader.Files)
                {
                    var key = GamePath.Normalize(f.Path);
                    if (pathFilter is not null && !pathFilter(key)) continue;
                    entries.Add((key, f));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add($"{name}: {ex.GetBaseException().Message}");
                continue;
            }
            foreach (var (key, f) in entries) map[key] = new ArchiveEntry(name, f); // later archive wins
        }

        return new AssetIndex(looseResolver, looseEnumerator, map, archivesLowToHigh.Count - skipped.Count, sw.Elapsed, skipped);
    }

    /// <summary>All data-relative paths (normalized) under a prefix such as "meshes\\", from loose files and BSAs.</summary>
    public IEnumerable<string> EnumeratePaths(string prefix)
    {
        prefix = GamePath.Normalize(prefix);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _looseEnumerator(prefix))
            if (k.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(k)) yield return k;
        foreach (var k in _archived.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal) && seen.Add(k)) yield return k;
    }

    private FileInfo? Loose(string key) => _looseCache.GetOrAdd(key, _looseResolver);

    public bool Exists(string dataRelativePath)
    {
        var key = GamePath.Normalize(dataRelativePath);
        return Loose(key) is not null || _archived.ContainsKey(key);
    }

    public bool TryOpen(string dataRelativePath, out Stream stream)
    {
        var key = GamePath.Normalize(dataRelativePath);
        if (Loose(key) is { } fi)
        {
            stream = fi.OpenRead();
            return true;
        }
        if (_archived.TryGetValue(key, out var e))
        {
            stream = new MemoryStream(e.File.GetBytes(), writable: false);
            return true;
        }
        stream = Stream.Null;
        return false;
    }

    public string? Fingerprint(string dataRelativePath)
    {
        var key = GamePath.Normalize(dataRelativePath);
        if (Loose(key) is { } fi) return $"L:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        if (_archived.TryGetValue(key, out var e)) return $"A:{e.ArchiveName}:{e.File.Size}";
        return null;
    }

    /// <summary>Where the winning copy comes from — handy for reports ("loose" or the BSA name).</summary>
    public string? SourceOf(string dataRelativePath)
    {
        var key = GamePath.Normalize(dataRelativePath);
        if (Loose(key) is not null) return "loose";
        return _archived.TryGetValue(key, out var e) ? e.ArchiveName : null;
    }
}
