using AnvilLOD.Core.Lod;

namespace AnvilLOD.Core.Tests;

public sealed class XLodGenLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvil-xlog-" + Guid.NewGuid().ToString("N"));

    public XLodGenLogTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    [Fact]
    public void Only_lines_added_after_the_start_count()
    {
        var log = XLodGenLog.PathFor(_root);
        File.WriteAllText(log, "[00:01] old run\n[01:39] LOD generation done.\n");   // a finished earlier run
        var offset = XLodGenLog.Length(log);
        Assert.False(XLodGenLog.Scan(log, ref offset));

        File.AppendAllText(log, "[00:02] working\n");
        Assert.False(XLodGenLog.Scan(log, ref offset));

        File.AppendAllText(log, "[01:10] [Occlusion.esp] Removed 0 unused masters.\n[01:10] LOD generation done.\n");
        Assert.True(XLodGenLog.Scan(log, ref offset));
        Assert.Equal(XLodGenLog.Length(log), offset);
    }

    [Fact]
    public void A_missing_or_restarted_log_is_handled()
    {
        var log = XLodGenLog.PathFor(_root);
        var offset = 0L;
        Assert.False(XLodGenLog.Scan(log, ref offset));
        Assert.Equal(0, XLodGenLog.Length(log));

        File.WriteAllText(log, "LOD generation done.\n");
        offset = 10_000;                 // the log was started over: shorter than where we were
        Assert.True(XLodGenLog.Scan(log, ref offset));
    }

    [Fact]
    public void A_done_line_cut_between_two_reads_is_not_lost_when_it_is_whole_in_the_second()
    {
        var log = XLodGenLog.PathFor(_root);
        File.WriteAllText(log, "start\n");
        var offset = XLodGenLog.Length(log);
        File.AppendAllText(log, "LOD generation done.\n");
        Assert.True(XLodGenLog.Scan(log, ref offset));
    }
}
