using System.Collections.Concurrent;
using AnvilLOD.Core.Pipeline;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// Makes object LOD use textures that match PBR full models. The LOD shaders are vanilla (no PBR, no sRGB), so a LOD mesh that
/// points at a texture the full model replaces with a PBR one (<c>textures\pbr\&lt;same path&gt;</c>) is drawn with the vanilla
/// look while the real object shows the PBR one. Two cases, as DynDOLOD handles them:
/// <list type="number">
/// <item>A LOD texture <c>X…lod.dds</c> that TexGen has rendered a PBR twin of (<c>X…pbr_lod.dds</c>, with <c>_n</c>): use the twin.</item>
/// <item>A full-size texture that has a PBR version: use a converted copy of the PBR albedo, written to
/// <see cref="OutputRoot"/> (never over the vanilla path, which other things still use). The pipeline writes the copies.</item>
/// </list>
/// </summary>
public sealed class PbrLodTextures(IAssetSource assets)
{
    public const string OutputRoot = "textures\\anvillod\\pbr\\";

    private readonly ConcurrentDictionary<string, string?> _diffuse = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _conversions = new(StringComparer.OrdinalIgnoreCase);
    private int _twins;

    /// <summary>Converted copies to write: output path (relative to the LOD output) → the PBR albedo it comes from.</summary>
    public IReadOnlyDictionary<string, string> Conversions => _conversions;

    /// <summary>Distinct textures swapped for a TexGen <c>pbr_lod</c> twin.</summary>
    public int TwinsUsed => _twins;

    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').Trim().TrimStart('\\');
        if (p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p[5..];
        return p.ToLowerInvariant();
    }

    /// <summary>The replacement for a diffuse texture, or null to leave it alone.</summary>
    public string? MapDiffuse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return _diffuse.GetOrAdd(path, Compute);
    }

    /// <summary>The replacement normal map for a texture whose diffuse was swapped for a twin, or null.</summary>
    public string? MapNormal(string diffuse, string normal)
    {
        var mapped = MapDiffuse(diffuse);
        if (mapped is null || mapped.StartsWith(OutputRoot, StringComparison.OrdinalIgnoreCase)) return null;
        var twinNormal = mapped[..^4] + "_n.dds";
        return assets.Exists(twinNormal) ? twinNormal : null;
    }

    private string? Compute(string original)
    {
        var p = Normalize(original);
        if (!p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || !p.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.StartsWith("textures\\pbr\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\anvillod\\", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith("pbr_lod.dds", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\terrain\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("textures\\dyndolod\\", StringComparison.OrdinalIgnoreCase)) return null;

        if (p.EndsWith("lod.dds", StringComparison.OrdinalIgnoreCase))
        {
            var twin = p[..^"lod.dds".Length] + "pbr_lod.dds";
            if (assets.Exists(twin)) { Interlocked.Increment(ref _twins); return twin; }
        }

        var pbr = "textures\\pbr\\" + p["textures\\".Length..];
        if (!assets.Exists(pbr)) return null;
        var output = OutputRoot + p["textures\\".Length..];
        _conversions[output] = pbr;
        return output;
    }
}
