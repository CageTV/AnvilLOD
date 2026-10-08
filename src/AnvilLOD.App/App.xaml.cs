using System.Windows;
using System.Windows.Threading;

namespace AnvilLOD.App;

public partial class App : Application
{
    public App()
    {
        StartupLog.Write("App constructed");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupLog.Write("OnStartup");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupLog.Write($"Exit code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        StartupLog.Write("FATAL (UI thread): " + e.Exception);
        try
        {
            MessageBox.Show(
                e.Exception.Message + "\n\nDetails were written to %LocalAppData%\\AnvilLOD\\logs\\app.log",
                "AnvilLOD crashed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* the UI itself may be what failed */ }
    }
}
