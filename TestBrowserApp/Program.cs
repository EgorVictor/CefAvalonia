using Avalonia;
using CefSharp.Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TestBrowserApp;

class Program
{
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "testapp_debug.log");

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}"); }
        catch { }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log($"FATAL UnhandledException (terminating={e.IsTerminating}): {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"UnobservedTaskException: {e.Exception}");
            e.SetObserved();
        };

        // CEF_DIAG=1 turns on verbose library diagnostics and routes them to the
        // same log file, so native-host embedding problems (X11 reparent, etc.)
        // are visible on Linux where Debug.WriteLine goes nowhere.
        if (Environment.GetEnvironmentVariable("CEF_DIAG") == "1")
        {
            CefDiagnostics.Level = CefLogLevel.Debug;
            CefDiagnostics.Sink = (level, message) => Log($"[{level}] {message}");
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

      // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // No per-platform tweaks here on purpose. Avalonia 11.x always selects the
        // X11 backend on Linux (Wayland needs the separate Avalonia.Wayland package
        // plus an explicit UseWayland(); the only WAYLAND_DISPLAY usage inside
        // Avalonia.X11 is for its GTK3 file-dialog helper). Everything the control
        // needs on Linux — X11 embedding, CEF binary deployment, +x bits — is done
        // inside CefSharp.Avalonia, so consumers never touch startup code.

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
