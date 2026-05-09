using CefSharp;
using CefSharp.WinForms;
using System;
using System.IO;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var url = "https://www.bing.com";
        var pipeName = "";
        var hostPid = 0;

        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            var key = eq >= 0 ? arg[..eq] : arg;
            var val = eq >= 0 ? arg[(eq + 1)..] : "";

            if (key.Equals("--url", StringComparison.OrdinalIgnoreCase))
                url = val;
            else if (key.Equals("--pipe", StringComparison.OrdinalIgnoreCase))
                pipeName = val;
            else if (key.Equals("--host-pid", StringComparison.OrdinalIgnoreCase))
                int.TryParse(val, out hostPid);
        }

        if (string.IsNullOrEmpty(pipeName))
        {
            MessageBox.Show("Missing --pipe argument", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharpBrowser.WinForms", "logs");
        Directory.CreateDirectory(logDir);

        Cef.EnableHighDPISupport();

        var settings = new CefSettings
        {
            MultiThreadedMessageLoop = true,
            CachePath = Path.Combine(logDir, "..", "cache")
        };

        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        settings.CefCommandLineArgs["disable-extensions"] = "1";

        var success = Cef.Initialize(settings);
        if (!success)
        {
            MessageBox.Show("Cef.Initialize failed", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try
        {
            Application.Run(new BrowserForm(url, pipeName, hostPid));
        }
        finally
        {
            Cef.Shutdown();
        }

        return 0;
    }
}
