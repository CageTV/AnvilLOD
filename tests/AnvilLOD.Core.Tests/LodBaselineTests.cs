using System.Numerics;
using AnvilLOD.Core.Incremental;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Tests;

public class LodBaselineTests
{
    private static readonly QuadKey Q1 = new("Tamriel", LodLevel.Lod4, 0, 0);
    private static readonly QuadKey Q2 = new("Tamriel", LodLevel.Lod4, 4, 0);

    private static LodReference Ref(string id, string plugin = "Skyrim.esm", float x = 0, string mesh = "a_lod.nif") =>
        new($"{id}:{plugin}", "000001:Skyrim.esm", null, "Tamriel", plugin, new Vector3(x, 0, 0), Vector3.Zero, 1f,
            new LodMeshSet(mesh, mesh, null, null), LodReferenceFlags.None);

    private static (IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>>, IReadOnlyDictionary<QuadKey, string>) State(
        params (QuadKey Q, string Hash, LodReference[] Refs)[] blocks) =>
        (blocks.ToDictionary(b => b.Q, b => (IReadOnlyList<LodReference>)b.Refs),
         blocks.ToDictionary(b => b.Q, b => b.Hash));

    private static LodBaseline Snap(params (QuadKey, string, LodReference[])[] blocks)
    {
        var (q, h) = State(blocks);
        return LodBaseline.Capture(q, h, "opts");
    }

    [Fact]
    public void Same_state_reports_nothing()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]), (Q2, "h2", [Ref("000011")]));
        var (q, h) = State((Q1, "h1", [Ref("000010")]), (Q2, "h2", [Ref("000011")]));
        var r = b.Diff(q, h, ["Tamriel"], "opts");
        Assert.Empty(r.Stale);
        Assert.Equal(2, r.Unchanged);
        Assert.Null(r.OptionsWarning);
    }

    [Fact]
    public void Added_reference_names_the_plugin_that_added_it()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]));
        var (q, h) = State((Q1, "h1x", [Ref("000010"), Ref("000020", "NewWorld.esp")]));
        var d = Assert.Single(b.Diff(q, h, ["Tamriel"], "opts").Stale);
        Assert.Equal(BlockChange.Changed, d.Kind);
        Assert.Equal(1, d.RefsAdded);
        Assert.Equal(0, d.RefsRemoved);
        Assert.Equal(1, d.ByPlugin["NewWorld.esp"]);
        Assert.False(d.AssetOnly);
    }

    [Fact]
    public void Removed_and_moved_references_are_counted_and_attributed()
    {
        var b = Snap((Q1, "h1", [Ref("000010", "Old.esp"), Ref("000011", "Mover.esp", x: 0)]));
        var (q, h) = State((Q1, "h1x", [Ref("000011", "Mover.esp", x: 50)]));
        var d = Assert.Single(b.Diff(q, h, ["Tamriel"], "opts").Stale);
        Assert.Equal(1, d.RefsRemoved);
        Assert.Equal(1, d.RefsMoved);
        Assert.Equal(1, d.RemovedByPlugin["Old.esp"]);
        Assert.Equal(1, d.ByPlugin["Mover.esp"]);
    }

    [Fact]
    public void Same_references_with_a_new_fingerprint_is_an_asset_change()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]));
        var (q, h) = State((Q1, "h2", [Ref("000010")]));
        var d = Assert.Single(b.Diff(q, h, ["Tamriel"], "opts").Stale);
        Assert.True(d.AssetOnly);
    }

    [Fact]
    public void Ignore_assets_counts_a_support_app_rewrite_as_unchanged_but_still_reports_object_changes()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]), (Q2, "h2", [Ref("000011")]));
        // Q1: same objects, new fingerprint (a tool rewrote a mesh). Q2: a different position, a real change.
        var (q, h) = State((Q1, "h1x", [Ref("000010")]), (Q2, "h2x", [Ref("000011", x: 500)]));

        var strict = b.Diff(q, h, ["Tamriel"], "opts");
        Assert.Equal(2, strict.Stale.Count);

        var lenient = b.Diff(q, h, ["Tamriel"], "opts", ignoreAssetOnly: true);
        var only = Assert.Single(lenient.Stale);
        Assert.Equal(Q2.RelativePath, only.RelativePath);
        Assert.False(only.AssetOnly);
        Assert.Equal(1, lenient.AssetOnlyIgnored);
        Assert.Equal(1, lenient.Unchanged);
    }

    [Fact]
    public void Content_baseline_says_what_changed_when_the_objects_did_not()
    {
        var refs = new[] { Ref("000010") };
        var quads = new Dictionary<QuadKey, IReadOnlyList<LodReference>> { [Q1] = refs };
        var hashes1 = new Dictionary<QuadKey, string> { [Q1] = "h1" };
        var comp1 = new BaselineComponents(new Dictionary<QuadKey, string> { [Q1] = "a1" }, new Dictionary<string, string> { ["Tamriel"] = "t1" },
            new Dictionary<string, string> { ["Tamriel"] = "w1" }, "set", ContentHashed: true);
        var b = LodBaseline.Capture(quads, hashes1, "opts", components: comp1);
        Assert.Equal("content", b.Hashing);

        // only the files changed
        var f = b.Diff(quads, new Dictionary<QuadKey, string> { [Q1] = "h2" }, ["Tamriel"], "opts",
            nowParts: comp1 with { QuadAssets = new Dictionary<QuadKey, string> { [Q1] = "a2" } });
        Assert.Equal(AssetChange.Files, Assert.Single(f.Stale).AssetReasons);

        // only the terrain changed
        var t = b.Diff(quads, new Dictionary<QuadKey, string> { [Q1] = "h2" }, ["Tamriel"], "opts",
            nowParts: comp1 with { Terrain = new Dictionary<string, string> { ["Tamriel"] = "t2" } });
        Assert.Equal(AssetChange.Terrain, Assert.Single(t.Stale).AssetReasons);

        // terrain and water together
        var tw = b.Diff(quads, new Dictionary<QuadKey, string> { [Q1] = "h2" }, ["Tamriel"], "opts",
            nowParts: comp1 with { Terrain = new Dictionary<string, string> { ["Tamriel"] = "t2" }, Water = new Dictionary<string, string> { ["Tamriel"] = "w2" } });
        Assert.Equal(AssetChange.Terrain | AssetChange.Water, Assert.Single(tw.Stale).AssetReasons);
        Assert.Equal(1, tw.AssetBreakdown()[AssetChange.Terrain]);

        // an older (file-time) baseline cannot say
        var old = LodBaseline.Capture(quads, hashes1, "opts");
        Assert.Equal("mtime", old.Hashing);
        var u = old.Diff(quads, new Dictionary<QuadKey, string> { [Q1] = "h2" }, ["Tamriel"], "opts", nowParts: comp1);
        Assert.Equal(AssetChange.Unknown, Assert.Single(u.Stale).AssetReasons);
    }

    [Fact]
    public void New_and_removed_blocks_are_reported_but_only_for_scanned_worldspaces()
    {
        var other = new QuadKey("Falskaar", LodLevel.Lod4, 0, 0);
        var b = Snap((Q1, "h1", [Ref("000010")]), (other, "hf", [Ref("000030")]));
        var (q, h) = State((Q2, "h2", [Ref("000011")]));

        var scoped = b.Diff(q, h, ["Tamriel"], "opts");
        Assert.Contains(scoped.Stale, d => d.Kind == BlockChange.New && d.RelativePath == Q2.RelativePath);
        Assert.Contains(scoped.Stale, d => d.Kind == BlockChange.Removed && d.RelativePath == Q1.RelativePath);
        Assert.DoesNotContain(scoped.Stale, d => d.Worldspace == "Falskaar");   // not scanned: not "removed"

        var both = b.Diff(q, h, ["Tamriel", "Falskaar"], "opts");
        Assert.Contains(both.Stale, d => d.Worldspace == "Falskaar" && d.Kind == BlockChange.Removed);
    }

    [Fact]
    public void Different_scan_options_warn()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]));
        var (q, h) = State((Q1, "h1", [Ref("000010")]));
        Assert.NotNull(b.Diff(q, h, ["Tamriel"], "other").OptionsWarning);
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var b = Snap((Q1, "h1", [Ref("000010", "A.esp")]));
        var path = Path.Combine(Path.GetTempPath(), $"anvil-baseline-{Guid.NewGuid():N}.json.gz");
        try
        {
            b.Save(path);
            var back = LodBaseline.Load(path);
            var (q, h) = State((Q1, "h1", [Ref("000010", "A.esp")]));
            Assert.Empty(back.Diff(q, h, ["Tamriel"], "opts").Stale);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Csv_lists_every_stale_block()
    {
        var b = Snap((Q1, "h1", [Ref("000010")]));
        var (q, h) = State((Q1, "h9", [Ref("000010"), Ref("000020", "New.esp")]));
        var csv = b.Diff(q, h, ["Tamriel"], "opts").ToCsv();
        Assert.Contains(Q1.RelativePath, csv);
        Assert.Contains("New.esp (1)", csv);
    }
}
