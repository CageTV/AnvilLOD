using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Textures;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes the converted PBR albedo copies that object LOD points at (<see cref="PbrLodTextures.OutputRoot"/>). Only the textures the
/// blocks built in this run asked for are converted; a copy is kept as long as its source, the size and the brightness are the same.
/// </summary>
public static class PbrLodStage
{
    private const string Sidecar = "pbrlod.json";

    public sealed record Result(int Converted, int Unchanged, int Failed, int Twins, TimeSpan Elapsed, IReadOnlyList<string> Errors);

    public static Result Run(IAssetSource assets, PbrLodTextures pbr, string output, int maxSize, float brightness,
        IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = Path.Combine(output, PbrLodTextures.OutputRoot);
        var sidecarPath = Path.Combine(root, Sidecar);
        var known = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(sidecarPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(sidecarPath)) is { } old)
                foreach (var kv in old) known[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        var settings = FormattableString.Invariant($"{maxSize}|{brightness:F3}|{PbrLodConverter.Gamma}|{PbrLodConverter.BaseScale}");
        int converted = 0, unchanged = 0, failed = 0;
        var errors = new ConcurrentBag<string>();

        Parallel.ForEach(pbr.Conversions, new ParallelOptions { CancellationToken = ct }, kv =>
        {
            var (rel, src) = (kv.Key, kv.Value);
            var dest = Path.Combine(output, rel);
            var stamp = settings + "|" + (assets.Fingerprint(src) ?? "?");
            if (File.Exists(dest) && known.TryGetValue(rel, out var prev) && prev == stamp) { Interlocked.Increment(ref unchanged); return; }
            try
            {
                if (!assets.TryOpen(src, out var s)) throw new FileNotFoundException(src);
                byte[] bytes;
                using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
                var dds = PbrLodConverter.Convert(bytes, maxSize, brightness);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, dds);
                known[rel] = stamp;
                Interlocked.Increment(ref converted);
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
        return new Result(converted, unchanged, failed, pbr.TwinsUsed, sw.Elapsed, [.. errors.Take(20)]);
    }

    /// <summary>Deletes every converted copy this tool wrote (used when the option is off).</summary>
    public static void RemoveAll(string output)
    {
        try
        {
            var root = Path.Combine(output, PbrLodTextures.OutputRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
