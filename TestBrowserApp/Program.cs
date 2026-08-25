using Avalonia;
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

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
