using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class ArchiveOrderTests
{
    [Fact]
    public void Base_game_archives_come_first_then_each_plugins_archives_in_load_order()
    {
        var order = ArchiveOrder.LowToHigh(
            [@"D:\Data\ModB - Textures.bsa", @"D:\Data\Skyrim - Textures0.bsa", @"D:\Data\ModA.bsa", @"D:\Data\ModB.bsa", @"D:\Data\Skyrim - Meshes0.bsa"],
            ["Skyrim.esm", "ModA.esp", "ModB.esp"]);

        Assert.Equal(
            [@"D:\Data\Skyrim - Meshes0.bsa", @"D:\Data\Skyrim - Textures0.bsa", @"D:\Data\ModA.bsa", @"D:\Data\ModB - Textures.bsa", @"D:\Data\ModB.bsa"],
            order);
    }

    [Fact]
    public void A_plugin_name_must_end_at_a_word_boundary_and_the_longest_name_wins()
    {
        // "Skyrim - Textures0.bsa" belongs to Skyrim.esm; "SkyrimExtra.bsa" must not be taken by it.
        var order = ArchiveOrder.LowToHigh(
            [@"Data\SkyrimExtra.bsa", @"Data\Skyrim - Textures0.bsa"],
            ["Skyrim.esm", "SkyrimExtra.esp"]);
        Assert.Equal([@"Data\Skyrim - Textures0.bsa", @"Data\SkyrimExtra.bsa"], order);
    }

    [Fact]
    public void Archives_without_a_plugin_sort_with_the_base_game_by_name_and_nothing_throws_on_empty_input()
    {
        Assert.Equal([@"Data\a.bsa", @"Data\b.bsa"], ArchiveOrder.LowToHigh([@"Data\b.bsa", @"Data\a.bsa"], []));
        Assert.Empty(ArchiveOrder.LowToHigh([], ["Skyrim.esm"]));
    }
}
