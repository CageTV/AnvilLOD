using System.Security.Cryptography;
using System.Text;

namespace AnvilLOD.Core.Lod;

/// <summary>One alternate texture of a base record's model (MODS): the shape it applies to and the texture-set slots it replaces.</summary>
/// <param name="ShapeName">The 3D name in the full model, e.g. "RockPileL04:0".</param>
/// <param name="Diffuse">Replacement for slot 0, or null to keep it.</param>
/// <param name="Normal">Replacement for slot 1, or null to keep it.</param>
public sealed record AltTexture(string ShapeName, string? Diffuse, string? Normal);

/// <summary>
/// What a base record's object looks like in the full model: its model file and its alternate textures (snow, ice, dirt, moss
/// variants share one model and swap textures). LOD models are made from the full model, so their textures have to follow it:
/// DynDOLOD does this too (its object report lists "shape = texture -> texture" per LOD model). <see cref="Id"/> is a hash of the
/// content, so bases that look the same share one variant.
/// </summary>
public sealed record TextureVariant(string Id, string FullModel, IReadOnlyList<AltTexture> Alternates)
{
    public static TextureVariant Create(string fullModel, IReadOnlyList<AltTexture> alternates)
    {
        var sb = new StringBuilder(fullModel.Trim().ToLowerInvariant());
        foreach (var a in alternates.OrderBy(a => a.ShapeName, StringComparer.OrdinalIgnoreCase))
            sb.Append('\n').Append(a.ShapeName.ToLowerInvariant()).Append('|').Append(a.Diffuse?.ToLowerInvariant()).Append('|').Append(a.Normal?.ToLowerInvariant());
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 8);
        return new TextureVariant(id, fullModel, alternates);
    }
}

/// <summary>A LOD mesh path together with the variant whose textures it should follow, as one string key for mesh caches.</summary>
public static class LodMeshKey
{
    public const char Separator = '\u0001';

    public static string Make(string path, string? variant) => variant is null ? path : path + Separator + variant;

    public static (string Path, string? Variant) Split(string key)
    {
        var i = key.IndexOf(Separator);
        return i < 0 ? (key, null) : (key[..i], key[(i + 1)..]);
    }
}
