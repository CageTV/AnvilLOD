using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Textures;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes the opaque-alpha copies that object LOD points at (<see cref="OpaqueLodTextures.OutputRoot"/>). Only the textures the blocks
/// built in this run asked for are written; a copy is kept as long as its source is the same.
/// </summary>
public static class OpaqueLodStage
{
    private const string Sidecar = "opaque.json";
    private const int MaxSize = 1024; // only used when a BC7 / uncompressed texture has to be re-encoded

    public sealed record Result(int Written, int Unchanged, int Failed, TimeSpan Elapsed, IReadOnlyList<string> Errors);

    /// <summary>The test <see cref="OpaqueLodTextures"/> uses to decide whether a texture's alpha channel is translucent.</summary>
    public static bool IsTranslucent(byte[] dds) => DdsAlpha.IsTranslucent(dds);

    public static Result Run(IAssetSource assets, OpaqueLodTextures opaque, string output, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = Path.Combine(output, OpaqueLodTextures.OutputRoot);
        var sidecarPath = Path.Combine(root, Sidecar);
        var known = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(sidecarPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(sidecarPath)) is { } old)
                foreach (var kv in old) known[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        int written = 0, unchanged = 0, failed = 0;
        var errors = new ConcurrentBag<string>();

        Parallel.ForEach(opaque.Conversions, new ParallelOptions { CancellationToken = ct }, kv =>
        {
            var (rel, src) = (kv.Key, kv.Value);
            var dest = Path.Combine(output, rel);
            var stamp = "v1|" + (assets.Fingerprint(src) ?? "?");
            if (File.Exists(dest) && known.TryGetValue(rel, out var prev) && prev == stamp) { Interlocked.Increment(ref unchanged); return; }
            try
            {
                if (!assets.TryOpen(src, out var s)) throw new FileNotFoundException(src);
                byte[] bytes;
                using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
                var dds = DdsAlpha.MakeOpaque(bytes, MaxSize);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, dds);
                known[rel] = stamp;
                Interlocked.Increment(ref written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                errors.Add($"{src}: {ex.Message}");
            }
        });

        if (known.Count > 0)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(known.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k.Key, k => k.Value)));
        }
        return new Result(written, unchanged, failed, sw.Elapsed, [.. errors.Take(20)]);
    }

    /// <summary>Deletes every opaque copy this tool wrote (used when the option is off).</summary>
    public static void RemoveAll(string output)
    {
        try
        {
            var root = Path.Combine(output, OpaqueLodTextures.OutputRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
