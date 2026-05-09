using Avalonia;
using CefSharp;
using CefSharp.WinForms;
using System;
using System.IO;

namespace CefSharp.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var settings = new CefSettings
        {
            MultiThreadedMessageLoop = true,
            CachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CefSharp.Avalonia", "Cache"),
        };

        settings.CefCommandLineArgs.Add("disable-extensions", "1");
        settings.CefCommandLineArgs.Add("disable-background-networking", "1");

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
