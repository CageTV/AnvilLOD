using AnvilLOD.Plugins;
using Mutagen.Bethesda;

namespace AnvilLOD.Core.Tests;

/// <summary>Things on other people's load orders that used to stop a whole run with a stack trace.</summary>
public class RobustnessTests
{
    [Fact]
    public void An_archive_that_cannot_be_read_is_skipped_not_fatal()
    {
        var dir = Directory.CreateTempSubdirectory("anvil-bsa-").FullName;
        try
        {
            var bad = Path.Combine(dir, "Syerscote.bsa");
            File.WriteAllBytes(bad, new byte[512]);               // not a BSA at all
            var index = AssetIndex.Create(GameRelease.SkyrimSE, [bad], _ => [], _ => null);
            Assert.Equal(0, index.ArchiveCount);
            Assert.Equal(0, index.ArchivedFileCount);
            var skipped = Assert.Single(index.SkippedArchives);
            Assert.StartsWith("Syerscote.bsa: ", skipped);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void No_archives_means_nothing_skipped()
    {
        var index = AssetIndex.Create(GameRelease.SkyrimSE, [], _ => [], _ => null);
        Assert.Empty(index.SkippedArchives);
    }

    [Theory]
    [InlineData("Skyrim")]          // no extension: Mutagen throws "Could not construct ModKey"
    [InlineData("")]
    [InlineData("   ")]
    public void A_season_ini_plugin_without_a_plugin_extension_is_skipped(string plugin)
        => Assert.Null(SeasonalLod.PluginForm(plugin, 0x1234));

    [Theory]
    [InlineData("Skyrim.esm")]
    [InlineData("Seasonal Landscapes.esp")]
    [InlineData(" Unfrozen.esl ")]
    public void A_valid_plugin_name_gives_a_form_key(string plugin)
    {
        var fk = SeasonalLod.PluginForm(plugin, 0x1234);
        Assert.NotNull(fk);
        Assert.Equal(0x1234u, fk!.Value.ID);
    }
}
