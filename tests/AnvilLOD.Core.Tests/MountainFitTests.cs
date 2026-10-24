using System.Numerics;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

public class MountainFitTests
{
    [Theory]
    [InlineData(@"lod\mountains\mountaincliff04_lod_0l.nif", @"landscape\mountains\mountaincliff04.nif")]
    [InlineData("lod/mountains/MountainCliff04_LOD_0.nif", @"landscape\mountains\MountainCliff04.nif")]
    [InlineData(@"lod\mountains\mountainridge01_lod_1.nif", @"landscape\mountains\mountainridge01.nif")]
    [InlineData(@"dlc01\lod\mountains\dlc1mountainridge02cave_lod_0h.nif", @"dlc01\landscape\mountains\dlc1mountainridge02cave.nif")]
    [InlineData(@"lod\mountains\mountaincliff04_lod_2.nif", null)]      // LOD16 is never drawn over the full model
    [InlineData(@"lod\whiterun\whiterunwall_lod_0.nif", null)]
    public void Full_model_is_found_from_the_LOD_path_for_LOD4_and_LOD8_mountains(string lodPath, string? expected)
        => Assert.Equal(expected, MountainFit.FullModelFor(lodPath));

    private static LodMeshPart Tri(Vector3[] positions) => new()
    {
        Material = new LodMaterial([@"textures\a.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, false, 0, 0, Vector3.Zero, 1f),
        Positions = positions,
        UVs = positions.Select(_ => Vector2.Zero).ToArray(),
        Normals = positions.Select(_ => Vector3.UnitZ).ToArray(),
        Tangents = positions.Select(_ => Vector3.UnitX).ToArray(),
        Bitangents = positions.Select(_ => Vector3.UnitY).ToArray(),
        Triangles = [0, 1, 2],
    };

    private static LodMesh Floor() => new() { Path = "full", Parts = [Tri([new(-500, -500, 0), new(500, -500, 0), new(0, 500, 0)])] };

    [Fact]
    public void Vertices_outside_the_full_model_land_on_its_surface_and_inside_ones_stay()
    {
        // The full model is a flat floor at z = 0 facing up. The LOD has one vertex 30 above it, one 10 below it and one on it.
        var lod = new LodMesh { Path = "lod", Parts = [Tri([new(0, 0, 30), new(100, 0, -10), new(0, 100, 0)])] };

        var fitted = MountainFit.Fit(lod, Floor(), out var moved);

        Assert.Equal(1, moved);
        Assert.Equal(0f, fitted.Parts[0].Positions[0].Z, 3);     // the vertex 30 above came down to the surface
        Assert.Equal(-10f, fitted.Parts[0].Positions[1].Z, 3);   // inside: untouched
        Assert.Equal(0f, fitted.Parts[0].Positions[2].Z, 3);
        Assert.Equal(30f, lod.Parts[0].Positions[0].Z, 3);       // the source mesh is not changed
    }

    [Fact]
    public void A_vertex_too_far_from_the_full_model_is_left_alone()
    {
        var lod = new LodMesh { Path = "lod", Parts = [Tri([new(0, 0, MountainFit.MaxMove + 20), new(100, 0, 0), new(0, 100, 0)])] };
        var fitted = MountainFit.Fit(lod, Floor(), out var moved);
        Assert.Equal(0, moved);
        Assert.Same(lod, fitted);
    }
}
