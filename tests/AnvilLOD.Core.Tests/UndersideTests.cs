using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

public class UndersideTests
{
    private const string Ws = "TestWorld";

    /// <summary>Hilly terrain over cells [0,32) x [0,32) and one ring of cells around them.</summary>
    private static TerrainHeights Hills(int from = -1, int to = 33)
    {
        var t = new TerrainHeights();
        for (int cy = from; cy < to; cy++)
            for (int cx = from; cx < to; cx++)
            {
                var h = new float[TerrainHeights.Grid * TerrainHeights.Grid];
                for (int y = 0; y < TerrainHeights.Grid; y++)
                    for (int x = 0; x < TerrainHeights.Grid; x++)
                        h[y * TerrainHeights.Grid + x] = Height(cx * 32 + x, cy * 32 + y);
                t.Set(Ws, cx, cy, h);
            }
        return t;
    }

    private static float Height(int gx, int gy) =>
        2000f * MathF.Sin(gx * 0.05f) * MathF.Cos(gy * 0.031f) + ((gx * 7 + gy * 13) % 5) * 90f;

    [Fact]
    public void Faces_point_down_and_the_mesh_never_rises_above_the_terrain()
    {
        var terrain = Hills();
        var shape = UndersideMesher.Build(terrain, Ws, 0, 0, step: 16)!;
        Assert.NotNull(shape);
        Assert.Equal(64 * 64 * 2, shape.TriangleCount);

        for (int t = 0; t < shape.Indices.Length; t += 3)
        {
            var a = shape.Positions[shape.Indices[t]];
            var b = shape.Positions[shape.Indices[t + 1]];
            var c = shape.Positions[shape.Indices[t + 2]];
            Assert.True(Vector3.Cross(b - a, c - a).Z < 0, "face points up");
        }

        // Every LAND vertex is at or above the underside surface directly beneath it.
        float unit = 16 * 128f;
        for (int gy = 0; gy < 1024; gy += 3)
            for (int gx = 0; gx < 1024; gx += 3)
            {
                float x = gx * 128f, y = gy * 128f;
                int i = Math.Min((int)(x / unit), 63), j = Math.Min((int)(y / unit), 63);
                float fx = x / unit - i, fy = y / unit - j;
                float z = SurfaceZ(shape, i, j, fx, fy);
                Assert.True(z <= Height(gx, gy) + 0.01f, $"underside {z} above terrain {Height(gx, gy)} at {gx},{gy}");
            }
    }

    // The mesh is a regular grid whose squares are split along (i,j)-(i+1,j+1).
    private static float SurfaceZ(UndersideMesher.Shape s, int i, int j, float fx, float fy)
    {
        float Z(int ii, int jj) => s.Positions.First(p => MathF.Abs(p.X - ii * 2048f) < 0.01f && MathF.Abs(p.Y - jj * 2048f) < 0.01f).Z;
        float z00 = Z(i, j), z11 = Z(i + 1, j + 1);
        return fx >= fy
            ? z00 + fx * (Z(i + 1, j) - z00) + fy * (z11 - Z(i + 1, j))
            : z00 + fy * (Z(i, j + 1) - z00) + fx * (z11 - Z(i, j + 1));
    }

    [Fact]
    public void Neighbouring_blocks_agree_along_their_shared_edge()
    {
        var terrain = Hills(-1, 65);
        var west = UndersideMesher.Build(terrain, Ws, 0, 0)!;
        var east = UndersideMesher.Build(terrain, Ws, 32, 0)!;
        var edgeX = 32 * CellCoord.CellSize;
        var w = west.Positions.Where(p => MathF.Abs(p.X - edgeX) < 0.01f).OrderBy(p => p.Y).ToArray();
        var e = east.Positions.Where(p => MathF.Abs(p.X - edgeX) < 0.01f).OrderBy(p => p.Y).ToArray();
        Assert.Equal(65, w.Length);
        Assert.Equal(w.Length, e.Length);
        for (int k = 0; k < w.Length; k++) Assert.Equal(w[k].Z, e[k].Z);
    }

    [Fact]
    public void A_block_without_terrain_has_no_underside_and_missing_cells_leave_holes()
    {
        var terrain = new TerrainHeights();
        Assert.Null(UndersideMesher.Build(terrain, Ws, 0, 0));

        var h = new float[TerrainHeights.Grid * TerrainHeights.Grid];
        terrain.Set(Ws, 0, 0, h);
        var small = UndersideMesher.Build(terrain, Ws, 0, 0, step: 32)!;
        Assert.NotNull(small);
        Assert.True(small.TriangleCount < 32 * 32 * 2);
    }

    [Fact]
    public void Nif_has_one_shape_per_block_with_the_expected_textures()
    {
        var terrain = Hills(-1, 65);
        var shapes = new[] { UndersideMesher.Build(terrain, Ws, 0, 0, 32)!, UndersideMesher.Build(terrain, Ws, 32, 0, 32)! };
        var r = UndersideBuilder.Build(Ws, shapes)!;
        Assert.Equal(2, r.Shapes);
        Assert.Equal(shapes.Sum(s => s.TriangleCount), r.Triangles);
        var text = System.Text.Encoding.Latin1.GetString(r.Bytes);
        Assert.Contains("BSFadeNode", text);
        Assert.Contains("Textures\\Terrain\\TestWorld\\TestWorld.32.0.0.dds", text);
        Assert.Contains("Textures\\Terrain\\TestWorld\\TestWorld.32.32.0.dds", text);
        Assert.Equal("Terrain\\TestWorld\\TestWorld_Underside.nif", UndersideBuilder.ModelPath(Ws));
        Assert.Null(UndersideBuilder.Build(Ws, []));
    }
}
