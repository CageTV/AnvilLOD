using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;

namespace AnvilLOD.Core.Tests;

public class ShipLodTests
{
    [Theory]
    [InlineData("meshes\\dungeons\\ship\\shiplongboat01.nif")]
    [InlineData("Meshes/Dungeons/Ship/ShipLargeDaintySload01.nif")]
    [InlineData("meshes\\dungeons\\ship\\shiprowboat01.nif")]
    [InlineData("meshes\\dungeons\\ship\\shiplongboatwreck02.nif")]
    [InlineData("meshes\\dungeons\\ship\\shiplargekatariah01.nif")]
    [InlineData("meshes/bstamriel/dungeons/ships/nordships/shipnordgreatship.nif")]
    [InlineData("Meshes/BSTamriel/dungeons/Ships/redguardships/corsairships/shipredguardLargeshipCorsair01_docked.nif")]
    [InlineData("meshes/bstamriel/dungeons/ships/nordships/shipnordrowboat.nif")]
    [InlineData("meshes\\_pgpatcher_dups\\3\\dungeons\\ship\\shiplongboat01.nif")]
    public void Whole_ships_are_recognised(string path) => Assert.True(ShipLodModels.IsShip(path));

    [Theory]
    [InlineData("meshes\\dungeons\\ship\\shiplargeinside01.nif")]
    [InlineData("meshes\\dungeons\\ship\\shipdoor01.nif")]
    [InlineData("meshes/bstamriel/dungeons/ships/redguardships/interiors/shipredguardLargeshipInterior01.nif")]
    [InlineData("meshes/bstamriel/dungeons/ships/redguardships/interiors/ShipRedguardDoor01.nif")]
    [InlineData("meshes/bstamriel/dungeons/ships/breticships/clutter/shipbreticropecoil01.nif")]
    [InlineData("meshes\\dungeons\\ship\\shipkatariahwoodbeam01.nif")]
    [InlineData("meshes\\architecture\\whiterun\\wrhouse01.nif")]
    [InlineData("")]
    public void Interior_pieces_and_other_models_are_not(string path) => Assert.False(ShipLodModels.IsShip(path));

    [Theory]
    [InlineData("meshes\\clutter\\vicn\\animatedship\\updown\\shipwrecklarge02.nif", true)]
    [InlineData("Meshes/Clutter/Vicn/AnimatedShip/UpDown/shiplongboatwreck01.nif", true)]
    [InlineData("meshes\\clutter\\vicn\\animatedship\\distant\\shiplongboat01.nif", false)]   // sails around: not a static ship
    [InlineData("meshes\\clutter\\vicn\\animatedship\\narrowpath\\shipwrecklarge02.nif", false)]
    [InlineData("meshes\\dungeons\\ship\\shipwrecklarge01.nif", false)]
    public void Only_the_wrecks_that_bob_in_place_are_taken_for_static_ships(string path, bool expected)
        => Assert.Equal(expected, ShipLodModels.IsBobbingWreck(path));

    [Fact]
    public void A_ship_mesh_path_remembers_the_stock_mesh_it_replaced()
    {
        var path = ShipLodModels.MeshPath("meshes/dungeons/ship/shiplongboat01.nif", 1, "meshes/lod/ships/shiplongboat01_lod.nif");
        Assert.Equal("meshes/dungeons/ship/shiplongboat01.nif", ShipLodModels.ModelOf(path));
        Assert.Equal(1, ShipLodModels.LevelOf(path));
        Assert.Equal("meshes/lod/ships/shiplongboat01_lod.nif", ShipLodModels.StockMesh(path));
        Assert.Equal("meshes/lod/x.nif", ShipLodModels.StockMesh("meshes/lod/x.nif"));
        Assert.Null(ShipLodModels.ModelOf("meshes/lod/x.nif"));
    }

    [Fact]
    public void Ship_meshes_are_sunk_a_little_so_the_real_deck_wins_where_both_are_drawn()
    {
        var ship = ShipLodModels.MeshPath("meshes/dungeons/ship/shiplongboat01.nif", 0, "meshes/lod/ships/shiplongboat01_lod.nif");
        Assert.Equal(LodInset.DefaultShip, LodInset.For(ship, 0f, 0f, LodInset.DefaultShip));
        Assert.Equal(0f, LodInset.For(ship, 6f, 4f));                 // option off: no inset
        Assert.Equal(0f, LodInset.For("meshes/lod/ships/x_lod.nif", 0f, 0f, LodInset.DefaultShip)); // stock meshes are left alone
    }

    private static readonly LodMaterial Wood = new(["textures\\wood.dds", "textures\\wood_n.dds", "", "", "", "", "", "", ""], 0x80000300u, 5u, 3, false, 0, 0, Vector3.Zero, 1f);

    /// <summary>A square grid of n x n cells, size units wide, with the given height ripple (0 = flat, which simplifies away).</summary>
    private static LodMeshPart Grid(int n, float size, float ripple, float z = 0, LodMaterial? material = null)
    {
        int side = n + 1;
        var pos = new Vector3[side * side];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
                pos[y * side + x] = new Vector3(x * size / n, y * size / n, z + ((x + y) % 2 == 0 ? ripple : -ripple));
        var tris = new List<ushort>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                ushort a = (ushort)(y * side + x), b = (ushort)(a + 1), c = (ushort)(a + side), d = (ushort)(c + 1);
                tris.AddRange([a, b, c, b, d, c]);
            }
        return new LodMeshPart
        {
            Name = "grid", Material = material ?? Wood, Positions = pos,
            UVs = pos.Select(p => new Vector2(p.X / size, p.Y / size)).ToArray(),
            Normals = pos.Select(_ => Vector3.UnitZ).ToArray(),
            Tangents = pos.Select(_ => Vector3.UnitX).ToArray(),
            Bitangents = pos.Select(_ => Vector3.UnitY).ToArray(),
            Triangles = [.. tris],
        };
    }

    private static LodMesh Ship(params LodMeshPart[] parts) => new() { Path = "ship.nif", Parts = parts };

    [Fact]
    public void A_generous_budget_keeps_every_part()
    {
        var b = new ShipLodBuilder(Ship(Grid(20, 2000, 0), Grid(20, 1500, 40, z: 100)));
        var r = b.Fit(100_000, 16f);
        Assert.Equal(2, r.Parts.Count);
        Assert.Equal(0, r.DroppedParts);
        Assert.False(r.OverBudget);
    }

    private static readonly LodMaterial Shields = Wood with { Textures = ["textures\\whiterun\\wrshields.dds", "", "", "", "", "", "", "", ""] };
    private static readonly LodMaterial Cloth = Wood with { Textures = ["textures\\ships\\shiprope01.dds", "", "", "", "", "", "", "", ""] };

    [Fact]
    public void A_tight_budget_gives_up_the_decoration_before_the_smooth_hull()
    {
        var hull = Grid(20, 2000, 0);                                  // flat: collapses to a handful of triangles
        var decoration = Grid(30, 1500, 60, z: 100, material: Shields); // rippled: can't be reduced much without losing the ripple
        var b = new ShipLodBuilder(Ship(hull, decoration));
        var r = b.Fit(60, 16f);
        Assert.False(r.OverBudget);
        Assert.True(r.Triangles <= 60);
        Assert.Single(r.Parts);                       // the smooth hull stays
        Assert.Equal(1, r.DroppedParts);
    }

    [Fact]
    public void Rigging_and_sails_stay_when_the_ship_is_only_a_little_over_budget()
    {
        var hull = Grid(20, 2000, 0);
        var sails = Grid(30, 1500, 60, z: 100, material: Cloth);
        var b = new ShipLodBuilder(Ship(hull, sails));
        int coarsest = b.Fit(100_000, 16f).Triangles;   // find roughly what the sails cost at their finest
        var r = b.Fit(coarsest / 2, 16f);                // over budget, but by less than the slack
        Assert.Equal(2, r.Parts.Count);                  // nothing is thrown away
        Assert.Equal(0, r.DroppedParts);
        var far = b.Fit(10, 16f);                        // far over budget: now the rigging may go
        Assert.Single(far.Parts);
    }

    [Fact]
    public void A_part_lying_exactly_on_another_is_lifted_off_it_and_fitted_no_coarser_than_32_units()
    {
        var opaque = Grid(10, 2000, 0);
        var paint = Grid(10, 2000, 0, material: Wood with { HasAlpha = true, AlphaFlags = 0x12EC, AlphaThreshold = 34 });
        var b = new ShipLodBuilder(Ship(opaque, paint));
        var r = b.Fit(100_000, 16f);
        Assert.Equal(2, r.Parts.Count);
        var zs = r.Parts.Select(p => p.Positions.Average(v => v.Z)).OrderBy(z => z).ToArray();
        Assert.True(zs[1] - zs[0] >= 2f, "the overlay must sit above the surface under it");
        // dropped from far away
        Assert.Single(b.Fit(100_000, 16f, dropOverlays: true).Parts);
    }

    [Fact]
    public void Parts_below_the_minimum_size_are_not_drawn_and_levels_can_be_fitted_in_turn()
    {
        var b = new ShipLodBuilder(Ship(Grid(10, 2000, 0), Grid(4, 10, 0)));
        Assert.Equal(2, b.Fit(100_000, 5f).Parts.Count);
        var coarse = b.Fit(100_000, 48f);
        Assert.Single(coarse.Parts);
        Assert.Equal(1, coarse.DroppedParts);
        Assert.Equal(2, b.Fit(100_000, 5f).Parts.Count); // asking again starts from scratch
    }
}
