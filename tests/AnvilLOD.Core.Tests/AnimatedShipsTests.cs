using AnvilLOD.Core.Lod;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class AnimatedShipsTests
{
    private static LodRules Load(bool animatedShips) =>
        LodRules.Load(null, new Dictionary<string, Func<string>>(), [], LodPreset.High, "SSE", new LodRuleOptions(AnimatedShips: animatedShips));

    [Fact]
    public void With_the_option_the_sailing_ships_are_far_grid_objects()
    {
        var rule = Load(true).Match(null, null, "meshes\\clutter\\vicn\\animatedship\\distant\\shiplongboat01.nif");
        Assert.True(rule.IsGridObject);
        Assert.Equal(DynamicGrid.Far, rule.Grid);
    }

    [Theory]
    [InlineData("meshes\\clutter\\vicn\\animatedship\\distant\\lod\\shiplongboatlod01.nif")]   // the mod's own LOD stand-ins stay out
    [InlineData("meshes\\clutter\\vicn\\animatedship\\narrowpath\\shiplongboat01.nif")]        // the near-the-docks route is not a distant ship
    public void Other_models_of_the_mod_are_not_grid_objects(string model)
        => Assert.False(Load(true).Match(null, null, model).IsGridObject);

    [Fact]
    public void Without_the_option_the_rules_are_untouched()
        => Assert.False(Load(false).Match(null, null, "meshes\\clutter\\vicn\\animatedship\\distant\\shiplongboat01.nif").IsGridObject);

    [Theory]
    [InlineData("clutter\\vicn\\animatedship\\distant\\shiplongboat01.nif", true)]
    [InlineData("Clutter\\Vicn\\AnimatedShip\\Distant\\ShipLargeDaintySload01.nif", true)]
    [InlineData("clutter\\vicn\\animatedship\\narrowpath\\shiplongboat01.nif", false)]
    [InlineData("dungeons\\ship\\shiplongboat01.nif", false)]
    public void Only_the_distant_models_are_sailing_ships(string model, bool expected)
        => Assert.Equal(expected, NativeGridObjects.IsAnimatedShip(model.ToLowerInvariant()));
}
