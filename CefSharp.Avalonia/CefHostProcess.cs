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
/// Shared native host process (Chrome-style): ONE CefBrowser.Native.exe hosts
/// N browser instances. Shared across browsers: GPU process, network service,
/// storage service. Isolated per browser: renderer + optional cache dir.
/// Ref-counted: the process starts on first AddRef and exits after Quit when
/// the last browser releases (grace period 5s, then hard kill).
/// Protocol v2: every command/event carries a browser id.
/// </summary>
public sealed class CefHostProcess : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CefHostProcess> Instances = new();

    /// <summary>Get or create the shared host for a given native exe path.</summary>
    public static CefHostProcess GetOrCreate(string exePath)
    {
        lock (Gate)
        {
            var key = Path.GetFullPath(exePath);
            if (!Instances.TryGetValue(key, out var host))
            {
                host = new CefHostProcess(exePath);
                Instances[key] = host;
            }
            return host;
        }
    }

    private readonly string _exePath;
    private readonly Dictionary<int, BrowserProcessManager> _sinks = new();
    private readonly Channel<string> _sendChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId;
    private int _refs;
    private Task? _writerTask;
    private Task? _readerTask;
    private static readonly string DiagLogPath = Path.Combine(AppContext.BaseDirectory, "cef_browser_diag.log");

    private CefHostProcess(string exePath)
    {
        _exePath = exePath;
    }

    /// <summary>
    /// Register a sink and allocate its browser id. Starts the host process on
    /// the first registration using the supplied process-level args
    /// (--cef-cache-path must be stripped by the caller; it is per-browser now).
    /// </summary>
    public int AddRef(BrowserProcessManager sink, string processArgs)
    {
        int id;
        lock (_sinks)
        {
            id = ++_nextId;
            _sinks[id] = sink;
            _refs++;

            if (_process == null)
                StartProcess(processArgs);
        }
        return id;
    }

    private void StartProcess(string processArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            Arguments = processArgs,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        _process = Process.Start(psi);
        if (_process == null)
            throw new InvalidOperationException("Failed to start CefBrowser.Native.exe host process");

        _stdin = _process.StandardInput;

        // stderr → diag file (native DIAG logs are gated by CEF_DIAG=1)
        _ = Task.Run(async () =>
        {
            try
            {
                using var err = _process.StandardError;
                string? line;
                while ((line = await err.ReadLineAsync()) != null)
                {
                    try { await File.AppendAllTextAsync(DiagLogPath, line + "\n"); } catch { }
                }
            }
            catch { }
        });

        _writerTask = Task.Run(WriterThreadProc);
        _readerTask = Task.Run(ReaderThreadProc);

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            Debug.WriteLine("[CefHost] Host process exited");
            BroadcastCrashed();
        };
    }

    private async Task WriterThreadProc()
    {
        try
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync())
            {
                if (_stdin == null) break;
                try
                {
                    await _stdin.WriteLineAsync(msg);
                    Debug.WriteLine($"[CefHost] Sent: {msg}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CefHost] Write error: {ex.Message}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CefHost] WriterThread error: {ex}");
        }
    }

    private async Task ReaderThreadProc()
    {
        try
        {
            var stdout = _process!.StandardOutput;
            string? line;
            while ((line = await stdout.ReadLineAsync()) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;
                Dispatch(line);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CefHost] ReaderThread error: {ex.Message}");
        }

        BroadcastCrashed();
    }

    /// <summary>Parse "Event|{id}|{rest...}" and route to the owning sink.</summary>
    private void Dispatch(string line)
    {
        try
        {
            var sep1 = line.IndexOf('|');
            if (sep1 < 0) return;
            var ev = line[..sep1];

            var sep2 = line.IndexOf('|', sep1 + 1);
            if (sep2 < 0) return;
            if (!int.TryParse(line[(sep1 + 1)..sep2], out var id)) return;
            var rest = sep2 + 1 <= line.Length ? line[(sep2 + 1)..] : "";

            BrowserProcessManager? sink;
            lock (_sinks)
            {
                _sinks.TryGetValue(id, out sink);
            }
            sink?.HandleHostEvent(ev, rest);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CefHost] Dispatch error: {ex} line={line}");
        }
    }

    internal void TrySend(string line) => _sendChannel.Writer.TryWrite(line);

    private void BroadcastCrashed()
    {
        BrowserProcessManager[] sinks;
        lock (_sinks)
        {
            sinks = new BrowserProcessManager[_sinks.Count];
            _sinks.Values.CopyTo(sinks, 0);
        }
        foreach (var s in sinks)
            s.OnHostExited();
    }

    /// <summary>
    /// Unregister a browser. The host process itself is NOT terminated here:
    /// it lives until an explicit <see cref="RequestShutdown"/> or stdin EOF
    /// (parent app process death). Closing the last tab just frees its browser,
    /// leaving the shared host ready for the next tab.
    /// </summary>
    public void Release(int id)
    {
        lock (_sinks)
        {
            _sinks.Remove(id);
            _refs--;
        }
    }

    /// <summary>
    /// Explicit whole-host shutdown (e.g. app-level teardown): tells native to
    /// close all browsers and exit. Normally unnecessary — stdin EOF on process
    /// exit triggers the same clean path.
    /// </summary>
    public void RequestShutdown()
    {
        TrySend("Quit");
        _sendChannel.Writer.TryComplete();
    }

    public void Dispose()
    {
        // Force path: only used in exceptional situations.
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch { }
        lock (Gate)
        {
            var key = Path.GetFullPath(_exePath);
            if (Instances.TryGetValue(key, out var cur) && cur == this)
                Instances.Remove(key);
        }
    }
}
