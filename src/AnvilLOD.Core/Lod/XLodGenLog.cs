using System.Text;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// xLODGen writes <c>SSELODGen_log.txt</c> next to its program and ends a run's work with "LOD generation done." long before the window
/// is closed (the plugin is saved when it is closed). Watching the log tells AnvilLOD when the work is over.
/// </summary>
public static class XLodGenLog
{
    public const string DoneLine = "LOD generation done.";

    public static string PathFor(string toolFolder) => Path.Combine(toolFolder, "SSELODGen_log.txt");

    /// <summary>The log's length now: start watching from here so lines of earlier runs are not mistaken for this one.</summary>
    public static long Length(string logPath) => File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

    /// <summary>
    /// Reads what was added after <paramref name="offset"/> and says whether it contains the "done" line. The offset moves to the end that was read.
    /// A log that is shorter than the offset was started over, so it is read from the top.
    /// </summary>
    public static bool Scan(string logPath, ref long offset)
    {
        if (!File.Exists(logPath)) return false;
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < offset) offset = 0;
        if (fs.Length == offset) return false;
        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - offset];
        var read = 0;
        while (read < buf.Length)
        {
            var n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        offset += read;
        return Encoding.Latin1.GetString(buf, 0, read).Contains(DoneLine, StringComparison.Ordinal);
    }
}
