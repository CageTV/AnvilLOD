namespace AnvilLOD.Meshes;

/// <summary>
/// Texture set and shader flag handling shared by every LOD shape writer. The texture set is always the full nine slots, like the
/// engine's own meshes (a source mesh with a shorter set left slots the engine reads as null). Shader flags are passed through as the
/// source has them: DynDOLOD's own tree LOD sets Parallax and Facegen_Detail_Map with empty slots and loads fine, so those are not
/// what crashes the game on a creek LOD shape.
/// </summary>
public static class ShaderGuard
{
    public const int Slots = 9;
    private const uint VertexColors = 1u << 5;

    public static string[] TextureSlots(IReadOnlyList<string> textures)
    {
        var slots = new string[Slots];
        for (int i = 0; i < Slots; i++) slots[i] = i < textures.Count ? textures[i] ?? "" : "";
        return slots;
    }

    /// <summary>The vanilla flat normal map (Skyrim - Textures0.bsa).</summary>
    public const string FlatNormal = @"textures\default_n.dds";

    /// <summary>
    /// The texture slots of a baked object-LOD shape. A shape with a diffuse texture and no normal map (Water for ENB's creek water, a mod's
    /// effect-like plane) gets the vanilla flat normal: the standard LOD shader reads the normal slot, and a null one is a null texture in
    /// the lighting shader's material setup, which crashed the game while drawing such a shape (user report, 2026-10-15).
    /// </summary>
    public static string[] BakedSlots(IReadOnlyList<string> textures)
    {
        var slots = TextureSlots(textures);
        if (slots[0].Length > 0 && string.IsNullOrWhiteSpace(slots[1])) slots[1] = FlatNormal;
        return slots;
    }

    /// <summary>The shader flags to write: the material's own, plus Vertex_Colors when the shape has vertex colours.</summary>
    public static (uint Flags1, uint Flags2) Safe(LodMaterial m, bool colors)
        => (m.ShaderFlags1, m.ShaderFlags2 | (colors ? VertexColors : 0u));
}
