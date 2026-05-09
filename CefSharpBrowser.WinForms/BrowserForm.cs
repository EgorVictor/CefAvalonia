using CefSharp;
using CefSharp.WinForms;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

public class BrowserForm : Form
{
    private readonly ChromiumWebBrowser browser;
    private readonly NamedPipeServerStream pipeServer;
    private readonly string pipeName;
    private readonly int hostPid;
    private StreamReader? pipeReader;
    private StreamWriter? pipeWriter;
    private readonly CancellationTokenSource pipeCts = new();
    private Thread? pipeThread;

    public BrowserForm(string url, string pipeName, int hostPid)
    {
        this.pipeName = pipeName;
        this.hostPid = hostPid;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Width = 800;
        Height = 600;

        browser = new ChromiumWebBrowser(url)
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(browser);

        pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Message, PipeOptions.Asynchronous);

        Shown += (_, _) => StartPipeListener();
        FormClosing += OnFormClosing;

        if (hostPid > 0)
            _ = WatchHostAsync(hostPid);
    }

    private void StartPipeListener()
    {
        pipeThread = new Thread(() =>
        {
            try
            {
                pipeServer.WaitForConnection();

                pipeReader = new StreamReader(pipeServer, Encoding.UTF8);
                pipeWriter = new StreamWriter(pipeServer, Encoding.UTF8) { AutoFlush = true };

                browser.AddressChanged += OnAddressChanged;
                browser.LoadError += OnLoadError;

                var hwnd = Handle.ToString("X");
                SendEvent("Ready|" + hwnd);

                while (!pipeCts.IsCancellationRequested)
                {
                    var line = pipeReader.ReadLine();
                    if (line == null) break;
                    BeginInvoke((Action)(() => ProcessCommand(line)));
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                SendEvent("PipeError|" + ex.Message);
            }
        })
        { IsBackground = true };
        pipeThread.Start();
    }

    private void ProcessCommand(string line)
    {
        var sep = line.IndexOf('|');
        var cmd = sep >= 0 ? line[..sep] : line;
        var arg = sep >= 0 ? line[(sep + 1)..] : "";

        switch (cmd)
        {
            case "Navigate":
                var navUrl = arg;
                if (!navUrl.StartsWith("http://") && !navUrl.StartsWith("https://"))
                    navUrl = "https://" + navUrl;
                browser.Load(navUrl);
                break;

            case "Reload":
                browser.Reload();
                break;

            case "Stop":
                browser.Stop();
                break;

            case "Close":
                Close();
                break;
        }
    }

    private void SendEvent(string message)
    {
        try
        {
            pipeWriter?.WriteLine(message);
        }
        catch { }
    }

    private void OnAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        SendEvent("AddressChanged|" + e.Address);
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        SendEvent($"LoadError|{(int)e.ErrorCode}|{e.ErrorText}|{e.FailedUrl}");
    }

    private async Task WatchHostAsync(int hostPid)
    {
        while (!pipeCts.IsCancellationRequested)
        {
            try
            {
                var host = Process.GetProcessById(hostPid);
                host.WaitForExit();
                SendEvent("HostExited");
                break;
            }
            catch (ArgumentException)
            {
                break;
            }
        }

        BeginInvoke((Action)(() =>
        {
            if (!IsDisposed)
                Close();
        }));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        pipeCts.Cancel();
        browser.AddressChanged -= OnAddressChanged;
        browser.LoadError -= OnLoadError;

        if (!browser.IsDisposed)
            browser.Dispose();

        pipeReader?.Dispose();
        pipeWriter?.Dispose();
        pipeServer?.Dispose();
    }
}
