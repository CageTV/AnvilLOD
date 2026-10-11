using System.Numerics;
using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class ParentCopyTests
{
    private const string WhiterunIni = """
        ; parent world
        [Tamriel]
        ScanChild=1
        ScanParent=1
        NoCellsWithNAVM=0
        NoScanIfPluginExists=Open Cities Skyrim.esp
        NoScanParentIfPluginExists=SkyrimFantasyOverhaulWhiterun-Interiors.esp
        IgnoreParentEDID=cwcatapult, Shrub
        Bounds=4,-3,7,1
        """;

    private static IReadOnlySet<string> Loaded(params string[] plugins) => new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parses_the_parent_copy_part_of_a_childworld_ini()
    {
        var w = ChildWorldCopies.ParseParentCopy("WhiterunWorld", WhiterunIni, Loaded("Skyrim.esm"), "x.ini")!;
        Assert.Equal("Tamriel", w.Parent);
        Assert.Equal(new CellRect(4, -3, 7, 1), w.Bounds);
        Assert.Null(w.BoundsIgnore);
        Assert.False(w.NoCellsWithNavmesh);
        Assert.True(w.IgnoresEditorId("CWCatapult01"));
        Assert.True(w.IgnoresEditorId("TreeShrub02"));
        Assert.False(w.IgnoresEditorId("RockL01"));
    }

    [Fact]
    public void Parent_copy_is_off_without_ScanParent_or_with_a_blocking_plugin()
    {
        Assert.Null(ChildWorldCopies.ParseParentCopy("MarkarthWorld", WhiterunIni.Replace("ScanParent=1", "ScanParent=0"), Loaded(), "x.ini"));
        Assert.Null(ChildWorldCopies.ParseParentCopy("WhiterunWorld", WhiterunIni, Loaded("Open Cities Skyrim.esp"), "x.ini"));
        Assert.Null(ChildWorldCopies.ParseParentCopy("WhiterunWorld", WhiterunIni, Loaded("SkyrimFantasyOverhaulWhiterun-Interiors.esp"), "x.ini"));
    }

    [Fact]
    public void Region_is_the_bounds_grown_by_five_cells_minus_the_ignored_area()
    {
        var whiterun = ChildWorldCopies.ParseParentCopy("WhiterunWorld", WhiterunIni, Loaded(), "x.ini")!;
        Assert.True(whiterun.Covers(-1, -8));    // 5 cells west of x=4 and 5 south of y=-3
        Assert.True(whiterun.Covers(12, 6));     // 5 east of x=7, 5 north of y=1
        Assert.False(whiterun.Covers(-2, 0));
        Assert.False(whiterun.Covers(13, 0));

        // Dragonsreach has BoundsIgnore instead (the ground Whiterun's own copies already cover).
        var dragonsreach = ChildWorldCopies.ParseParentCopy("WhiterunDragonsreachWorld",
            WhiterunIni.Replace("Bounds=4,-3,7,1", "BoundsIgnore=-128,-128,7,1"), Loaded(), "y.ini")!;
        Assert.Null(dragonsreach.Bounds);
        Assert.False(dragonsreach.Covers(5, 0));   // inside the ignored area
        Assert.True(dragonsreach.Covers(9, 0));
        Assert.True(dragonsreach.Covers(5, 3));
    }

    [Fact]
    public void CellRect_reads_four_numbers_and_ignores_a_trailing_height()
    {
        Assert.Equal(new CellRect(-17, 24, -11, 27), CellRect.Parse("-17,24,-11,27,-10000"));
        Assert.Equal(new CellRect(1, 2, 3, 4), CellRect.Parse("3,4,1,2"));   // corners in either order
        Assert.Null(CellRect.Parse("1,2,3"));
        Assert.Null(CellRect.Parse("a,b,c,d"));
        Assert.Null(CellRect.Parse(null));
    }

    [Fact]
    public void Copies_file_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil-copies-" + Guid.NewGuid().ToString("N"));
        try
        {
            var copies = new List<ParentCopy>
            {
                new("WhiterunWorld", "Skyrim.esm", 0x1A26F, "Skyrim.esm", 0x123456, "RockL01", @"Meshes\Landscape\Rocks\RockL01.nif",
                    new Vector3(1, 2, 3), new Vector3(0.1f, 0.2f, 0.3f), 1.5f, 6, -1, true, false),
                new("WhiterunWorld", "Skyrim.esm", 0x1A26F, "Skyrim.esm", 0x123457, "RockPileM02Snow", @"meshes\landscape\rocks\rockpilem02.nif",
                    new Vector3(4, 5, 6), Vector3.Zero, 1f, 7, -1, false, true),
            };
            Assert.Equal(2, ParentCopyWriter.Write(dir, copies));

            var b = File.ReadAllBytes(Path.Combine(dir, ParentCopyWriter.RelativePath));
            using var r = new BinaryReader(new MemoryStream(b));
            Assert.Equal("ALCP", Encoding.ASCII.GetString(r.ReadBytes(4)));
            Assert.Equal(1u, r.ReadUInt32());
            var strings = new List<string>();
            for (uint i = 0, n = r.ReadUInt32(); i < n; i++) strings.Add(Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16())));
            Assert.Equal(["Skyrim.esm", @"landscape\rocks\rockl01.nif", @"landscape\rocks\rockpilem02.nif"], strings);   // mesh paths are relative to Data\meshes
            Assert.Equal(2u, r.ReadUInt32());
            Assert.Equal(0u, r.ReadUInt32());                    // world plugin
            Assert.Equal(0x1A26Fu, r.ReadUInt32());              // world local id
            Assert.Equal(1u, r.ReadUInt32());                    // model
            Assert.Equal(new Vector3(1, 2, 3), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Assert.Equal(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            Assert.Equal(1.5f, r.ReadSingle());
            Assert.Equal(1u, r.ReadUInt32());                    // flags: the child worldspace has the cell
            Assert.Equal(44, 4 * 3 + 12 + 12 + 4 + 4);           // the entry size the SKSE plugin reads
            Assert.Equal(b.Length, 8 + 4 + strings.Sum(s => 2 + Encoding.UTF8.GetByteCount(s)) + 4 + 2 * 44);

            ParentCopyWriter.Remove(dir);
            Assert.False(File.Exists(Path.Combine(dir, ParentCopyWriter.RelativePath)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
