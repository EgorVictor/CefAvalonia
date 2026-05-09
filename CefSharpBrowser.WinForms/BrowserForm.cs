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
    private readonly StreamReader pipeReader;
    private readonly StreamWriter pipeWriter;
    private readonly CancellationTokenSource pipeCts = new();
    private Thread? pipeThread;

    public BrowserForm(string url, string pipeName, int hostPid)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Width = 800;
        Height = 600;

        browser = new ChromiumWebBrowser(url)
        {
            Dock = DockStyle.Fill
        };

        browser.AddressChanged += OnAddressChanged;
        browser.LoadError += OnLoadError;

        Controls.Add(browser);

        pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Message, PipeOptions.Asynchronous);
        pipeReader = new StreamReader(pipeServer, Encoding.UTF8);
        pipeWriter = new StreamWriter(pipeServer, Encoding.UTF8) { AutoFlush = true };

        Shown += (_, _) => StartPipeListener();
        FormClosing += OnFormClosing;

        if (hostPid > 0)
            _ = WatchHostAsync(hostPid);
    }

    private void StartPipeListener()
    {
        pipeThread = new Thread(async () =>
        {
            try
            {
                pipeServer.WaitForConnection();

                var hwnd = Handle.ToString("X");
                await SendEventAsync($"Ready|{hwnd}");

                while (!pipeCts.IsCancellationRequested)
                {
                    var line = await pipeReader.ReadLineAsync();
                    if (line == null) break;
                    ProcessCommand(line);
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                await SendEventAsync("PipeError|" + ex.Message);
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

        BeginInvoke((Action)(() =>
        {
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
                    BeginInvoke((Action)Close);
                    break;
            }
        }));
    }

    private async Task SendEventAsync(string message)
    {
        try
        {
            await pipeWriter.WriteLineAsync(message);
        }
        catch { }
    }

    private void OnAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        _ = SendEventAsync($"AddressChanged|{e.Address}");
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        _ = SendEventAsync($"LoadError|{(int)e.ErrorCode}|{e.ErrorText}|{e.FailedUrl}");
    }

    private async Task WatchHostAsync(int hostPid)
    {
        while (!pipeCts.IsCancellationRequested)
        {
            try
            {
                var host = Process.GetProcessById(hostPid);
                host.WaitForExit();
                await SendEventAsync("HostExited");
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

        pipeReader.Dispose();
        pipeWriter.Dispose();
        pipeServer.Dispose();
    }
}
