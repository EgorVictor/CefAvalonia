using Avalonia;
using System;
using CefSharp;
using CefSharp.WinForms;

namespace CefSharp.Avalonia;

class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var settings = new CefSettings();
        settings.MultiThreadedMessageLoop = true;
        Cef.Initialize(settings);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
