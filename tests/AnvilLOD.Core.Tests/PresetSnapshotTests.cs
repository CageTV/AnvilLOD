using System.Text.Json;
using AnvilLOD.Core.Settings;

namespace AnvilLOD.Core.Tests;

public class PresetSnapshotTests
{
    private sealed class Sample
    {
        public string? Output { get; set; }
        public bool Trees { get; set; } = true;
        public int Brightness { get; set; } = 100;
        public double Ratio { get; set; } = 0.5;
        public List<int> Levels { get; set; } = [4, 8];
        public string Global { get; set; } = "keep";
        public string ReadOnly => "x";   // no setter: never part of a snapshot
    }

    private static readonly HashSet<string> Exclude = ["Global"];

    [Fact]
    public void A_snapshot_restores_every_included_property_and_leaves_the_excluded_ones_alone()
    {
        var a = new Sample { Output = "D:\\LOD", Trees = false, Brightness = 60, Ratio = 0.25, Levels = [4, 8, 16, 32], Global = "A" };
        var snap = PresetSnapshot.Capture(a, Exclude);
        Assert.DoesNotContain("Global", snap.Keys);
        Assert.DoesNotContain("ReadOnly", snap.Keys);

        var b = new Sample { Global = "B" };
        PresetSnapshot.Apply(b, snap, Exclude);
        Assert.Equal("D:\\LOD", b.Output);
        Assert.False(b.Trees);
        Assert.Equal(60, b.Brightness);
        Assert.Equal(0.25, b.Ratio);
        Assert.Equal([4, 8, 16, 32], b.Levels);
        Assert.Equal("B", b.Global);
    }

    [Fact]
    public void A_snapshot_survives_being_written_to_json_and_read_back()
    {
        var a = new Sample { Output = null, Brightness = 30 };
        var json = JsonSerializer.Serialize(PresetSnapshot.Capture(a, Exclude));
        var back = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var b = new Sample { Output = "x", Brightness = 100 };
        PresetSnapshot.Apply(b, back, Exclude);
        Assert.Null(b.Output);          // an explicit null is restored, not skipped
        Assert.Equal(30, b.Brightness);
    }

    [Fact]
    public void Missing_or_malformed_values_keep_the_current_setting()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["Brightness"] = JsonSerializer.SerializeToElement("not a number"),
            ["Trees"] = JsonSerializer.SerializeToElement(false),
        };
        var s = new Sample { Brightness = 77, Output = "keep me" };
        PresetSnapshot.Apply(s, values, Exclude);
        Assert.Equal(77, s.Brightness);      // wrong shape: unchanged
        Assert.False(s.Trees);               // good value applied
        Assert.Equal("keep me", s.Output);   // absent: unchanged
    }
}
