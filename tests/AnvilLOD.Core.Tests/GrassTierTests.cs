using System.Numerics;
using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Core.Tests;

/// <summary>Grass density tiers: the game can show 25, 50, 75 or 100% of the grass that was generated.</summary>
public class GrassTierTests
{
    private static readonly Vector3 Origin = new(4096 * 3, 4096 * -2, 0);

    private static List<GrassInstance> Cell(int count, int seed = 11)
    {
        var rng = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(_ => new GrassInstance(
            Origin + new Vector3((float)rng.NextDouble() * 4096, (float)rng.NextDouble() * 4096, 100), 1f, (float)rng.NextDouble() * 6.28f))];
    }

    private static readonly GrassBillboard Bb = new("textures/x.dds", null, 90f, 60f, 0f);

    /// <summary>The base position of every tuft of a part (the middle of its first quad's lower edge), relative to the cell origin.</summary>
    private static IEnumerable<Vector2> Tufts(LodMeshPart p)
    {
        for (int q = 0; q < p.Positions.Length / 8; q++)
        {
            var mid = (p.Positions[q * 8] + p.Positions[q * 8 + 1]) * 0.5f;
            yield return new Vector2(mid.X, mid.Y);
        }
    }

    [Fact]
    public void One_tier_keeps_everything_in_tier_zero_as_before()
    {
        var mesh = GrassPatchBuilder.Build("g", Origin, [(Bb, Cell(5000))], 1f, 1f);
        Assert.All(mesh.Parts, p => Assert.Equal(0, p.Tier));
    }

    [Fact]
    public void Tiers_split_the_tufts_into_equal_parts_and_lose_none()
    {
        var cell = Cell(8000);
        var single = GrassPatchBuilder.Build("g", Origin, [(Bb, cell)], 1f, 1f);
        var tiered = GrassPatchBuilder.Build("g", Origin, [(Bb, cell)], 1f, 1f, tiers: LodLayers.GrassTiers);

        Assert.Equal(single.Parts.Sum(p => p.TriangleCount), tiered.Parts.Sum(p => p.TriangleCount));
        var byTier = tiered.Parts.GroupBy(p => p.Tier).ToDictionary(g => g.Key, g => g.Sum(p => p.Positions.Length / 8));
        Assert.Equal(Enumerable.Range(0, LodLayers.GrassTiers), byTier.Keys.Order());
        Assert.All(byTier.Values, n => Assert.InRange(n, 8000 / LodLayers.GrassTiers - 1, 8000 / LodLayers.GrassTiers + 1));
        Assert.All(tiered.Parts, p => Assert.True(p.Positions.Length <= 65_535));
    }

    [Fact]
    public void Every_tier_is_an_even_spread_so_hiding_tiers_thins_without_bald_squares()
    {
        var tiered = GrassPatchBuilder.Build("g", Origin, [(Bb, Cell(12_000))], 1f, 1f, tiers: LodLayers.GrassTiers);
        // An 8x8 grid over the cell: each square holds ~190 tufts, so each tier should hold about a quarter of them in every square.
        const int N = 8;
        var perTier = Enumerable.Range(0, LodLayers.GrassTiers).Select(_ => new int[N, N]).ToArray();
        foreach (var p in tiered.Parts)
            foreach (var t in Tufts(p))
                perTier[p.Tier][Math.Clamp((int)(t.X / 4096 * N), 0, N - 1), Math.Clamp((int)(t.Y / 4096 * N), 0, N - 1)]++;
        for (int x = 0; x < N; x++)
            for (int y = 0; y < N; y++)
            {
                int total = perTier.Sum(a => a[x, y]);
                Assert.True(total > 100, $"square {x},{y} has only {total} tufts");
                for (int t = 0; t < LodLayers.GrassTiers; t++)
                    Assert.InRange(perTier[t][x, y] / (double)total, 0.18, 0.32);
            }
    }

    [Fact]
    public void Tiers_are_deterministic()
    {
        var cell = Cell(3000);
        var a = GrassPatchBuilder.Build("g", Origin, [(Bb, cell)], 1f, 1f, tiers: 4);
        var b = GrassPatchBuilder.Build("g", Origin, [(Bb, cell)], 1f, 1f, tiers: 4);
        Assert.Equal(a.Parts.Select(p => (p.Tier, p.Positions.Length)), b.Parts.Select(p => (p.Tier, p.Positions.Length)));
        Assert.Equal(a.Parts[0].Positions, b.Parts[0].Positions);
    }

    [Fact]
    public void Layer_node_names_carry_the_tier_after_the_level_and_tier_zero_keeps_the_old_name()
    {
        Assert.Equal("AL:grass:4", LodLayers.NodeName(LodLayers.Grass, 4));
        Assert.Equal("AL:grass:4", LodLayers.NodeName(LodLayers.Grass, 4, 0));
        Assert.Equal("AL:grass:16:2", LodLayers.NodeName(LodLayers.Grass, 16, 2));
        Assert.Null(LodLayers.NodeName(LodLayers.Objects, 4, 3));
    }

    [Fact]
    public void A_block_gets_one_named_node_per_grass_tier()
    {
        var mat = new LodMaterial(["textures\\g.dds", "", "", "", "", "", "", "", ""], 0x80000300, 0x5, 3, true, GrassPatchBuilder.AlphaTestFlags, GrassPatchBuilder.AlphaThreshold, Vector3.Zero, 1f);
        LodMeshPart Part(int tier) => new()
        {
            Material = mat,
            Positions = [Vector3.Zero, new Vector3(10, 0, 0), new Vector3(0, 10, 0)],
            UVs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
            Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Triangles = [0, 1, 2],
            Tier = tier,
        };
        var mesh = new LodMesh { Path = "g", Parts = [Part(0), Part(1), Part(2)] };
        var r = new LodReference("1:A.esp", "2:A.esp", null, "T", "A.esp", new Vector3(100, 100, 0), Vector3.Zero, 1f,
            new LodMeshSet("g", null, null, null), LodReferenceFlags.None);

        var res = BtoBuilder.Build(new QuadKey("T", LodLevel.Lod4, 0, 0), [r], _ => mesh, categorize: _ => LodLayers.Grass);
        var text = Encoding.ASCII.GetString(res.Bytes);
        Assert.Equal(3, res.Shapes);
        Assert.Contains("AL:grass:4", text);
        Assert.Contains("AL:grass:4:1", text);
        Assert.Contains("AL:grass:4:2", text);
        Assert.DoesNotContain("AL:grass:4:3", text);
    }
}
