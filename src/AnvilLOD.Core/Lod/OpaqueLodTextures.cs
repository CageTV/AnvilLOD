using System.Collections.Concurrent;
using AnvilLOD.Core.Pipeline;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// Object LOD shapes without an alpha property are still alpha-tested by the engine's LOD shader, so a diffuse texture whose alpha
/// channel holds something other than opacity (ship hulls: gloss) comes out full of holes. Vanilla and DynDOLOD LOD textures are
/// atlases with opaque alpha and never show it. A shape without an alpha property that points at such a texture is sent to an opaque
/// copy under <see cref="OutputRoot"/>; the original is never touched. The pipeline writes the copies.
/// </summary>
public sealed class OpaqueLodTextures(IAssetSource assets, Func<byte[], bool> isTranslucent)
{
    public const string OutputRoot = "textures\\anvillod\\opaque\\";

    private readonly ConcurrentDictionary<string, string?> _mapped = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _conversions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Opaque copies to write: output path (relative to the LOD output) → the texture it comes from.</summary>
    public IReadOnlyDictionary<string, string> Conversions => _conversions;

    /// <summary>The opaque copy's path for a diffuse texture of a shape without an alpha property, or null to leave the texture alone.</summary>
    public string? MapDiffuse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _mapped.GetOrAdd(path, Compute);
    }

    private string? Compute(string original)
    {
        var p = PbrLodTextures.Normalize(original);
        if (!p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) return null;
        // Atlases and textures AnvilLOD or TexGen made are opaque already (or alpha-tested on purpose through their own alpha property).
        if (p.StartsWith("textures\\anvillod\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\terrain\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\dyndolod\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\lod\\", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("lod.dds", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (!assets.TryOpen(p, out var s)) return null;
            byte[] bytes;
            using (s) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            if (!isTranslucent(bytes)) return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { return null; }

        var output = OutputRoot + p["textures\\".Length..];
        _conversions[output] = p;
        return output;
    }
}
