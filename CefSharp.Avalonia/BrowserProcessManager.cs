using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Manages CefBrowser.Native.exe subprocess using stdin/stdout IPC (cross-platform, no named pipes).
/// Commands: C# → stdin
/// Events:   stdout → C#
/// This eliminates IPC bottlenecks and works on Windows/Linux/macOS.
/// </summary>
public sealed class BrowserProcessManager : IDisposable
{
    private readonly string exePath;
    private Process? browserProcess;
    private StreamWriter? stdin;
    private StreamReader? stdout;
    private string? lastUrl;
    public string? LastUrl => lastUrl;
    private bool disposed;
    private bool _ipcFrozen;
    private int _lastWidth = 1024;
    private int _lastHeight = 768;

    // Bounded channels with DropOldest
    private static BoundedChannelOptions BufOpts() => new(256) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    };
    private readonly Channel<string> _sendChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _writerTask;
    private readonly Channel<string> _recvChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _recvTask;
    private static readonly string _diagLogPath = Path.Combine(
        AppContext.BaseDirectory, "cef_browser_diag.log");

    /// <summary>Raised on AddressChanged event from native process.</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised on page load error. Arg: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when the native process exits unexpectedly.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised when the browser window HWND is ready.</summary>
    public event Action<IntPtr>? WindowHandleReceived;
    /// <summary>Raised when page title changes.</summary>
    public event Action<string>? TitleChanged;
    public event Action<string>? OpenPopup;
    /// <summary>Raised when loading state changes (true=loading, false=done).</summary>
    public event Action<bool>? LoadingStateChanged;

    public BrowserProcessManager()
    {
        exePath = GetCefPath("CefBrowser.Native.exe");
        Debug.WriteLine($"[BPM] CefBrowser.Native path: {exePath}");
    }

    private string GetCefPath(string name)
    {
        var local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
            var test = Path.Combine(dir, "CefBrowser.Native", "build", "Release", name);
            if (File.Exists(test)) return test;
        }

        return local;
    }

    /// <summary>
    /// Launches CefBrowser.Native.exe with URL and CefSettings args.
    /// Pipes are connected via stdio (Process.StandardInput/StandardOutput).
    /// </summary>
    public async Task StartAsync(string url, CefSettings? settings = null)
    {
        lastUrl = url;

        var settingsArgs = settings?.ToCommandLineArgs() ?? "";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--cef-url={url}{settingsArgs}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        browserProcess = Process.Start(psi);
        if (browserProcess == null)
            throw new InvalidOperationException("Failed to start browser process");

        browserProcess.EnableRaisingEvents = true;
        browserProcess.Exited += (_, _) =>
        {
            if (!disposed)
                BrowserCrashed?.Invoke();
        };

        stdin = browserProcess.StandardInput;
        stdout = browserProcess.StandardOutput;

        // Read stderr to a diagnostic log file
        _ = Task.Run(async () =>
        {
            try
            {
                using var stderrReader = browserProcess.StandardError;
                string? errLine;
                while ((errLine = await stderrReader.ReadLineAsync()) != null)
                {
                    try { File.AppendAllText(_diagLogPath, errLine + "\n"); } catch { }
                }
            }
            catch { }
        });

        _writerTask = Task.Run(WriterThreadProc);
        _recvTask = Task.Run(ReadStdoutLoopAsync);
        _ = Task.Run(ProcessRecvChannelAsync);

        // Send initial Ready signal (wait for browser to be ready)
        await Task.Delay(500);
        Debug.WriteLine("[BPM] StartAsync complete");
    }

    /// <summary>
    /// Background writer: reads from _sendChannel and writes to stdin.
    /// Non-blocking: all SendAsync calls are queued to the channel.
    /// </summary>
    private async Task WriterThreadProc()
    {
        try
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync())
            {
                if (_ipcFrozen) continue;
                if (stdin == null) break;

                try
                {
                    stdin.WriteLine(msg);
                    Console.Error.WriteLine($"DIAG: BPM Sent: {msg}");
                    Debug.WriteLine($"[BPM] Sent: {msg}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BPM] Write error: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] WriterThread error: {ex}");
        }
    }

    /// <summary>
    /// Background reader: reads lines from stdout and dispatches events.
    /// Protocol: "Cmd|arg" or "Cmd"
    /// </summary>
    private async Task ReadStdoutLoopAsync()
    {
        try
        {
            if (stdout == null) return;

            string? line;
            while ((line = await stdout.ReadLineAsync()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;

                Debug.WriteLine($"[BPM] Received: {line}");
                _recvChannel.Writer.TryWrite(line);
            }
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[BPM] ReadStdout IO error: {ex.Message}");
        }
        catch (ObjectDisposedException ex)
        {
            Debug.WriteLine($"[BPM] ReadStdout disposed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] ReadStdout error: {ex}");
        }

        if (!disposed)
        {
            Debug.WriteLine("[BPM] Browser process crashed unexpectedly");
            BrowserCrashed?.Invoke();
        }
    }

    /// <summary>
    /// Background dispatcher: processes received messages.
    /// </summary>
    private async Task ProcessRecvChannelAsync()
    {
        try
        {
            await foreach (var msg in _recvChannel.Reader.ReadAllAsync())
            {
                if (!_ipcFrozen)
                {
                    DispatchMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] ProcessRecvChannel error: {ex}");
        }
    }

    /// <summary>
    /// Parse and dispatch messages from native process.
    /// </summary>
    private void DispatchMessage(string line)
    {
        try
        {
            var sep = line.IndexOf('|');
            var cmd = sep >= 0 ? line[..sep] : line;
            var arg = sep >= 0 ? line[(sep + 1)..] : "";

            switch (cmd)
            {
                case "Ready":
                    Console.Error.WriteLine($"DIAG: BPM Received Ready HWND={arg}");
                    if (!string.IsNullOrEmpty(arg) &&
                        long.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var hwnd))
                        WindowHandleReceived?.Invoke(new IntPtr(hwnd));
                    break;
                case "AddressChanged":
                    lastUrl = arg;
                    AddressChanged?.Invoke(arg);
                    break;
                case "LoadError":
                    LoadError?.Invoke(arg);
                    break;
                case "NavState":
                    var parts = arg.Split('|');
                    if (parts.Length >= 1)
                        LoadingStateChanged?.Invoke(parts[0] == "1");
                    break;
                case "TitleChanged":
                    TitleChanged?.Invoke(arg);
                    break;
                case "OpenPopup":
                    OpenPopup?.Invoke(arg);
                    break;
                default:
                    Debug.WriteLine($"[BPM] Unknown message: {cmd}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] DispatchMessage error: {ex}");
        }
    }

    public Task NavigateAsync(string url)
    {
        lastUrl = url;
        return SendAsync("Navigate", url);
    }

    public Task ReloadAsync() => SendAsync("Reload", "");
    public Task StopAsync() => SendAsync("Stop", "");
    public Task SendEmbedDoneAsync() => SendAsync("EmbedDone", "");
    public Task SendResizeAsync(int w, int h) => SendAsync("Resize", $"{w}|{h}");

    public void SendResize(int w, int h)
    {
        _lastWidth = w;
        _lastHeight = h;
        SendSync("Resize", $"{w}|{h}");
    }

    public void FreezeIpc()
    {
        _ipcFrozen = true;
        Debug.WriteLine("[BPM] IPC frozen");
    }

    public async Task ResumeIpcAsync()
    {
        _ipcFrozen = false;
        Debug.WriteLine("[BPM] IPC resuming");
        await SendAsync("Resize", $"{_lastWidth}|{_lastHeight}");
    }

    private Task SendAsync(string cmd, string arg)
    {
        var msg = $"{cmd}|{arg}";
        _sendChannel.Writer.TryWrite(msg);
        return Task.CompletedTask;
    }

    private void SendSync(string cmd, string arg)
    {
        var msg = $"{cmd}|{arg}";
        _sendChannel.Writer.TryWrite(msg);
    }

    private void Kill()
    {
        _sendChannel.Writer.TryComplete();
        _recvChannel.Writer.TryComplete();

        if (browserProcess != null)
        {
            try
            {
                if (!browserProcess.HasExited)
                    browserProcess.Kill();
            }
            catch (Exception ex) { Debug.WriteLine($"[BPM] Kill error: {ex.Message}"); }
            browserProcess.Dispose();
            browserProcess = null;
        }

        stdin?.Dispose();
        stdout?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Kill();

        if (_writerTask != null)
            _writerTask.Wait(2000);
        if (_recvTask != null)
            _recvTask.Wait(2000);

        _sendChannel.Writer.TryComplete();
        _recvChannel.Writer.TryComplete();
    }
}
