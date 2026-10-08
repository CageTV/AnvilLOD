using System.Numerics;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class AuthoringTests
{
    private static readonly LodMaterial Mat = new(["textures\\a.dds", "", "", "", "", "", "", "", ""], 0x80000300u, 5u, 3, false, 0, 0, Vector3.Zero, 1f);

    private static LodMeshPart Grid(int n, float size, Func<float, float, float>? height = null)
    {
        var pos = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<ushort>();
        for (int y = 0; y <= n; y++)
        for (int x = 0; x <= n; x++)
        {
            float fx = x * size / n, fy = y * size / n;
            pos.Add(new Vector3(fx, fy, height?.Invoke(fx, fy) ?? 0));
            uv.Add(new Vector2(x / (float)n, y / (float)n));
        }
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * (n + 1) + x;
            tris.AddRange([(ushort)i, (ushort)(i + 1), (ushort)(i + n + 2), (ushort)i, (ushort)(i + n + 2), (ushort)(i + n + 1)]);
        }
        int c = pos.Count;
        return new LodMeshPart
        {
            Material = Mat, Positions = [.. pos], UVs = [.. uv],
            Normals = Enumerable.Repeat(Vector3.UnitZ, c).ToArray(), Tangents = Enumerable.Repeat(Vector3.UnitX, c).ToArray(),
            Bitangents = Enumerable.Repeat(Vector3.UnitY, c).ToArray(), Triangles = [.. tris],
        };
    }

    [Fact]
    public void Flat_grid_collapses_to_a_few_triangles()
    {
        var g = Grid(20, 1000);
        var s = MeshSimplifier.Simplify(g, maxError: 1f);
        Assert.InRange(s.TriangleCount, 2, 40);   // border points only slide along the border
        // corners survive
        foreach (var corner in new[] { Vector3.Zero, new Vector3(1000, 0, 0), new Vector3(0, 1000, 0), new Vector3(1000, 1000, 0) })
            Assert.Contains(s.Positions, p => Vector3.Distance(p, corner) < 0.01f);
    }

    [Fact]
    public void Bumpy_surface_keeps_detail_under_tight_error()
    {
        var g = Grid(20, 1000, (x, y) => 40f * MathF.Sin(x / 80f) * MathF.Cos(y / 80f));
        var loose = MeshSimplifier.Simplify(g, maxError: 30f);
        var tight = MeshSimplifier.Simplify(g, maxError: 0.5f);
        Assert.True(tight.TriangleCount > loose.TriangleCount);
        Assert.True(loose.TriangleCount < g.TriangleCount);
    }

    [Fact]
    public void Lod_nif_round_trips_through_the_reader()
    {
        var g = MeshSimplifier.Simplify(Grid(8, 512), 1f);
        var bytes = LodNifWriter.Write("test_lod_0", [g]);
        var back = NifGeometryReader.Read("test_lod_0.nif", bytes);
        Assert.Single(back.Parts);
        Assert.Equal(g.TriangleCount, back.Parts[0].TriangleCount);
        Assert.Equal("textures\\a.dds", back.Parts[0].Material.Textures[0]);
    }
}
