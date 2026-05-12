using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public sealed class BrowserProcessManager : IDisposable
{
    private readonly string pipeName;
    private readonly string exePath;
    private Process? browserProcess;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private string? lastUrl;
    public string? LastUrl => lastUrl;
    private bool disposed;
    // Bounded channels with DropOldest prevent unbounded memory growth.
    // If the pipe is slow, stale messages are dropped instead of piling up.
    private static BoundedChannelOptions BufOpts() => new(256) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    };
    private readonly Channel<string> _sendChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _writerTask;
    private readonly Channel<string> _recvChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _recvTask;

    public event Action<string>? AddressChanged;
    public event Action<string>? LoadError;
    public event Action? BrowserCrashed;
    public event Action<IntPtr>? WindowHandleReceived;
    public event Action<bool>? LoadingStateChanged;
    public event Action<string>? TitleChanged;

    public BrowserProcessManager()
    {
        pipeName = $"CefAvalonia_{Environment.ProcessId}";
        exePath = ResolveExePath();
        Debug.WriteLine($"[BPM] Constructor: ProcessId={Environment.ProcessId}");
        Debug.WriteLine($"[BPM] Constructor: exePath={exePath}");
        Debug.WriteLine($"[BPM] Constructor: BaseDir={AppContext.BaseDirectory}");
        Debug.WriteLine($"[BPM] Constructor: FileExists={File.Exists(exePath)}");
    }

    private static string ResolveExePath()
    {
        var name = "CefBrowser.Native.exe";
        var local = Path.Combine(AppContext.BaseDirectory, name);
        Debug.WriteLine($"[BPM] ResolveExePath: local={local} exists={File.Exists(local)}");
        if (File.Exists(local)) return local;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
            var test = Path.Combine(dir, "CefBrowser.Native", "build", "Release", name);
            Debug.WriteLine($"[BPM] ResolveExePath: alt={test} exists={File.Exists(test)}");
            if (File.Exists(test)) return test;
        }

        Debug.WriteLine($"[BPM] ResolveExePath: fallback to {local}");
        return local;
    }

    public async Task StartAsync(string url)
    {
        lastUrl = url;

        Debug.WriteLine($"[BPM] StartAsync url='{url}' exe='{exePath}' pipe='{pipeName}'");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--cef-pipe={pipeName} --cef-url={url} --cef-host-pid={Environment.ProcessId} --disable-gpu --no-sandbox",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["CEF_PIPE"] = pipeName;
        psi.EnvironmentVariables["CEF_URL"] = url;
        psi.EnvironmentVariables["CEF_HOST_PID"] = Environment.ProcessId.ToString();

        Debug.WriteLine($"[BPM] Starting process: {exePath} args='{psi.Arguments}'");
        browserProcess = Process.Start(psi);
        if (browserProcess == null)
        {
            Debug.WriteLine($"[BPM] Process.Start returned null!");
            throw new InvalidOperationException("Failed to start browser process");
        }

        Debug.WriteLine($"[BPM] Process started PID={browserProcess.Id}");

        // Subscribe to process exit for crash detection
        browserProcess.EnableRaisingEvents = true;
        browserProcess.Exited += (_, _) =>
        {
            Debug.WriteLine("[BPM] Browser process exited (crashed?)");
            if (!disposed)
            {
                BrowserCrashed?.Invoke();
            }
        };

        await ConnectPipeAsync();
    }

    private async Task ConnectPipeAsync()
    {
        Debug.WriteLine($"[BPM] ConnectPipeAsync: connecting to pipe '{pipeName}'");
        for (int i = 0; i < 30; i++)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                Debug.WriteLine($"[BPM] Pipe connect attempt {i + 1}...");
                await pipe.ConnectAsync(1000);
                Debug.WriteLine($"[BPM] Pipe connected!");
                pipe.ReadMode = System.IO.Pipes.PipeTransmissionMode.Message;
                reader = new StreamReader(pipe);
                writer = new StreamWriter(pipe) { AutoFlush = true };

                // Background writer drains send channel
                _writerTask = Task.Run(RunWriterAsync);
                // Background receiver processes inbound messages IN ORDER
                _recvTask = Task.Run(ProcessRecvChannelAsync);
                _ = ReadPipeLoopAsync();

                Debug.WriteLine($"[BPM] Pipe reader/writer started");
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BPM] Pipe connect attempt {i + 1} failed: {ex.GetType().Name}");
                await Task.Delay(500);
            }
        }
        Debug.WriteLine($"[BPM] ConnectPipeAsync: TIMEOUT after 30 attempts");
        throw new TimeoutException("Failed to connect to browser process via named pipe");
    }

    private async Task RunWriterAsync()
    {
        Debug.WriteLine("[BPM] Writer task started");
        try
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync())
            {
                if (writer == null) break;
                await writer.WriteLineAsync(msg);
            }
        }
        catch (IOException)
        {
            Debug.WriteLine("[BPM] Writer pipe broken");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine("[BPM] Writer disposed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] Writer error: {ex.GetType().Name}: {ex.Message}");
        }
        Debug.WriteLine("[BPM] Writer task exited");
    }

    private async Task ReadPipeLoopAsync()
    {
        Debug.WriteLine($"[BPM] ReadPipeLoopAsync started");
        try
        {
            while (!disposed && reader != null)
            {
                var line = await reader.ReadLineAsync();
                Debug.WriteLine($"[BPM] ReadPipeLoop received: '{line}'");
                if (line == null) break;
                // Non-blocking enqueue: always succeeds for unbounded channel
                _recvChannel.Writer.TryWrite(line);
            }
        }
        catch (IOException)
        {
            Debug.WriteLine($"[BPM] ReadPipeLoop pipe broken");
        }
        catch (ObjectDisposedException)
        {
            Debug.WriteLine($"[BPM] ReadPipeLoop disposed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] ReadPipeLoop error: {ex.GetType().Name}: {ex.Message}");
        }

        Debug.WriteLine($"[BPM] ReadPipeLoop exited, disposed={disposed}");
        if (!disposed)
        {
            Debug.WriteLine($"[BPM] Firing BrowserCrashed event");
            BrowserCrashed?.Invoke();
        }
    }

    private async Task ProcessRecvChannelAsync()
    {
        Debug.WriteLine("[BPM] ProcessRecvChannelAsync started");
        try
        {
            await foreach (var msg in _recvChannel.Reader.ReadAllAsync())
            {
                DispatchPipeMessage(msg);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] ProcessRecv error: {ex.GetType().Name}: {ex.Message}");
        }
        Debug.WriteLine("[BPM] ProcessRecvChannelAsync exited");
    }

    private void DispatchPipeMessage(string line)
    {
        var sep = line.IndexOf('|');
        var cmd = sep >= 0 ? line[..sep] : line;
        var arg = sep >= 0 ? line[(sep + 1)..] : "";

        Debug.WriteLine($"[BPM] Dispatch cmd='{cmd}' arg='{arg}'");

        try
        {
            switch (cmd)
            {
                case "Ready":
                    if (!string.IsNullOrEmpty(arg) &&
                        long.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var hwnd))
                    {
                        var ptr = new IntPtr(hwnd);
                        Debug.WriteLine($"[BPM] Ready parsed HWND=0x{ptr.ToInt64():X8}");
                        Debug.WriteLine($"[BPM] Invoking WindowHandleReceived (handler count: {WindowHandleReceived?.GetInvocationList().Length ?? 0})");
                        WindowHandleReceived?.Invoke(ptr);
                    }
                    else
                    {
                        Debug.WriteLine($"[BPM] Ready parse FAILED: arg='{arg}'");
                    }
                    break;
                case "AddressChanged":
                    Debug.WriteLine($"[BPM] AddressChanged: {arg}");
                    lastUrl = arg;
                    AddressChanged?.Invoke(arg);
                    break;
                case "LoadError":
                    Debug.WriteLine($"[BPM] LoadError: {arg}");
                    LoadError?.Invoke(arg);
                    break;
                case "NavState":
                    var parts = arg.Split('|');
                    if (parts.Length == 3)
                    {
                        var loading = parts[0] == "1";
                        Debug.WriteLine($"[BPM] NavState: loading={loading}");
                        LoadingStateChanged?.Invoke(loading);
                    }
                    break;
                case "TitleChanged":
                    Debug.WriteLine($"[BPM] TitleChanged: {arg}");
                    TitleChanged?.Invoke(arg);
                    break;
                default:
                    Debug.WriteLine($"[BPM] Unknown command: {cmd}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BPM] DispatchPipeMessage error: {ex.GetType().Name}: {ex.Message}");
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

    private Task SendAsync(string cmd, string arg)
    {
        if (writer == null)
        {
            Debug.WriteLine($"[BPM] Send SKIP (writer null): {cmd}|{arg}");
            return Task.CompletedTask;
        }
        Debug.WriteLine($"[BPM] Send: {cmd}|{arg}");
        _sendChannel.Writer.TryWrite($"{cmd}|{arg}");
        return Task.CompletedTask;
    }

    private void Kill()
    {
        Debug.WriteLine($"[BPM] Kill called");
        _sendChannel.Writer.TryComplete();
        _recvChannel.Writer.TryComplete();

        if (browserProcess != null)
        {
            try
            {
                if (!browserProcess.HasExited)
                {
                    Debug.WriteLine($"[BPM] Killing process PID={browserProcess.Id}");
                    browserProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BPM] Kill process error: {ex.GetType().Name}: {ex.Message}");
            }
            browserProcess.Dispose();
            browserProcess = null;
        }

        if (pipe != null)
        {
            Debug.WriteLine($"[BPM] Disposing pipe");
            pipe.Dispose();
            pipe = null;
            reader = null;
            writer = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        Debug.WriteLine($"[BPM] Dispose");
        disposed = true;
        Kill();
    }
}
