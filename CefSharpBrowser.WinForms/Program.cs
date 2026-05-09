using CefSharp;
using CefSharp.WinForms;
using System;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Cef.EnableHighDPISupport();

        var settings = new CefSettings
        {
            MultiThreadedMessageLoop = true,
            CachePath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CefSharpBrowser.WinForms", "cache")
        };

        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        settings.CefCommandLineArgs["disable-extensions"] = "1";
        settings.CefCommandLineArgs["disable-background-networking"] = "1";
        settings.CefCommandLineArgs["disable-renderer-backgrounding"] = "1";

        var success = Cef.Initialize(settings);
        if (!success)
            throw new InvalidOperationException("Cef.Initialize failed.");

        try
        {
            Application.Run(new Form1());
        }
        finally
        {
            if (Cef.IsInitialized)
                Cef.Shutdown();
        }
    }
}
