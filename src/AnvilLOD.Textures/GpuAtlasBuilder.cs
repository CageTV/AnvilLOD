using AnvilLOD.Core.Pipeline;

namespace AnvilLOD.Textures;

/// <summary>
/// Milestone 2 — the TexGen replacement.
/// <para>Plan:</para>
/// <list type="bullet">
/// <item>Collect the unique textures referenced by LOD meshes (diffuse, normal, and for PBR: RMAOS).</item>
/// <item>Downscale on the GPU (compute shader), pack with a skyline/maxrects packer, BC7-compress on the GPU.</item>
/// <item>Emit parallel atlas pages per channel so PBR LOD can sample the same UVs.</item>
/// <item>Return UV rects so the mesh stage can remap texture coordinates.</item>
/// </list>
/// Not implemented yet.
/// </summary>
public sealed class GpuAtlasBuilder : IAtlasBuilder
{
    public Task<AtlasResult> BuildAsync(IReadOnlyCollection<string> sourceTextures, string outputDataFolder, CancellationToken ct = default)
        => throw new NotImplementedException("Atlas building is milestone 2. See docs/ROADMAP.md.");
}
