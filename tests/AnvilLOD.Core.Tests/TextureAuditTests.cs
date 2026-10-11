using System.Numerics;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

public class TextureAuditTests
{
    private static LodMesh Mesh(string path, params string[] textures)
    {
        var material = new LodMaterial(textures, 0, 0, 0, false, 0, 0, Vector3.Zero, 1f);
        return new LodMesh
        {
            Path = path,
            Parts =
            [
                new LodMeshPart
                {
                    Material = material,
                    Positions = [], UVs = [], Normals = [], Tangents = [], Bitangents = [], Triangles = [],
                },
            ],
        };
    }

    [Fact]
    public void A_texture_that_exists_nowhere_is_reported_with_the_meshes_that_use_it()
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "textures\\lod\\ash01lod.dds" };
        var missing = LodGenerator.FindMissingTextures(
            [
                Mesh("meshes\\a_lod_0.nif", "textures\\lod\\ash01lod.dds", "textures\\lod\\ash01lod_n.dds"),
                Mesh("meshes\\b_lod_0.nif", "Textures/LOD/ash01lod_n.dds", ""),
            ],
            have.Contains);

        var entry = Assert.Single(missing);
        Assert.Equal("textures\\lod\\ash01lod_n.dds", entry.Key);
        Assert.Equal(("meshes\\a_lod_0.nif", 2), entry.Value);
    }

    [Fact]
    public void Slashes_case_and_a_missing_textures_prefix_do_not_cause_false_reports()
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "textures\\landscape\\x.dds" };
        Assert.Empty(LodGenerator.FindMissingTextures([Mesh("m", "Landscape/X.dds")], have.Contains));
    }

    [Fact]
    public void Only_the_diffuse_and_normal_slots_are_checked()
    {
        var missing = LodGenerator.FindMissingTextures(
            [Mesh("m", "textures\\a.dds", "textures\\a_n.dds", "textures\\envmask_not_checked.dds")],
            t => t.StartsWith("textures\\a", StringComparison.Ordinal) && !t.Contains("envmask"));
        Assert.Empty(missing);
    }
}
