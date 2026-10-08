using System.Diagnostics;

namespace AnvilLOD.Plugins;

/// <summary>
/// Passes progress messages on and also appends them, time-stamped, to a log file (e.g. the output folder's
/// <c>AnvilLOD.log</c>), so a run can be looked at afterwards without copying the app's Log tab.
/// </summary>
public sealed class FileLogProgress : IProgress<string>, IDisposable
{
    private readonly IProgress<string>? _inner;
    private readonly StreamWriter? _writer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();

    public FileLogProgress(string? logFile, IProgress<string>? inner, string header)
    {
        _inner = inner;
        if (logFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            _writer = new StreamWriter(logFile, append: false) { AutoFlush = true };
            _writer.WriteLine($"{header}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (IOException) { _writer = null; }
        catch (UnauthorizedAccessException) { _writer = null; }
    }

    public void Report(string value)
    {
        _inner?.Report(value);
        if (_writer is null) return;
        lock (_lock) _writer.WriteLine($"[{_clock.Elapsed:mm\\:ss\\.f}] {value}");
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
