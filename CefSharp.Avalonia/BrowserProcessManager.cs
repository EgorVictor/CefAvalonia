using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Manages the CefBrowser.Native.exe subprocess lifecycle and Named Pipe IPC.
/// Commands flow C#→C++ via _sendChannel→writer thread; events flow C++→C# via reader→_recvChannel→dispatch.
/// Bounded channels (DropOldest) prevent unbounded memory growth when the pipe is backed up.
/// </summary>
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

    // Bounded channels with DropOldest: if the pipe is slow, stale messages drop instead of piling up.
    private static BoundedChannelOptions BufOpts() => new(256) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
    };
    private readonly Channel<string> _sendChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _writerTask;
    private readonly Channel<string> _recvChannel = Channel.CreateBounded<string>(BufOpts());
    private Task? _recvTask;

    /// <summary>Raised on AddressChanged event from native process.</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised on page load error. Arg: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when the native process exits unexpectedly.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised on Ready event with the CEF browser HWND (hex string).</summary>
    public event Action<IntPtr>? WindowHandleReceived;
    /// <summary>Raised on NavState event. Bool = isLoading.</summary>
    public event Action<bool>? LoadingStateChanged;
    /// <summary>Raised on TitleChanged event from native process.</summary>
    public event Action<string>? TitleChanged;

    public BrowserProcessManager()
    {
        pipeName = $"CefAvalonia_{Environment.ProcessId}";
        exePath = ResolveExePath();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        Kill();
    }

    /// <summary>
    /// Locates CefBrowser.Native.exe: first alongside the managed assembly, then walks up 5 dir levels
    /// looking for CefBrowser.Native\build\Release\ (useful during development).
    /// </summary>
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

    /// <summary>
    /// Launches CefBrowser.Native.exe with pipe/url/pid arguments and CefSettings serialized as --cef-* args.
    /// Waits for the named pipe connection (retries up to 15s).
    /// </summary>
    public async Task StartAsync(string url, CefSettings? settings = null)
    {
        lastUrl = url;

        var settingsArgs = settings?.ToCommandLineArgs() ?? "";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--cef-pipe={pipeName} --cef-url={url} --cef-host-pid={Environment.ProcessId}{settingsArgs}",
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

        browserProcess.EnableRaisingEvents = true;
        browserProcess.Exited += (_, _) =>
        {
            if (!disposed)
                BrowserCrashed?.Invoke();
        };

        await ConnectPipeAsync();
    }

    /// <summary>
    /// Connects to the named pipe created by CefBrowser.Native.exe.
    /// Starts the writer/reader/dispatch background tasks once connected.
    /// Retries every 500ms for up to 15s (the native process needs time to create CEF browser + pipe).
    /// </summary>
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

    /// <summary>
    /// Background writer: reads from _sendChannel and writes to the pipe.
    /// All SendAsync calls are non-blocking — they enqueue to the channel.
    /// </summary>
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

    /// <summary>
    /// Background reader: reads lines from the pipe and enqueues to _recvChannel.
    /// When the pipe breaks (process exit), fires BrowserCrashed unless disposed.
    /// </summary>
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

    /// <summary>
    /// Background dispatcher: reads from _recvChannel and calls DispatchPipeMessage.
    /// Separate from ReadPipeLoopAsync so deserialization doesn't block pipe I/O.
    /// </summary>
    private async Task ProcessRecvChannelAsync()
    {
        try
        {
            await foreach (var msg in _recvChannel.Reader.ReadAllAsync())
                DispatchPipeMessage(msg);
        }
        catch { }
    }

    /// <summary>
    /// Parses a pipe message line ("Cmd|arg") and fires the corresponding event.
    /// Supported commands: Ready, AddressChanged, LoadError, NavState, TitleChanged.
    /// </summary>
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

    /// <summary>Send Navigate command to the native process.</summary>
    public Task NavigateAsync(string url)
    {
        lastUrl = url;
        return SendAsync("Navigate", url);
    }

    /// <summary>Send Reload command.</summary>
    public Task ReloadAsync() => SendAsync("Reload", "");
    /// <summary>Send Stop command.</summary>
    public Task StopAsync() => SendAsync("Stop", "");
    /// <summary>Notify native process that HWND embedding is complete (shows the browser window).</summary>
    public Task SendEmbedDoneAsync() => SendAsync("EmbedDone", "");
    /// <summary>Send resize notification to the native process.</summary>
    public Task SendResizeAsync(int w, int h) => SendAsync("Resize", $"{w}|{h}");

    /// <summary>
    /// Enqueue a command to the send channel. Non-blocking: the background writer thread
    /// handles actual pipe I/O. If the channel is full, the oldest pending message is dropped.
    /// </summary>
    private Task SendAsync(string cmd, string arg)
    {
        if (writer == null)
            return Task.CompletedTask;
        _sendChannel.Writer.TryWrite($"{cmd}|{arg}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forcefully terminates the native process and cleans up pipe resources.
    /// Channels are completed so background tasks exit cleanly.
    /// </summary>
    private void Kill()
    {
        _sendChannel.Writer.TryComplete();
        _recvChannel.Writer.TryComplete();

        if (browserProcess != null)
        {
            try
            {
                if (!browserProcess.HasExited)
                {
                    browserProcess.Kill(entireProcessTree: true);
                    browserProcess.WaitForExit(3000);
                }
            }
            catch
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
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Kill();
    }
}
