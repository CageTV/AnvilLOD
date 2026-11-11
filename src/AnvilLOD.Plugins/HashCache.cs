using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnvilLOD.Plugins;

/// <summary>
/// Content hashes of loose files, remembered by path, size and last-write time, so a file is read again only when it changed.
/// A support app (PGPatcher, TexGen, TreeLODGen, xLODGen...) that rewrites a file gives it a new time but, when nothing in it changed, the same hash:
/// that is the difference between "the tool ran" and "the content is different". Stored as JSON next to the baseline it belongs to.
/// </summary>
public sealed class HashCache
{
    private sealed record Entry(long Length, long Ticks, string Hash);

    private readonly ConcurrentDictionary<string, Entry> _map;
    private readonly string _path;
    private int _computed;

    private HashCache(string path, ConcurrentDictionary<string, Entry> map) { _path = path; _map = map; }

    /// <summary>Files read and hashed in this run (the rest came from the cache).</summary>
    public int Computed => _computed;
    public int Count => _map.Count;

    public static HashCache Load(string path)
    {
        var map = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? [])
                    map[k] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* a damaged cache is just rebuilt */ }
        return new HashCache(path, map);
    }

    /// <summary>Short content hash of the file (16 hex digits of SHA-256).</summary>
    public string HashOf(FileInfo file)
    {
        var key = file.FullName;
        long ticks = file.LastWriteTimeUtc.Ticks, len = file.Length;
        if (_map.TryGetValue(key, out var e) && e.Length == len && e.Ticks == ticks) return e.Hash;
        string hash;
        try
        {
            using var fs = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            hash = Convert.ToHexString(SHA256.HashData(fs), 0, 8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }
        Interlocked.Increment(ref _computed);
        _map[key] = new Entry(len, ticks, hash);
        return hash;
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map.ToDictionary(kv => kv.Key, kv => kv.Value)));
        File.Move(tmp, _path, overwrite: true);
    }
}
