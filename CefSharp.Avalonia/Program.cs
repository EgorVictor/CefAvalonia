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

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharp.Avalonia");

        var settings = new CefSettings
        {
            MultiThreadedMessageLoop = true,
            CachePath = Path.Combine(appData, "cache")
        };

        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        settings.CefCommandLineArgs["disable-extensions"] = "1";
        settings.CefCommandLineArgs["disable-background-networking"] = "1";
        settings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";

        var success = Cef.Initialize(settings);
        if (!success)
            throw new InvalidOperationException("Cef.Initialize failed. Check browser.log for details.");

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
