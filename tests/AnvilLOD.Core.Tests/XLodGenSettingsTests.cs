using AnvilLOD.Core.Lod;

namespace AnvilLOD.Core.Tests;

public sealed class XLodGenSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvil-xlg-" + Guid.NewGuid().ToString("N"));

    public XLodGenSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private const string Sample =
        "[Init]\r\nFirstStart=1\r\n\r\n[SSE LOD Options]\r\nOcclusion=0\r\nObjectsLOD=1\r\nTreesLOD=1\r\nTerrainLOD=1\r\nOcclusionQuality=3\r\nOcclusionMode=All\r\nHeightMaps=1\r\n\r\n[Filter]\r\nX=1\r\n";

    [Fact]
    public void Apply_sets_the_occlusion_keys_and_leaves_everything_else()
    {
        var text = XLodGenSettings.Apply(Sample, new OcclusionOptions(2, 100, 80, "-Flat +Border"));
        var v = XLodGenSettings.Read(text);
        Assert.Equal("1", v["Occlusion"]);
        Assert.Equal("1", v["OcclusionNewPlugin"]);   // added: it was missing
        Assert.Equal("0", v["ObjectsLOD"]);
        Assert.Equal("0", v["TreesLOD"]);
        Assert.Equal("0", v["TerrainLOD"]);
        Assert.Equal("2", v["OcclusionQuality"]);
        Assert.Equal("100", v["OcclusionHeight"]);
        Assert.Equal("80", v["OcclusionRadius"]);
        Assert.Equal("-Flat +Border", v["OcclusionMode"]);
        Assert.Equal("2", v["OcclusionMaxThreadsObjectLOD"]);   // the default keeps memory use down
        Assert.Equal("0", v["Chunk"]);                 // chunk off, its fields emptied
        Assert.Equal("", v["LODX"]);
        Assert.Equal("0", v["OcclusionCreateImages"]);
        Assert.Equal("1", v["HeightMaps"]);            // not ours
        Assert.Contains("[Init]\r\nFirstStart=1\r\n", text);
        Assert.Contains("[Filter]\r\nX=1\r\n", text);
    }

    [Fact]
    public void Apply_twice_changes_nothing_more()
    {
        var o = new OcclusionOptions(3, 0, 100, "All");
        var once = XLodGenSettings.Apply(Sample, o);
        Assert.Equal(once, XLodGenSettings.Apply(once, o));
    }

    [Fact]
    public void Apply_adds_the_section_when_the_file_has_none()
    {
        var text = XLodGenSettings.Apply("[Init]\nFirstStart=1\n", new OcclusionOptions());
        Assert.Equal("1", XLodGenSettings.Read(text)["Occlusion"]);
        Assert.StartsWith("[Init]\nFirstStart=1\n[SSE LOD Options]\n", text);
    }

    [Fact]
    public void Values_are_kept_in_range()
    {
        var v = XLodGenSettings.Read(XLodGenSettings.Apply("", new OcclusionOptions(9, -5, 0, " ")));
        Assert.Equal("3", v["OcclusionQuality"]);
        Assert.Equal("0", v["OcclusionHeight"]);
        Assert.Equal("1", v["OcclusionRadius"]);
        Assert.Equal("-Flat +Border", v["OcclusionMode"]);
        Assert.Equal("-1", XLodGenSettings.Read(XLodGenSettings.Apply("", new OcclusionOptions(ObjectThreads: 0)))["OcclusionMaxThreadsObjectLOD"]);
    }

    [Fact]
    public void ApplyToFile_keeps_a_backup_and_the_other_bytes()
    {
        var path = Path.Combine(_root, "Plugins.sseviewsettings");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(Sample.Replace("X=1", "Name=José")));
        Assert.True(XLodGenSettings.ApplyToFile(path, new OcclusionOptions(), new DateTime(2026, 10, 19, 21, 0, 0)));
        Assert.True(File.Exists(path + ".bak_anvillod_20261019-210000"));
        Assert.Contains((byte)0xE9, File.ReadAllBytes(path));   // the accent byte is unchanged
        Assert.Equal("1", XLodGenSettings.Read(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)))["Occlusion"]);
        Assert.False(XLodGenSettings.ApplyToFile(Path.Combine(_root, "none"), new OcclusionOptions()));
    }
}
