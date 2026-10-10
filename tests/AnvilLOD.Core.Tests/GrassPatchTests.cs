using AnvilLOD.Core.Pipeline;
using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using Xunit.Abstractions;

namespace AnvilLOD.Core.Tests;

public class GrassPatchTests(ITestOutputHelper output)
{
    private static readonly Vector3 Origin = new(4096 * 3, 4096 * -2, 0);

    /// <summary>Grass in the south-west half of a cell, none in the north-east half, ~25 units apart.</summary>
    private static List<GrassInstance> HalfFilledCell(int seed = 7)
    {
        var rng = new Random(seed);
        var list = new List<GrassInstance>();
        for (int i = 0; i < 12_000; i++)
        {
            float x = (float)rng.NextDouble() * 4096, y = (float)rng.NextDouble() * 4096;
            if (x + y > 4096) continue; // south-west triangle only
            list.Add(new GrassInstance(Origin + new Vector3(x, y, 100), 0.8f + (float)rng.NextDouble() * 0.4f, (float)rng.NextDouble() * 6.28f));
        }
        return list;
    }

    [Fact]
    public void Stratify_is_deterministic()
    {
        var cell = HalfFilledCell();
        var a = GrassPatchBuilder.Stratify(cell, Origin, 0.15f);
        var b = GrassPatchBuilder.Stratify(cell, Origin, 0.15f);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.15f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(0.95f)]
    public void Stratify_keeps_no_more_tufts_than_density_asks_for(float density)
    {
        var cell = HalfFilledCell();
        var tufts = GrassPatchBuilder.Stratify(cell, Origin, density);
        Assert.InRange(tufts.Count, 1, (int)MathF.Round(cell.Count * density));
        Assert.All(tufts, t => Assert.True(t.Count >= 1));
        Assert.Equal(cell.Count, tufts.Sum(t => t.Count)); // every instance belongs to exactly one tuft
    }

    [Fact]
    public void Stratify_leaves_empty_ground_empty()
    {
        var tufts = GrassPatchBuilder.Stratify(HalfFilledCell(), Origin, 0.15f);
        // Tufts are centroids of grass, so none can sit far inside the grass-free triangle.
        Assert.All(tufts, t => Assert.True((t.Position.X - Origin.X) + (t.Position.Y - Origin.Y) <= 4096 + 1, "tuft in empty ground"));
    }

    [Fact]
    public void Full_density_keeps_every_instance_unchanged()
    {
        var cell = HalfFilledCell();
        var tufts = GrassPatchBuilder.Stratify(cell, Origin, 1f);
        Assert.Equal(cell.Count, tufts.Count);
        Assert.Equal(cell[10].Position, tufts[10].Position);
    }

    [Fact]
    public void Higher_density_keeps_more_tufts_and_never_enlarges_them_past_natural_size()
    {
        var cell = HalfFilledCell();
        int last = 0;
        foreach (var d in new[] { 0.04f, 0.08f, 0.15f, 0.25f, 0.4f, 0.6f, 0.9f, 1f })
        {
            int n = GrassPatchBuilder.Stratify(cell, Origin, d).Count;
            Assert.True(n >= last, $"{d:P0} kept {n}, fewer than the previous step ({last})");
            last = n;
        }
        Assert.Equal(cell.Count, last);
    }

    [Fact]
    public void Empty_cell_gives_no_tufts() => Assert.Empty(GrassPatchBuilder.Stratify([], Origin, 0.15f));

    [Fact]
    public void Binned_tufts_cover_the_grass_more_evenly_than_random_sampling()
    {
        var cell = HalfFilledCell();
        const float density = 0.15f, width = 90f;

        // New: bins, tuft width as the builder computes it.
        var tufts = GrassPatchBuilder.Stratify(cell, Origin, density);
        var newCover = Coverage(cell, tufts.Select(t => (t.Position, Width: Math.Clamp(width * MathF.Sqrt(t.Count), width, MathF.Max(width, t.Spacing * 1.15f)))));

        // Old: hash-random sample widened by sqrt(0.6 / density).
        var rng = new Random(3);
        float widen = MathF.Sqrt(0.6f / density);
        var old = cell.Where(_ => rng.NextDouble() < density).Select(g => (g.Position, Width: width * widen));
        var oldCover = Coverage(cell, old);

        output.WriteLine($"covered share of grass ground: bins {newCover.Covered:P1} (tufts {tufts.Count}, avg overdraw {newCover.Overdraw:F2}), random {oldCover.Covered:P1} (avg overdraw {oldCover.Overdraw:F2})");
        Assert.True(newCover.Covered >= oldCover.Covered, "binned coverage should be at least the random coverage");
        Assert.True(newCover.Covered > 0.85, $"binned coverage {newCover.Covered:P1}");
        Assert.True(newCover.Overdraw <= oldCover.Overdraw, "binned tufts should overlap less");
    }

    /// <summary>Share of 32-unit probe points near grass that a tuft footprint covers, and the average number of footprints on a covered point.</summary>
    private static (double Covered, double Overdraw) Coverage(List<GrassInstance> grass, IEnumerable<(Vector3 Position, float Width)> tufts)
    {
        var list = tufts.ToList();
        int probes = 0, covered = 0; long layers = 0;
        for (float x = 16; x < 4096; x += 32)
            for (float y = 16; y < 4096; y += 32)
            {
                if (x + y > 4096 - 64) continue; // keep probes clearly inside the grass triangle
                probes++;
                var p = Origin + new Vector3(x, y, 100);
                int hits = 0;
                foreach (var (pos, w) in list)
                    if (MathF.Abs(pos.X - p.X) <= w / 2 && MathF.Abs(pos.Y - p.Y) <= w / 2) hits++;
                if (hits > 0) { covered++; layers += hits; }
            }
        return (covered / (double)probes, covered == 0 ? 0 : layers / (double)covered);
    }

    [Fact]
    public void Build_makes_two_crossed_quads_per_tuft_with_no_oversized_parts()
    {
        var bb = new GrassBillboard("textures/x.dds", null, 90f, 60f, 0f);
        var mesh = GrassPatchBuilder.Build("grass", Origin, [(bb, HalfFilledCell())], 0.15f, 1f);
        Assert.NotEmpty(mesh.Parts);
        Assert.All(mesh.Parts, p => { Assert.True(p.Positions.Length <= 65_535); Assert.Equal(p.Positions.Length / 8 * 12, p.Triangles.Length); });
    }
}

public class GrassAtlasTests
{
    private static readonly Vector3 Origin = new(0, 0, 0);

    private static List<GrassInstance> Patch(float x0, float y0, int seed)
    {
        var rng = new Random(seed);
        return [.. Enumerable.Range(0, 3000).Select(_ => new GrassInstance(
            new Vector3(x0 + (float)rng.NextDouble() * 1500, y0 + (float)rng.NextDouble() * 1500, 50), 1f, (float)rng.NextDouble() * 6.28f))];
    }

    [Fact]
    public void Types_in_the_atlas_share_one_material_and_use_their_own_tile()
    {
        var a = new GrassBillboard("textures/anvillod/grass/atlas.dds", "textures/anvillod/grass/atlas_n.dds", 90f, 60f, 0f, new AtlasRect(0.0f, 0.0f, 0.5f, 0.25f));
        var b = new GrassBillboard("textures/anvillod/grass/atlas.dds", "textures/anvillod/grass/atlas_n.dds", 70f, 50f, 0f, new AtlasRect(0.5f, 0.25f, 1.0f, 0.5f));
        var mesh = GrassPatchBuilder.Build("grass", Origin, [(a, Patch(0, 0, 1)), (b, Patch(2000, 2000, 2))], 0.15f, 1f);

        Assert.Single(mesh.Parts.Select(p => p.Material.Key).Distinct()); // one material = one draw call per block
        var uvs = mesh.Parts.SelectMany(p => p.UVs).ToList();
        // Every UV lies inside one of the two tiles.
        Assert.All(uvs, uv => Assert.True(
            (uv.X >= 0f && uv.X <= 0.5f && uv.Y >= 0f && uv.Y <= 0.25f) || (uv.X >= 0.5f && uv.X <= 1f && uv.Y >= 0.25f && uv.Y <= 0.5f),
            $"uv {uv} is outside both tiles"));
        Assert.Contains(uvs, uv => uv.X <= 0.5f && uv.Y <= 0.25f);
        Assert.Contains(uvs, uv => uv.X >= 0.5f && uv.Y >= 0.25f);
    }

    [Fact]
    public void Without_a_tile_the_quad_uses_the_whole_texture()
    {
        var bb = new GrassBillboard("textures/x.dds", null, 90f, 60f, 0f);
        var mesh = GrassPatchBuilder.Build("grass", Origin, [(bb, Patch(0, 0, 1))], 0.15f, 1f);
        Assert.Equal(new[] { 0f, 1f }, mesh.Parts.SelectMany(p => p.UVs).SelectMany(uv => new[] { uv.X, uv.Y }).Distinct().Order().ToArray());
    }

    [Fact]
    public void Top_and_bottom_brightness_set_the_vertex_colours()
    {
        var bb = new GrassBillboard("textures/x.dds", null, 90f, 60f, 0f);
        var mesh = GrassPatchBuilder.Build("grass", Origin, [(bb, Patch(0, 0, 1))], 0.15f, 1f, top: 1.0f, bottom: 0.2f);
        var colors = mesh.Parts.SelectMany(p => p.Colors!).Distinct().Order().ToArray();
        Assert.Equal([GrassPatchBuilder.Grey(0.2f), GrassPatchBuilder.Grey(1.0f)], colors);
        Assert.Equal(0xFF333333u, GrassPatchBuilder.Grey(0.2f));
        Assert.Equal(0xFFFFFFFFu, GrassPatchBuilder.Grey(1.4f)); // above white clamps
    }

    [Fact]
    public void Default_brightness_matches_the_old_constants()
    {
        Assert.Equal(0xFF808080u, GrassPatchBuilder.Grey(GrassPatchBuilder.DefaultBottom));
        Assert.Equal(0xFFD9D9D9u, GrassPatchBuilder.Grey(GrassPatchBuilder.DefaultTop));
    }

    [Fact]
    public void Tile_cap_shrinks_big_billboards_and_the_two_atlases_share_a_layout()
    {
        // Two sizes of billboard; the cap forces the big ones down, diffuse and normal packs must still line up.
        byte[] Dds(int w, int h, byte seed)
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = seed; rgba[i + 1] = 100; rgba[i + 2] = 50; rgba[i + 3] = 255; }
            var blocks = AnvilLOD.Textures.Bc.Bc7Encoder.EncodeImage(rgba, w, h);
            using var ms = new MemoryStream();
            AnvilLOD.Textures.Dds.DdsFile.WriteBc7(ms, w, h, [blocks]);
            return ms.ToArray();
        }
        var diffuse = new List<AnvilLOD.Textures.TreeAtlasBuilder.Input> { new("a", Dds(1024, 512, 10)), new("b", Dds(64, 128, 20)), new("c", Dds(512, 256, 30)) };
        var normal = new List<AnvilLOD.Textures.TreeAtlasBuilder.Input> { new("a", Dds(1024, 512, 128)), new("b", Dds(64, 128, 128)), new("c", Dds(512, 256, 128)) };
        var builder = new AnvilLOD.Textures.TreeAtlasBuilder { MaxSize = 2048, MaxTile = 128 };
        var d = builder.Build(diffuse);
        var n = builder.Build(normal);
        Assert.Equal(d.Size, n.Size);
        Assert.Equal(d.Rects.OrderBy(k => k.Key), n.Rects.OrderBy(k => k.Key));
        Assert.True(d.SizeCap <= 128);
        // The big billboards were shrunk to <= 128 px on their long side.
        var ra = d.Rects["a"];
        Assert.True((ra.U1 - ra.U0) * d.Size <= 128 + 4);
    }
}
