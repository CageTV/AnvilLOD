using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AnvilLOD.App;

/// <summary>
/// Startup/crash trace so silent failures (e.g. under MO2's USVFS) leave evidence.
/// Writes to %LocalAppData%\AnvilLOD\logs\app.log and AnvilLOD.App.log next to the exe.
/// Installed from a module initializer, so it runs before WPF itself starts.
/// </summary>
internal static class StartupLog
{
    private static readonly object Gate = new();
    private static readonly List<string> Targets = [];

    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnvilLOD", "logs");
            Directory.CreateDirectory(local);
            Targets.Add(Path.Combine(local, "app.log"));
        }
        catch { /* ignore */ }
        Targets.Add(Path.Combine(AppContext.BaseDirectory, "AnvilLOD.App.log"));

        foreach (var t in Targets)
        {
            try { File.WriteAllText(t, ""); } catch { /* ignore */ }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("FATAL (AppDomain): " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };

        Write($"AnvilLOD.App starting  v{typeof(StartupLog).Assembly.GetName().Version}");
        Write($"Exe:      {Environment.ProcessPath}");
        Write($"CWD:      {Environment.CurrentDirectory}");
        Write($"Runtime:  {RuntimeInformation.FrameworkDescription}  ({RuntimeInformation.ProcessArchitecture})");
        Write($"OS:       {RuntimeInformation.OSDescription}");
        Write($"Args:     {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        Write($"Under MO2 (usvfs loaded): {IsUnderMo2()}");
    }

    public static bool IsUnderMo2()
    {
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* ignore */ }
        return false;
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            foreach (var t in Targets)
            {
                try { File.AppendAllText(t, line, Encoding.UTF8); } catch { /* ignore */ }
            }
        }
    }
}
