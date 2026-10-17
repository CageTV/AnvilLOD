using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class MeshCleanerTests
{
    private static readonly LodMaterial Mat = new(["textures\\x.dds"], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f);

    private static LodMeshPart Part(Vector3[] pos, ushort[] tris, Vector2[]? uv = null) => new()
    {
        Material = Mat,
        Positions = pos,
        UVs = uv ?? pos.Select(_ => Vector2.Zero).ToArray(),
        Normals = pos.Select(_ => Vector3.UnitZ).ToArray(),
        Tangents = pos.Select(_ => Vector3.UnitX).ToArray(),
        Bitangents = pos.Select(_ => Vector3.UnitY).ToArray(),
        Colors = null,
        Triangles = tris,
    };

    [Fact]
    public void Identical_vertices_are_merged_and_triangles_follow()
    {
        // A square drawn as two triangles with the shared edge's vertices written twice.
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 10, 0) };
        var r = MeshCleaner.Clean(Part(pos, [0, 1, 2, 3, 4, 5]));
        Assert.Equal(2, r.Part.TriangleCount);
        Assert.Equal(4, r.Part.VertexCount);
        Assert.Equal(2, r.VerticesMerged);
        Assert.Equal(0, r.TrianglesDropped);
        // Each surviving triangle still has the same corner positions as before.
        var corners = Enumerable.Range(0, 6).Select(k => r.Part.Positions[r.Part.Triangles[k]]).ToArray();
        Assert.Equal(pos, corners);
    }

    [Fact]
    public void Vertices_that_differ_in_uv_are_not_merged()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 10, 0) };
        var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0.5f, 0.5f), new Vector2(1, 1), new Vector2(0, 1) };
        var r = MeshCleaner.Clean(Part(pos, [0, 1, 2, 3, 4, 5], uv));
        Assert.Equal(5, r.Part.VertexCount);   // only the two (10,10) corners share every attribute
        Assert.Equal(1, r.VerticesMerged);
    }

    [Fact]
    public void Triangles_without_area_are_dropped()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(5, 0, 0) };
        // 0,1,2 is real; 0,1,1 repeats a corner; 0,3,1 lies on one line.
        var r = MeshCleaner.Clean(Part(pos, [0, 1, 2, 0, 1, 1, 0, 3, 1]));
        Assert.Equal(1, r.Part.TriangleCount);
        Assert.Equal(2, r.TrianglesDropped);
        Assert.Equal(3, r.Part.VertexCount);   // the vertex only the dropped triangles used is gone
    }

    [Fact]
    public void A_clean_mesh_is_returned_untouched()
    {
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0) };
        var part = Part(pos, [0, 1, 2]);
        var r = MeshCleaner.Clean(part);
        Assert.Same(part, r.Part);
        Assert.Equal(0, r.TrianglesDropped);
        Assert.Equal(0, r.VerticesMerged);
    }
}

public class UnderwaterTests
{
    [Fact]
    public void A_triangle_completely_under_the_water_is_dropped_but_one_poking_out_is_kept()
    {
        float? Ground(float x, float y) => -5000f;   // deep terrain: nothing is buried
        float? Water(float x, float y) => 0f;
        var test = new BtoBuilder.TerrainTest(Ground, 64f, Water);
        // Below water minus the margin (64): dropped.
        Assert.True(test.Buried(new(0, 0, -200), new(100, 0, -200), new(0, 100, -200)));
        // One corner above the surface: kept.
        Assert.False(test.Buried(new(0, 0, -200), new(100, 0, -200), new(0, 100, 50)));
        // Inside the safety margin of the surface: kept.
        Assert.False(test.Buried(new(0, 0, -30), new(100, 0, -30), new(0, 100, -30)));
    }

    [Fact]
    public void Cells_without_water_keep_their_triangles()
    {
        var test = new BtoBuilder.TerrainTest((x, y) => -5000f, 64f, (x, y) => null);
        Assert.False(test.Buried(new(0, 0, -200), new(100, 0, -200), new(0, 100, -200)));
    }

    [Fact]
    public void Without_a_water_lookup_only_terrain_decides()
    {
        var test = new BtoBuilder.TerrainTest((x, y) => -5000f, 64f);
        Assert.False(test.Buried(new(0, 0, -200), new(100, 0, -200), new(0, 100, -200)));
    }

    [Fact]
    public void Terrain_water_is_stored_per_cell_and_has_a_stable_fingerprint()
    {
        var t = new TerrainHeights();
        t.SetWater("Tamriel", 0, 0, -100f);
        Assert.Equal(-100f, t.WaterAt("Tamriel", 10f, 10f));
        Assert.Null(t.WaterAt("Tamriel", 5000f, 10f));          // the next cell has none
        var before = t.WaterFingerprint("Tamriel");
        Assert.Equal(before, t.WaterFingerprint("tamriel"));
        t.SetWater("Tamriel", 1, 0, 0f);
        Assert.NotEqual(before, t.WaterFingerprint("Tamriel"));
    }
}
