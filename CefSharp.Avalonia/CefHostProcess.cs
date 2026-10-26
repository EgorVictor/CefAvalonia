using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Shared native host process (Chrome-style): ONE CefBrowser.Native executable
/// hosts N browser instances. Shared across browsers: GPU process, network
/// service, storage service. Isolated per browser: renderer + optional cache dir.
///
/// Lifecycle:
///   * The process starts on the first <see cref="AddRef"/> and stays alive while
///     the application is running (closing a tab only releases its slot).
///   * If the process dies (crash, external kill) every registered sink is
///     notified once and the host instance is discarded, so the next
///     <see cref="GetOrCreate"/> starts a brand new process.
/// </summary>
public sealed class CefHostProcess : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CefHostProcess> Instances =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed class Outbound
    {
        public Outbound(string line, bool awaitWrite)
        {
            Line = line;
            Completion = awaitWrite
                ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public string Line { get; }
        public TaskCompletionSource<bool>? Completion { get; }
    }

    private readonly string _exePath;
    private readonly object _sync = new();
    private readonly Dictionary<int, BrowserProcessManager> _sinks = new();

    private Channel<Outbound>? _channel;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _writerTask;
    private Task? _readerTask;
    private int _nextId;
    private int _refs;
    private int _generation;
    private bool _disposed;
    /// <summary>Process-level arguments of the first (host-creating) browser, used to warn on mismatches.</summary>
    private string? _initialProcessArgs;
    private bool _shutdownRequested;
    /// <summary>Protocol version reported by the native host, or 0 before the handshake.</summary>
    public int ProtocolVersion { get; private set; }

    private CefHostProcess(string exePath)
    {
        _exePath = exePath;
    }

    static CefHostProcess()
    {
        // Last-resort cleanup so a consumer that forgets to call ShutdownAll does
        // not leave an orphaned CefBrowser.Native (and its renderer children).
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { ShutdownAll(1500); }
            catch { /* process is going away */ }
        };
    }

    /// <summary>True while the native process is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return !_disposed && _process is { HasExited: false };
        }
    }

    /// <summary>
    /// Get the shared host for a native executable path, creating a fresh
    /// instance when the previous one has crashed or been disposed.
    /// </summary>
    public static CefHostProcess GetOrCreate(string exePath)
    {
        string key = Path.GetFullPath(exePath);
        lock (Gate)
        {
            if (Instances.TryGetValue(key, out CefHostProcess? existing) && existing.IsRunning)
                return existing;

            // A dead instance is replaced. Its Exited handler is generation-guarded,
            // so it will not disturb the new instance.
            var host = new CefHostProcess(exePath);
            Instances[key] = host;
            return host;
        }
    }

    /// <summary>
    /// Register a sink and allocate its browser id. Starts the host process on
    /// the first registration using the supplied process-level arguments.
    /// Throws <see cref="InvalidOperationException"/> when the executable cannot
    /// be started.
    /// </summary>
    public int AddRef(BrowserProcessManager sink, IReadOnlyList<string> processArguments)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_process == null || _process.HasExited)
            {
                StartProcessLocked(processArguments);
            }
            else
            {
                // The host is already running on the first WebView's process-level
                // settings. Later browsers that request different process-level
                // settings cannot change them (CEF settings are per-process), so
                // surface the mismatch instead of silently ignoring it.
                string joined = string.Join(" ", processArguments);
                if (_initialProcessArgs != null && _initialProcessArgs != joined)
                {
                    CefDiagnostics.Warn(
                        $"[CefHost] process-level CefSettings from this WebView differ from the first " +
                        $"instance; only the first instance's settings take effect. this=[{joined}] first=[{_initialProcessArgs}]");
                }
            }

            int id = ++_nextId;
            _sinks[id] = sink;
            _refs++;
            return id;
        }
    }

    private void StartProcessLocked(IReadOnlyList<string> processArguments)
    {
        // Discard state belonging to the previous (dead) process.
        CleanupProcessLocked();

        if (!File.Exists(_exePath))
            throw new FileNotFoundException(
                $"Native browser host not found: {_exePath}. Build CefBrowser.Native or set the native binaries path.",
                _exePath);

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        foreach (string arg in processArguments)
            psi.ArgumentList.Add(arg);

        if (!OperatingSystem.IsWindows())
        {
            TryMakeExecutable(_exePath);

            // libcef.so and friends live next to the native executable.
            string nativeDir = Path.GetDirectoryName(_exePath) ?? AppContext.BaseDirectory;
            string oldLibPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
            psi.Environment["LD_LIBRARY_PATH"] = oldLibPath.Length == 0
                ? nativeDir
                : nativeDir + ":" + oldLibPath;
        }

        Process process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start {Path.GetFileName(_exePath)} host process");

        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = false;
        _shutdownRequested = false;
        ProtocolVersion = 0;
        _initialProcessArgs = string.Join(" ", processArguments);

        Channel<Outbound> channel = Channel.CreateBounded<Outbound>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        _channel = channel;
        int generation = ++_generation;

        _writerTask = Task.Run(() => WriterLoopAsync(generation, channel, _stdin));
        _readerTask = Task.Run(() => ReaderLoopAsync(generation, process));
        _ = Task.Run(() => DrainStderrAsync(generation, process));

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => HandleProcessExit(generation, "native host process exited");

        CefDiagnostics.Info(
            $"[CefHost] started pid={process.Id} exe={_exePath} args={string.Join(' ', processArguments)}");
    }

    private static void TryMakeExecutable(string path)
    {
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            CefDiagnostics.Warn($"[CefHost] chmod failed for {path}: {ex.Message}");
        }
    }

    private async Task WriterLoopAsync(int generation, Channel<Outbound> channel, StreamWriter stdin)
    {
        try
        {
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out Outbound? message))
                {
                    try
                    {
                        await stdin.WriteLineAsync(message.Line).ConfigureAwait(false);
                        await stdin.FlushAsync().ConfigureAwait(false);
                        message.Completion?.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        message.Completion?.TrySetResult(false);
                        CefDiagnostics.Error($"[CefHost] write failed: {ex.Message}");
                        channel.Writer.TryComplete();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (generation == _generation)
                CefDiagnostics.Error($"[CefHost] writer loop failed: {ex.Message}");
        }
        finally
        {
            // Never leave callers awaiting a write that will not happen.
            while (channel.Reader.TryRead(out Outbound? pending))
                pending.Completion?.TrySetResult(false);
        }
    }

    private async Task ReaderLoopAsync(int generation, Process process)
    {
        try
        {
            StreamReader stdout = process.StandardOutput;
            while (true)
            {
                string? line = await stdout.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;

                if (line.StartsWith("Hello|", StringComparison.Ordinal))
                {
                    if (int.TryParse(line.AsSpan(6), out int version))
                    {
                        ProtocolVersion = version;
                        if (version != CefProtocol.Version)
                        {
                            CefDiagnostics.Error(
                                $"[CefHost] protocol mismatch: native={version} managed={CefProtocol.Version}");
                        }
                    }
                    continue;
                }

                Dispatch(line);
            }
        }
        catch (Exception ex)
        {
            CefDiagnostics.Warn($"[CefHost] reader loop ended: {ex.Message}");
        }

        HandleProcessExit(generation, "native host stdout closed");
    }

    private async Task DrainStderrAsync(int generation, Process process)
    {
        try
        {
            StreamReader stderr = process.StandardError;
            while (true)
            {
                string? line = await stderr.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (generation != _generation) return;
                CefDiagnostics.Debug($"[native] {line}");
            }
        }
        catch
        {
            // The process is gone; nothing to do.
        }
    }

    /// <summary>Parse "Event|{id}|{rest...}" and route it to the owning sink.</summary>
    private void Dispatch(string line)
    {
        try
        {
            int sep = line.IndexOf('|');
            if (sep <= 0) return;

            string ev = line[..sep];
            string tail = line[(sep + 1)..];

            // Events may or may not carry a payload: "DownloadCanceled|3" has no
            // rest, while "AddressChanged|3|https://..." does.
            int idSep = tail.IndexOf('|');
            int id;
            string rest;
            if (idSep < 0)
            {
                if (!int.TryParse(tail, out id)) return;
                rest = "";
            }
            else
            {
                if (!int.TryParse(tail.AsSpan(0, idSep), out id)) return;
                rest = tail[(idSep + 1)..];
            }

            BrowserProcessManager? sink;
            lock (_sync)
            {
                _sinks.TryGetValue(id, out sink);
            }

            sink?.HandleHostEvent(ev, rest);
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error($"[CefHost] failed to dispatch '{line}': {ex.Message}");
        }
    }

    private void HandleProcessExit(int generation, string reason)
    {
        BrowserProcessManager[] sinks;
        Process? process;
        StreamWriter? stdin;

        lock (_sync)
        {
            if (generation != _generation) return;
            if (_process == null && _channel == null) return;

            bool expected = _shutdownRequested;

            process = _process;
            stdin = _stdin;
            _process = null;
            _stdin = null;
            _channel?.Writer.TryComplete();
            _channel = null;
            _refs = 0;

            sinks = _sinks.Values.ToArray();
            _sinks.Clear();

            if (!expected)
                CefDiagnostics.Error($"[CefHost] {reason} (pid={(process?.Id.ToString() ?? "?")})");

            try { stdin?.Dispose(); } catch { /* ignore */ }
            try { process?.Dispose(); } catch { /* ignore */ }
        }

        // Removing the instance takes the static Gate. Do it outside _sync so
        // GetOrCreate (Gate -> IsRunning -> _sync) can never deadlock with us.
        RemoveInstance();

        foreach (BrowserProcessManager sink in sinks)
            sink.OnHostExited(reason);
    }

    private void RemoveInstance()
    {
        string key = Path.GetFullPath(_exePath);
        lock (Gate)
        {
            if (Instances.TryGetValue(key, out CefHostProcess? current) && ReferenceEquals(current, this))
                Instances.Remove(key);
        }
    }

    private void CleanupProcessLocked()
    {
        try { _channel?.Writer.TryComplete(); } catch { /* ignore */ }
        try { _stdin?.Dispose(); } catch { /* ignore */ }
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        try { _process?.Dispose(); } catch { /* ignore */ }

        _channel = null;
        _stdin = null;
        _process = null;
    }

    /// <summary>
    /// Fire-and-forget send. Returns false when the host is not running or the
    /// outbound queue is full (the command was <b>not</b> sent).
    /// </summary>
    public bool TrySend(string line)
    {
        Channel<Outbound>? channel = _channel;
        if (channel == null) return false;
        if (channel.Writer.TryWrite(new Outbound(line, awaitWrite: false))) return true;

        CefDiagnostics.Warn("[CefHost] outbound queue is full; command dropped");
        return false;
    }

    /// <summary>
    /// Sends a command and completes when the bytes have been flushed to the
    /// native process. Returns false on timeout or when the host is gone.
    /// </summary>
    public async Task<bool> SendAsync(string line, CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        Channel<Outbound>? channel = _channel;
        if (channel == null) return false;

        var message = new Outbound(line, awaitWrite: true);
        if (!channel.Writer.TryWrite(message))
        {
            CefDiagnostics.Warn("[CefHost] outbound queue is full; command dropped");
            return false;
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(effectiveTimeout);

        try
        {
            return await message.Completion!.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CefDiagnostics.Warn($"[CefHost] command timed out after {effectiveTimeout.TotalSeconds:0.#}s");
            return false;
        }
    }

    /// <summary>Unregister a browser slot. The host process itself stays alive.</summary>
    public void Release(int id)
    {
        lock (_sync)
        {
            if (_sinks.Remove(id))
                _refs--;
        }
    }

    /// <summary>
    /// Ask the native host to close all browsers and exit. Safe to call more
    /// than once; a hard kill is the caller's responsibility (see ShutdownAll).
    /// </summary>
    public void RequestShutdown()
    {
        lock (_sync)
        {
            if (_shutdownRequested) return;
            _shutdownRequested = true;
        }

        // Route Quit through the writer loop: StreamWriter is not thread-safe and
        // must never be written to from two threads at once.
        if (!TrySend("Quit"))
            CefDiagnostics.Debug("[CefHost] Quit could not be queued (host already closing)");
    }

    /// <summary>
    /// Application-exit hook: asks every host to shut down, gives each a short
    /// grace period, then force-kills survivors.
    /// </summary>
    public static void ShutdownAll(int graceMs = 3000)
    {
        CefHostProcess[] hosts;
        lock (Gate)
        {
            hosts = Instances.Values.ToArray();
            Instances.Clear();
        }

        foreach (CefHostProcess host in hosts)
        {
            try { host.RequestShutdown(); }
            catch (Exception ex) { CefDiagnostics.Warn($"[CefHost] shutdown request failed: {ex.Message}"); }
        }

        foreach (CefHostProcess host in hosts)
        {
            try
            {
                Process? process = host._process;
                if (process != null && !process.WaitForExit(graceMs))
                {
                    CefDiagnostics.Warn("[CefHost] process did not exit in time; killing");
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                CefDiagnostics.Warn($"[CefHost] shutdown wait failed: {ex.Message}");
            }

            host.Dispose();
        }
    }

    public void Dispose()
    {
        Process? process;

        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdownRequested = true;

            process = _process;
            CleanupProcessLocked();
        }

        RemoveInstance();

        try { process?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { process?.Dispose(); } catch { /* ignore */ }
    }
}
