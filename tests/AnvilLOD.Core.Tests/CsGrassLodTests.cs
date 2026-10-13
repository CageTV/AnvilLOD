using System.Numerics;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Core.Tests;

public class CsGrassLodTests
{
    private static readonly LodMaterial Grass = new(["textures\\landscape\\grass\\x.dds", "", "", "", "", "", "", "", ""], 0x80000008u, 0x31u, 3, true, 0x12EC, 128, Vector3.Zero, 1f);

    private static LodMeshPart Quad(bool colors) => new()
    {
        Name = "quad", Material = Grass,
        Positions = [new(0, 0, 0), new(10, 0, 0), new(0, 0, 10), new(10, 0, 10)],
        UVs = [new(0, 1), new(1, 1), new(0, 0), new(1, 0)],
        Normals = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
        Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
        Bitangents = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Colors = colors ? [0xFFFFFFFFu, 0xFFFFFFFFu, 0xFF808080u, 0xFF808080u] : null,
        Triangles = [0, 1, 2, 1, 3, 2],
    };

    // The descriptors the Grass Optimizations stage accepts: what vanilla and nearly every grass mod's models use (checked on DynDOLOD's grass
    // output and 137 of Folkvangr's 156 models), which is what AnvilLOD's own NIF writer produces.
    [Fact]
    public void The_writers_vertex_layout_with_colours_is_the_grass_layout()
    {
        var nif = LodNifWriter.Write("g", [Quad(colors: true)]);
        Assert.Equal(0x0003B00007650408UL, NifGeometryReader.FirstShapeVertexDescriptor(nif));
    }

    [Fact]
    public void The_layout_without_colours_is_the_other_accepted_one()
    {
        var nif = LodNifWriter.Write("g", [Quad(colors: false)]);
        Assert.Equal(0x0001B00000650407UL, NifGeometryReader.FirstShapeVertexDescriptor(nif));
    }

    [Fact]
    public void A_mesh_without_shapes_has_no_descriptor()
    {
        Assert.Null(NifGeometryReader.FirstShapeVertexDescriptor(LodNifWriter.Write("empty", [])));
    }
}
