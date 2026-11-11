using AnvilLOD.Plugins;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class DynamicRefCheckTests
{
    private static string Row(uint key, string src5, string plug6, string src12, string plug13) =>
        $"{key}=1,1,1,1,,{src5},{plug6},-1,-1,,1,,{src12},{plug13},,370000,DynDOLOD.esp,370000,DynDOLOD.esp";

    [Fact]
    public void ReadRows_UsesTheUniquePerRowSourceColumns_AndFallsBackWhenTheyAreEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil-dyncheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "DynDOLOD_Tamriel_Objects.txt"), new[]
            {
                "[Config]", "bunchofnumbers=ABC", "[Objects]",
                Row(369598, "57708", "Lanterns Of Skyrim II.esm", "163135", "Lanterns Of Skyrim II.esm"),   // shared template in 5/6, the real ref in 12/13
                Row(369599, "47288", "HearthFires.esm", "", ""),                                             // 12/13 empty: use 5/6
                "369600=1,2,3",                                                                              // wrong column count
            });
            var rows = DynamicRefCheck.ReadRows(dir, out var unparsed, out var files);
            Assert.Equal(1, files);
            Assert.Equal(1, unparsed);
            Assert.Equal(2, rows.Count);
            Assert.Equal((369598u, 163135u, "Lanterns Of Skyrim II.esm", "Tamriel"), (rows[0].DynId, rows[0].SourceId, rows[0].SourcePlugin, rows[0].World));
            Assert.Equal((47288u, "HearthFires.esm"), (rows[1].SourceId, rows[1].SourcePlugin));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadRows_ReadsTheDllNgFormat_AndSkipsTheWorldList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil-dyncheck-ng-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "DynDOLOD_NG_Tamriel.txt"), new[]
            {
                "[Config]", "bunchofnumbers=ABC", "master=410440,DynDOLOD.esp", "x=-96", "y=-96", "[Objects]",
                "410448,DynDOLOD.esp,1,1,1,1,,57708,Lanterns Of Skyrim II.esm,-1,-1,,1,,288540,Lanterns Of Skyrim II.esm,",   // shared base in 7/8, the real ref in 14/15
                "410449,DynDOLOD.esp,0,,1,,1,47288,HearthFires.esm,-1,-1,,1,,,,",                                            // 14/15 empty: use 7/8
                "410450,DynDOLOD.esp,1,2",                                                                                   // too few columns
                "[Resets]", "3451,RigmorCyrodiil.esm",                                                                       // other sections are not Objects rows
                "[Markers]", "60,Skyrim.esm,-1,-14,267883,DynDOLOD.esm,5",
                "[Disabled]", "264465,Skyrim.esm",
            });
            File.WriteAllLines(Path.Combine(dir, "DynDOLOD_NG_Worlds.txt"), new[] { "[Worlds]", "1,Tamriel" });
            var rows = DynamicRefCheck.ReadRows(dir, out var unparsed, out var files);
            Assert.Equal(1, files);
            Assert.Equal(1, unparsed);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(r.Ng));
            Assert.Equal((410448u, 288540u, "Lanterns Of Skyrim II.esm", "Tamriel"), (rows[0].DynId, rows[0].SourceId, rows[0].SourcePlugin, rows[0].World));
            Assert.Equal("DynDOLOD_NG_Tamriel.txt", rows[0].DataFile);
            Assert.Equal((47288u, "HearthFires.esm"), (rows[1].SourceId, rows[1].SourcePlugin));
            // the enable columns keep their old meaning (old col 2 = new col 4, old col 4 = new col 6)
            Assert.True(rows[0].DataStartsEnabled);
            Assert.False(rows[0].DataChainOpposite);
            Assert.True(rows[1].DataStartsEnabled);
            Assert.True(rows[1].DataChainOpposite);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadRows_MissingFolder_IsEmpty()
    {
        var rows = DynamicRefCheck.ReadRows(Path.Combine(Path.GetTempPath(), "anvil-no-such-" + Guid.NewGuid().ToString("N")), out var unparsed, out var files);
        Assert.Empty(rows);
        Assert.Equal((0, 0), (unparsed, files));
    }
}

public class DynFixWriterTests
{
    [Fact]
    public void EditObjectsLine_SetsColumns2And4_KeepsEverythingElse_AndTheLineEnding()
    {
        var line = "369598=1,1,,1,,47288,HearthFires.esm,-1,-1,,1,,47288,HearthFires.esm,,369501,DynDOLOD.esp,369501,DynDOLOD.esp\r\n";
        var edited = DynFixWriter.EditObjectsLine(line, 369598, startsEnabled: true, chainOpposite: true);
        Assert.Equal("369598=1,1,1,1,1,47288,HearthFires.esm,-1,-1,,1,,47288,HearthFires.esm,,369501,DynDOLOD.esp,369501,DynDOLOD.esp\r\n", edited);
        var back = DynFixWriter.EditObjectsLine(edited, 369598, startsEnabled: false, chainOpposite: false);
        Assert.Equal("369598=1,1,,1,,47288,HearthFires.esm,-1,-1,,1,,47288,HearthFires.esm,,369501,DynDOLOD.esp,369501,DynDOLOD.esp\r\n", back);
    }

    [Fact]
    public void EditObjectsLine_RefusesAWrongKey_OrWrongColumnCount()
    {
        var line = "369598=1,1,,1,,47288,HearthFires.esm,-1,-1,,1,,47288,HearthFires.esm,,369501,DynDOLOD.esp,369501,DynDOLOD.esp\n";
        Assert.Throws<InvalidOperationException>(() => DynFixWriter.EditObjectsLine(line, 111, true, false));
        Assert.Throws<InvalidOperationException>(() => DynFixWriter.EditObjectsLine("369598=1,2,3\n", 369598, true, false));
    }
}
