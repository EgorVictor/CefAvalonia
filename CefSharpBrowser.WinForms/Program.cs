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
            Form form;
            if (string.IsNullOrEmpty(pipeName))
                form = new StandaloneForm(url);
            else
                form = new BrowserForm(url, pipeName, hostPid);

            Application.Run(form);
        }
        finally
        {
            Cef.Shutdown();
        }

        return 0;
    }
}
