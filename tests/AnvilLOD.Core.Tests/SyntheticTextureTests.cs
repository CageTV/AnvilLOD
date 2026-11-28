using System.Numerics;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

/// <summary>
/// Tree cards and the other synthetic meshes are not kept in the generator's mesh cache, so the copy of AnvilLOD's own billboards into the
/// output and the missing-texture audit have to learn their textures another way (Lively Farms' light-plugin trees drew purple in game).
/// </summary>
public class SyntheticTextureTests
{
    private const string Card = "~anvillod-treecard|";
    private const string Dds = "textures\\terrain\\lodgen\\lively farms.esp\\maple1_00000800_1.dds";
    private const string Normal = "textures\\terrain\\lodgen\\lively farms.esp\\maple1_00000800_1_n.dds";

    private sealed class NoAssets : IAssetSource
    {
        public bool Exists(string dataRelativePath) => false;
        public bool TryOpen(string dataRelativePath, out Stream stream) { stream = Stream.Null; return false; }
        public string? Fingerprint(string dataRelativePath) => null;
    }

    private sealed class OneCard : ISyntheticMeshSource
    {
        public bool Owns(string path) => path.StartsWith(Card, StringComparison.Ordinal);

        public LodMesh? Build(string path) => new()
        {
            Path = path,
            Parts =
            [
                new LodMeshPart
                {
                    Material = new LodMaterial([Dds, Normal, "", "", "", "", "", "", ""], 0, 0, 3, true, 0, 0, Vector3.Zero, 1f),
                    Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
                    UVs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
                    Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
                    Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
                    Bitangents = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                    Triangles = [0, 1, 2],
                },
            ],
        };
    }

    [Fact]
    public void A_tree_card_texture_is_reported_as_used_and_as_missing_when_it_exists_nowhere()
    {
        var generator = new LodGenerator(new NoAssets(), null, new OneCard());
        Assert.NotNull(generator.GetMesh(Card + Dds));

        var used = generator.UsedTextures();
        Assert.Contains(Dds, used);
        Assert.Contains(Normal, used);

        var missing = generator.AuditTextures(System.IO.Path.GetTempPath() + "anvillod-no-such-output");
        Assert.True(missing.ContainsKey(Dds));
        Assert.Equal(Card + Dds, missing[Dds].FirstMesh);
    }
}
