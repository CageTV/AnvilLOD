using AnvilLOD.Core.Lod;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class TextureVariantTests
{
    [Fact]
    public void Id_depends_on_content_not_order_or_case()
    {
        var a = TextureVariant.Create("meshes\\Landscape\\Rocks\\RockCliff02.nif",
            [new AltTexture("RockCliff02:1", "textures\\a.dds", null), new AltTexture("RockCliff02:0", "textures\\b.dds", "textures\\b_n.dds")]);
        var b = TextureVariant.Create("MESHES\\landscape\\rocks\\rockcliff02.nif",
            [new AltTexture("rockcliff02:0", "TEXTURES\\B.dds", "textures\\b_n.dds"), new AltTexture("rockcliff02:1", "textures\\a.dds", null)]);
        var c = TextureVariant.Create("meshes\\Landscape\\Rocks\\RockCliff02.nif", [new AltTexture("RockCliff02:1", "textures\\other.dds", null)]);

        Assert.Equal(a.Id, b.Id);
        Assert.NotEqual(a.Id, c.Id);
    }

    [Fact]
    public void Plain_model_and_model_with_alternates_get_different_ids()
    {
        var plain = TextureVariant.Create("meshes\\x.nif", []);
        var alt = TextureVariant.Create("meshes\\x.nif", [new AltTexture("x:0", "textures\\snow.dds", null)]);
        Assert.NotEqual(plain.Id, alt.Id);
    }

    [Theory]
    [InlineData("meshes\\lod\\a_lod_0.nif", null)]
    [InlineData("meshes\\lod\\a_lod_0.nif", "0123456789ABCDEF")]
    public void Mesh_key_round_trips(string path, string? variant)
    {
        var (p, v) = LodMeshKey.Split(LodMeshKey.Make(path, variant));
        Assert.Equal(path, p);
        Assert.Equal(variant, v);
    }
}
