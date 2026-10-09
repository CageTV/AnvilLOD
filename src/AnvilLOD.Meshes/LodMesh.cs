using System.Numerics;

namespace AnvilLOD.Meshes;

/// <summary>
/// Everything about a surface that decides whether two pieces of geometry can be merged
/// into one shape (= one draw call) in a LOD block.
/// </summary>
public sealed record LodMaterial(
    IReadOnlyList<string> Textures,   // texture set slots, as written in the source mesh
    uint ShaderFlags1,                // already reduced to the bits LOD keeps (all of them for a passthru material)
    uint ShaderFlags2,
    uint ClampMode,
    bool HasAlpha,
    ushort AlphaFlags,
    byte AlphaThreshold,
    Vector3 EmissiveColor,
    float EmissiveMultiple,
    LodShaderPassthru? Passthru = null)   // the source shader's own settings, for models that say "passthru"
{
    private string? _key;

    /// <summary>Stable string key used for grouping and hashing.</summary>
    public string Key => _key ??= string.Join('|', Textures.Select(t => t.ToLowerInvariant()))
        + $"|{ShaderFlags1:X}|{ShaderFlags2:X}|{ClampMode}|{(HasAlpha ? $"{AlphaFlags:X}/{AlphaThreshold}" : "-")}"
        + $"|{EmissiveColor.X:F3},{EmissiveColor.Y:F3},{EmissiveColor.Z:F3}x{EmissiveMultiple:F3}"
        + (Passthru is null ? "" : "|pt" + Passthru.Key);

    public bool Equals(LodMaterial? other) => other is not null && Key == other.Key;
    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// A BSLightingShaderProperty's own values, kept as they are in the source mesh. LODGen does this for LOD models whose
/// file name ends in <c>passthru_lod</c> ("do not modify the shader"); every other LOD mesh gets LODGen's standard LOD shader.
/// </summary>
public sealed record LodShaderPassthru(
    uint ShaderType,
    float UvOffsetX, float UvOffsetY, float UvScaleX, float UvScaleY,
    float Alpha, float Refraction, float Glossiness,
    Vector3 SpecularColor, float SpecularStrength,
    float LightingEffect1, float LightingEffect2)
{
    public string Key => FormattableString.Invariant(
        $"{ShaderType}:{UvOffsetX:G6},{UvOffsetY:G6},{UvScaleX:G6},{UvScaleY:G6}:{Alpha:G6},{Refraction:G6},{Glossiness:G6}:{SpecularColor.X:G6},{SpecularColor.Y:G6},{SpecularColor.Z:G6}:{SpecularStrength:G6}:{LightingEffect1:G6},{LightingEffect2:G6}");
}

/// <summary>
/// One shape of a LOD mesh, already baked into mesh space (all node transforms applied).
/// Directions are unit vectors; Bitangent follows the NIF's own naming.
/// </summary>
public sealed class LodMeshPart
{
    /// <summary>The shape's name in the source mesh. LODGen reads instructions from it (crown, trunk, flattrunk, spherenormals…).</summary>
    public string? Name { get; init; }
    public required LodMaterial Material { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector2[] UVs { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector3[] Tangents { get; init; }
    public required Vector3[] Bitangents { get; init; }
    public uint[]? Colors { get; init; }          // RGBA packed little-endian, or null
    public required ushort[] Triangles { get; init; } // 3 indices per triangle

    public int VertexCount => Positions.Length;
    public int TriangleCount => Triangles.Length / 3;
}

/// <summary>A loaded LOD mesh (all usable shapes) plus anything skipped while reading it.</summary>
public sealed class LodMesh
{
    public required string Path { get; init; }
    public required IReadOnlyList<LodMeshPart> Parts { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
