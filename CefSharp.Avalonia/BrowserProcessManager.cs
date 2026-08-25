using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Thin per-browser facade over the shared <see cref="CefHostProcess"/>.
/// One instance per WebView; owns a browser id inside the shared native host.
/// Public API unchanged relative to the legacy per-tab-process implementation,
/// so WebView.cs and consumers need no changes.
///
/// Protocol v2 (all commands carry the browser id):
///   C#→Native: Create|{id}|{cachePath}|{url}, Navigate|{id}|{url}, Reload|{id},
///              Stop|{id}, Resize|{id}|{w}|{h}, EmbedDone|{id}, Close|{id}, Quit
///   Native→C#: Ready|{id}|{hwnd}, AddressChanged|{id}|{url}, ... (routed here by id)
/// </summary>
public sealed class BrowserProcessManager
{
    private readonly string exePath;
    private CefHostProcess? _host;
    private int _browserId;
    private string? lastUrl;
    private bool _ipcFrozen;
    private bool _crashReported;
    private int _lastWidth = 1024;
    private int _lastHeight = 768;

    public string? LastUrl => lastUrl;

    /// <summary>Raised on AddressChanged event from native host.</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised on page load error. Arg: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when the host process exits unexpectedly.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised when this browser's HWND is ready.</summary>
    public event Action<IntPtr>? WindowHandleReceived;
    /// <summary>Raised when page title changes.</summary>
    public event Action<string>? TitleChanged;
    public event Action<string>? OpenPopup;
    /// <summary>Raised when loading state changes (true=loading, false=done).</summary>
    public event Action<bool>? LoadingStateChanged;

    public BrowserProcessManager()
    {
        exePath = GetCefPath("CefBrowser.Native.exe");
        Debug.WriteLine($"[BPM] Shared host exe: {exePath}");
    }

    private static string GetCefPath(string name)
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
    /// Registers with the shared host and creates THIS browser instance.
    /// Process-level settings come from the first registrant; CachePath is
    /// per-browser (becomes an isolated RequestContext in the native host).
    /// </summary>
    public Task StartAsync(string url, CefSettings? settings = null)
    {
        if (_host != null) return Task.CompletedTask;
        settings ??= new CefSettings { NoSandbox = true };

        // Strip --cef-cache-path from PROCESS args: it moves into Create|id (per-browser ctx)
        var allArgs = settings.ToCommandLineArgs();
        var parts = allArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var procParts = new List<string>();
        foreach (var p in parts)
            if (!p.StartsWith("--cef-cache-path=", StringComparison.Ordinal))
                procParts.Add(p);
        var processArgs = procParts.Count > 0 ? string.Join(" ", procParts) : "";

        _host = CefHostProcess.GetOrCreate(exePath);
        _browserId = _host.AddRef(this, processArgs);

        lastUrl = url;
        var cachePath = settings.CachePath ?? "";
        _host.TrySend($"Create|{_browserId}|{cachePath}|{url}");
        Debug.WriteLine($"[BPM] Create id={_browserId} url={url}");
        return Task.CompletedTask;
    }

    /// <summary>Entry point for events routed by <see cref="CefHostProcess"/> (already id-filtered).</summary>
    internal void HandleHostEvent(string ev, string arg)
    {
        switch (ev)
        {
            case "Ready":
                if (!string.IsNullOrEmpty(arg) &&
                    long.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var hwnd))
                {
                    Console.Error.WriteLine($"DIAG: BPM[{_browserId}] Ready HWND={arg}");
                    WindowHandleReceived?.Invoke(new IntPtr(hwnd));
                }
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
                Debug.WriteLine($"[BPM {_browserId}] Unknown event: {ev}");
                break;
        }
    }

    /// <summary>Shared host exited (crash or external kill). Raises BrowserCrashed once.</summary>
    internal void OnHostExited()
    {
        if (_crashReported || _disposed) return;
        _crashReported = true;
        BrowserCrashed?.Invoke();
    }

    public Task NavigateAsync(string url)
    {
        lastUrl = url;
        SendChecked("Navigate", url);
        return Task.CompletedTask;
    }

    public Task ReloadAsync() { SendChecked("Reload", ""); return Task.CompletedTask; }
    public Task StopAsync() { SendChecked("Stop", ""); return Task.CompletedTask; }
    public Task SendEmbedDoneAsync() { SendChecked("EmbedDone", ""); return Task.CompletedTask; }
    public Task SendResizeAsync(int w, int h) { SendResize(w, h); return Task.CompletedTask; }

    public void SendResize(int w, int h)
    {
        _lastWidth = w;
        _lastHeight = h;
        SendChecked("Resize", $"{w}|{h}");
    }

    private void SendChecked(string cmd, string arg)
    {
        if (_host == null || _browserId <= 0 || _ipcFrozen) return;
        _host.TrySend($"{cmd}|{_browserId}|{arg}");
    }

    public void FreezeIpc()
    {
        _ipcFrozen = true;
        Debug.WriteLine($"[BPM {_browserId}] IPC frozen");
    }

    public async Task ResumeIpcAsync()
    {
        _ipcFrozen = false;
        Debug.WriteLine($"[BPM {_browserId}] IPC resuming");
        await SendResizeAsync(_lastWidth, _lastHeight);
    }

    private bool _disposed;

    /// <summary>
    /// Permanently closes THIS browser (native side destroys its renderer chain),
    /// releases its slot in the shared host. The host process itself exits only
    /// when the LAST browser releases. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_host != null && _browserId > 0)
            {
                _host.TrySend($"Close|{_browserId}");
                _host.Release(_browserId);
            }
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Debug.WriteLine($"[BPM] Dispose error: {ex.Message}"); }

        _host = null;
        _browserId = 0;
    }
}
