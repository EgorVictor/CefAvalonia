using Avalonia;
using CefSharp.WinForms;
using System;
using System.IO;

namespace CefSharp.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Cef.EnableHighDPISupport();

        var settings = new CefSettings
        {
            MultiThreadedMessageLoop = true,
            CachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CefSharp.Avalonia", "cache")
        };

        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        settings.CefCommandLineArgs["disable-extensions"] = "1";
        settings.CefCommandLineArgs["disable-background-networking"] = "1";
        settings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";

        Cef.Initialize(settings);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Cef.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
