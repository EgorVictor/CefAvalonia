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
    }

    private static string ResolveExePath()
    {
        var name = "CefBrowser.Native.exe";
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

    public async Task StartAsync(string url, string[]? cefArgs = null)
    {
        lastUrl = url;

        var extra = cefArgs != null ? " " + string.Join(" ", cefArgs) : "";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--cef-pipe={pipeName} --cef-url={url} --cef-host-pid={Environment.ProcessId}{extra}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["CEF_PIPE"] = pipeName;
        psi.EnvironmentVariables["CEF_URL"] = url;
        psi.EnvironmentVariables["CEF_HOST_PID"] = Environment.ProcessId.ToString();

        browserProcess = Process.Start(psi);
        if (browserProcess == null)
            throw new InvalidOperationException("Failed to start browser process");

        // Subscribe to process exit for crash detection
        browserProcess.EnableRaisingEvents = true;
        browserProcess.Exited += (_, _) =>
        {
            if (!disposed)
                BrowserCrashed?.Invoke();
        };

        await ConnectPipeAsync();
    }

    private async Task ConnectPipeAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000);
                pipe.ReadMode = System.IO.Pipes.PipeTransmissionMode.Message;
                reader = new StreamReader(pipe);
                writer = new StreamWriter(pipe) { AutoFlush = true };

                _writerTask = Task.Run(RunWriterAsync);
                _recvTask = Task.Run(ProcessRecvChannelAsync);
                _ = ReadPipeLoopAsync();

                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        throw new TimeoutException("Failed to connect to browser process via named pipe");
    }

    private async Task RunWriterAsync()
    {
        try
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync())
            {
                if (writer == null) break;
                await writer.WriteLineAsync(msg);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadPipeLoopAsync()
    {
        try
        {
            while (!disposed && reader != null)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                _recvChannel.Writer.TryWrite(line);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!disposed)
            BrowserCrashed?.Invoke();
    }

    private async Task ProcessRecvChannelAsync()
    {
        try
        {
            await foreach (var msg in _recvChannel.Reader.ReadAllAsync())
                DispatchPipeMessage(msg);
        }
        catch { }
    }

    private void DispatchPipeMessage(string line)
    {
        var sep = line.IndexOf('|');
        var cmd = sep >= 0 ? line[..sep] : line;
        var arg = sep >= 0 ? line[(sep + 1)..] : "";

        try
        {
            switch (cmd)
            {
                case "Ready":
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
                    if (parts.Length == 3)
                        LoadingStateChanged?.Invoke(parts[0] == "1");
                    break;
                case "TitleChanged":
                    TitleChanged?.Invoke(arg);
                    break;
            }
        }
        catch { }
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
            return Task.CompletedTask;
        _sendChannel.Writer.TryWrite($"{cmd}|{arg}");
        return Task.CompletedTask;
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
            catch (Exception ex)
            {
            }
            browserProcess.Dispose();
            browserProcess = null;
        }

        if (pipe != null)
        {
            pipe.Dispose();
            pipe = null;
            reader = null;
            writer = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Kill();
    }
}
