using System.Numerics;
using AnvilLOD.Meshes;
using AnvilLOD.Plugins.Authoring;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class BillboardNormalTests
{
    // A flat card in the XZ plane (the camera looks along +Y) whose corner normals lean away from the middle:
    // top corners up, bottom corners down, left corners left, right corners right, all towards the viewer.
    private static LodMesh Card()
    {
        static Vector3 N(float x, float z) => Vector3.Normalize(new Vector3(x, -0.5f, z));
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(400, 0, 0), new Vector3(0, 0, 400), new Vector3(400, 0, 400) };
        var nrm = new[] { N(-0.8f, -0.8f), N(0.8f, -0.8f), N(-0.8f, 0.8f), N(0.8f, 0.8f) };   // 0,1 bottom; 2,3 top
        var mat = new LodMaterial(["textures\\x.dds"], 0, 0, 3, false, 0, 0, Vector3.Zero, 1f);
        var part = new LodMeshPart
        {
            Material = mat, Positions = pos, UVs = [new(0, 1), new(1, 1), new(0, 0), new(1, 0)], Normals = nrm,
            Tangents = pos.Select(_ => Vector3.UnitX).ToArray(), Bitangents = pos.Select(_ => Vector3.UnitZ).ToArray(),
            Colors = null, Triangles = [0, 1, 2, 1, 3, 2],
        };
        return new LodMesh { Path = "card.nif", Parts = [part] };
    }

    [Fact]
    public void Normal_map_follows_the_model_in_TexGens_DirectX_orientation()
    {
        var img = BillboardRenderer.Render(Card(), _ => null, maxPixels: 64);
        Assert.NotNull(img);
        var n = img!.NormalRgba;
        Assert.NotNull(n);
        Assert.Equal(img.Rgba.Length, n!.Length);

        int W = img.Width, H = img.Height;
        (int R, int G, int B) At(double fx, double fy) { int i = ((int)(fy * (H - 1)) * W + (int)(fx * (W - 1))) * 4; return (n[i], n[i + 1], n[i + 2]); }

        var top = At(0.5, 0.1); var bottom = At(0.5, 0.9); var left = At(0.1, 0.5); var right = At(0.9, 0.5);
        Assert.True(top.G < 110, $"top green {top.G}");          // normal leaning up = low green (green points down in the image)
        Assert.True(bottom.G > 146, $"bottom green {bottom.G}");
        Assert.True(left.R < 110, $"left red {left.R}");
        Assert.True(right.R > 146, $"right red {right.R}");
        Assert.True(top.B > 128 && bottom.B > 128 && left.B > 128 && right.B > 128);   // always towards the viewer
    }

    [Fact]
    public void Every_normal_in_the_map_is_a_unit_vector_towards_the_viewer()
    {
        var n = BillboardRenderer.Render(Card(), _ => null, maxPixels: 64)!.NormalRgba!;
        for (int i = 0; i < n.Length; i += 4)
        {
            float x = (n[i] - 128f) / 127f, y = (n[i + 1] - 128f) / 127f, z = (n[i + 2] - 128f) / 127f;
            Assert.InRange(MathF.Sqrt(x * x + y * y + z * z), 0.95f, 1.05f);
            Assert.True(z > 0f);
        }
    }

    [Fact]
    public void Renormalize_turns_a_short_or_empty_vector_into_a_usable_one()
    {
        var px = new byte[] { 128, 128, 128, 255 };   // a zero vector (no length to scale)
        BillboardRenderer.Renormalize(px, 0);
        Assert.Equal(new byte[] { 128, 128, 255 }, px[..3]);
    }
}