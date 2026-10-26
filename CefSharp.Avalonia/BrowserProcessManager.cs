using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Per-browser facade over the shared <see cref="CefHostProcess"/>.
/// One instance per WebView; owns a browser id inside the shared native host.
///
/// Protocol v3 (all commands carry the browser id; text fields are escaped):
///   C#→Native: Create|{id}|{cachePath}|{url}, Navigate|{id}|{url}, Reload|{id},
///              Stop|{id}, Resize|{id}|{w}|{h}, EmbedDone|{id}, Close|{id}, Quit,
///              Back|{id}, Forward|{id}, Zoom|{id}|{delta}, Find|{id}|{text}|{f}|{n},
///              StopFind|{id}, DevTools|{id}, ExecJS|{id}|{base64},
///              JsDialogResult|{dialogId}|{accept}|{text}
///   Native→C#: Hello|3, Ready|{id}|{hwnd}, AddressChanged|{id}|{url},
///              LoadError|{id}|{code}|{text}|{url}, NavState|{id}|{l}|{b}|{f},
///              TitleChanged|{id}|{title}, OpenPopup|{id}|{url},
///              JsDialog|{id}|{dialogId}|{type}|{message}|{default},
///              Download*|{id}|...
/// </summary>
public sealed class BrowserProcessManager : IDisposable
{
    private static readonly string NativeExeName = OperatingSystem.IsWindows()
        ? "CefBrowser.Native.exe"
        : "CefBrowser.Native";

    private readonly string exePath;
    private readonly object _state = new();
    private readonly object _eventGate = new();

    private CefHostProcess? _host;
    private int _browserId;
    private CefSettings? _settings;
    private bool _disposed;
    private bool _crashReported;

    private string? lastUrl;
    private int _lastWidth = 100;
    private int _lastHeight = 100;

    public BrowserProcessManager()
    {
        exePath = GetCefPath(NativeExeName);
        CefDiagnostics.Info($"[BPM] native host: {exePath}");
    }

    /// <summary>Last URL requested by the application or reported by the browser.</summary>
    public string? LastUrl => lastUrl;

    /// <summary>
    /// When set, this browser gets its own CEF request context and cache
    /// directory (isolated cookies/storage). Leave null to share the host's
    /// default profile.
    /// </summary>
    public string? IsolatedCachePath { get; set; }

    private bool _canGoBack;
    /// <summary>True when the browser history has an entry behind the current page.</summary>
    public bool CanGoBack
    {
        get => _canGoBack;
        private set
        {
            if (_canGoBack == value) return;
            _canGoBack = value;
            NavigationStateChanged?.Invoke();
        }
    }

    private bool _canGoForward;
    /// <summary>True when the browser history has an entry ahead of the current page.</summary>
    public bool CanGoForward
    {
        get => _canGoForward;
        private set
        {
            if (_canGoForward == value) return;
            _canGoForward = value;
            NavigationStateChanged?.Invoke();
        }
    }

    /// <summary>Raised when CanGoBack / CanGoForward change.</summary>
    public event Action? NavigationStateChanged;

    /// <summary>Raised on AddressChanged event from native host (already unescaped).</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised on page load error. Arg: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when the host process exits unexpectedly.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised when this browser's HWND is ready.</summary>
    public event Action<IntPtr>? WindowHandleReceived;
    /// <summary>Raised when page title changes.</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Raised when a page requests a popup. Open it in a new tab.</summary>
    public event Action<string>? OpenPopup;
    /// <summary>Raised when loading state changes (true=loading, false=done).</summary>
    public event Action<bool>? LoadingStateChanged;
    /// <summary>Raised on download lifecycle events.</summary>
    public event Action<string>? DownloadEvent;

    /// <summary>
    /// Raised when the page opens a JavaScript dialog. The handler must call
    /// <see cref="CefJsDialog.Accept"/> or <see cref="CefJsDialog.Dismiss"/>.
    /// If there is no subscriber the dialog is answered with a safe default.
    /// </summary>
    public event Action<CefJsDialog>? JsDialogRequested;

    /// <summary>
    /// Start this browser inside the shared host. Process-level settings are
    /// taken from the first caller (the host is shared).
    /// </summary>
    public async Task<bool> StartAsync(string url, CefSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host != null && _browserId > 0) return true;
        }

        settings ??= new CefSettings();
        _settings = settings;
        lastUrl = url;

        CefHostProcess host;
        int browserId;
        try
        {
            host = CefHostProcess.GetOrCreate(exePath);
            browserId = host.AddRef(this, settings.ToArgumentList());
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error($"[BPM] failed to start native host: {ex.Message}", ex);
            return false;
        }

        lock (_state)
        {
            _host = host;
            _browserId = browserId;
        }

        string cachePath = CefProtocol.Escape(IsolatedCachePath);
        string targetUrl = CefProtocol.Escape(url);
        bool created = await host.SendAsync(
            $"Create|{browserId}|{cachePath}|{targetUrl}", cancellationToken).ConfigureAwait(false);

        if (!created)
            CefDiagnostics.Error($"[BPM {browserId}] Create command was not delivered");

        return created;
    }

    /// <summary>
    /// Restart this browser on a fresh host process after a crash, preserving
    /// the last known URL. Returns false when recovery failed.
    /// </summary>
    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        CefHostProcess? oldHost;
        int oldId;

        lock (_state)
        {
            if (_disposed) return false;
            oldHost = _host;
            oldId = _browserId;
            _host = null;
            _browserId = 0;
            _crashReported = false;
        }

        if (oldHost != null && oldId > 0)
            oldHost.Release(oldId);

        string url = string.IsNullOrEmpty(lastUrl) ? "about:blank" : lastUrl;
        return await StartAsync(url, _settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Entry point for events routed by <see cref="CefHostProcess"/> (id already stripped).</summary>
    internal void HandleHostEvent(string ev, string arg)
    {
        switch (ev)
        {
            case "Ready":
                if (!string.IsNullOrEmpty(arg) &&
                    long.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out long hwnd))
                {
                    CefDiagnostics.Info($"[BPM {_browserId}] Ready HWND=0x{hwnd:X}");
                    WindowHandleReceived?.Invoke(new IntPtr(hwnd));
                }
                break;

            case "AddressChanged":
                lastUrl = CefProtocol.Unescape(arg);
                AddressChanged?.Invoke(lastUrl);
                break;

            case "LoadError":
            {
                // "code|text|url" with text/url escaped by the native side.
                string[] p = CefProtocol.Split(arg, 3);
                if (p.Length >= 1)
                {
                    string info = p.Length >= 2
                        ? $"{p[0]}|{p[1]}|{(p.Length >= 3 ? p[2] : "")}"
                        : arg;
                    LoadError?.Invoke(info);
                }
                break;
            }

            case "NavState":
            {
                string[] parts = CefProtocol.Split(arg, 3);
                if (parts.Length >= 3)
                {
                    CanGoBack = parts[1] == "1";
                    CanGoForward = parts[2] == "1";
                }
                if (parts.Length >= 1)
                    LoadingStateChanged?.Invoke(parts[0] == "1");
                break;
            }

            case "TitleChanged":
                TitleChanged?.Invoke(CefProtocol.Unescape(arg));
                break;

            case "OpenPopup":
                OpenPopup?.Invoke(CefProtocol.Unescape(arg));
                break;

            case "DownloadStart":
            case "DownloadComplete":
            case "DownloadCanceled":
            {
                string path = CefProtocol.Unescape(arg);
                DownloadEvent?.Invoke(path.Length == 0 ? ev : ev + "|" + path);
                break;
            }

            case "DownloadProgress":
            {
                string[] p = CefProtocol.Split(arg, 2);
                DownloadEvent?.Invoke($"{ev}|{(p.Length > 0 ? p[0] : "0")}|{(p.Length > 1 ? p[1] : "0")}");
                break;
            }

            case "JsDialog":
                HandleJsDialog(arg);
                break;

            case "Pong":
                CefDiagnostics.Debug($"[BPM {_browserId}] pong {arg}");
                break;

            default:
                CefDiagnostics.Debug($"[BPM {_browserId}] unknown event: {ev}");
                break;
        }
    }

    private void HandleJsDialog(string arg)
    {
        // arg = "{dialogId}|{type}|{message}|{defaultText}"
        string[] f = CefProtocol.Split(arg, 4);
        if (f.Length < 4 || !int.TryParse(f[0], out int dialogId))
        {
            CefDiagnostics.Warn($"[BPM {_browserId}] malformed JsDialog: {arg}");
            return;
        }

        var dialog = new CefJsDialog(
            dialogId,
            f[1],
            f[2],
            f[3],
            (id, accept, text) => _ = SendAsync("JsDialogResult", null, default,
                $"{id}|{(accept ? "1" : "0")}|{CefProtocol.Escape(text)}"));

        Action<CefJsDialog>? handler = JsDialogRequested;
        if (handler == null)
        {
            // Nobody is listening: never leave a page blocked on a dialog.
            if (dialog.IsAlert) dialog.Accept();
            else dialog.Dismiss();
            return;
        }

        try
        {
            handler(dialog);
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error($"[BPM] JsDialog handler failed: {ex.Message}", ex);
            dialog.Dismiss();
        }
    }

    // ---- Navigation -------------------------------------------------------

    /// <summary>Navigate to a URL. Returns false when the command was not delivered.</summary>
    public Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        lastUrl = url;
        return SendAsync("Navigate", CefProtocol.Escape(url), cancellationToken);
    }

    public Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
        => SendAsync("Reload", null, cancellationToken);

    public Task<bool> StopAsync(CancellationToken cancellationToken = default)
        => SendAsync("Stop", null, cancellationToken);

    public void GoBack() => _ = SendAsync("Back", null, default);
    public void GoForward() => _ = SendAsync("Forward", null, default);

    // ---- Zoom -------------------------------------------------------------

    public void ZoomIn() => _ = SendAsync("Zoom", null, default, "1");
    public void ZoomOut() => _ = SendAsync("Zoom", null, default, "-1");
    public void ZoomReset() => _ = SendAsync("Zoom", null, default, "0");

    // ---- Find in page -----------------------------------------------------

    public void Find(string text, bool forward = true, bool findNext = false)
        => _ = SendAsync("Find", null, default,
            $"{CefProtocol.Escape(text)}|{(forward ? "1" : "0")}|{(findNext ? "1" : "0")}");

    public void StopFinding() => _ = SendAsync("StopFind", null, default);

    // ---- Script / dev tools ----------------------------------------------

    public void ExecuteJavaScript(string code)
    {
        if (string.IsNullOrEmpty(code)) return;
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(code));
        _ = SendAsync("ExecJS", b64, default);
    }

    public void ShowDevTools() => _ = SendAsync("DevTools", null, default);

    // ---- Window -----------------------------------------------------------

    public void SendResize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        _lastWidth = width;
        _lastHeight = height;
        _ = SendAsync("Resize", null, default, $"{width}|{height}");
    }

    public Task<bool> SendResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        if (width <= 0 || height <= 0) return Task.FromResult(false);
        _lastWidth = width;
        _lastHeight = height;
        return SendAsync("Resize", null, cancellationToken, $"{width}|{height}");
    }

    public Task<bool> SendEmbedDoneAsync(CancellationToken cancellationToken = default)
        => SendAsync("EmbedDone", null, cancellationToken);

    // ---- Internals --------------------------------------------------------

    private Task<bool> SendAsync(string command, string? argument, CancellationToken cancellationToken,
        string? overrideArgument = null)
    {
        CefHostProcess? host;
        int id;

        lock (_state)
        {
            if (_disposed) return Task.FromResult(false);
            host = _host;
            id = _browserId;
        }

        if (host == null || id <= 0) return Task.FromResult(false);

        string? payload = overrideArgument ?? argument;
        string line = payload == null ? $"{command}|{id}" : $"{command}|{id}|{payload}";
        return host.SendAsync(line, cancellationToken);
    }

    /// <summary>Shared host exited (crash or external kill). Raises BrowserCrashed once.</summary>
    internal void OnHostExited(string reason)
    {
        lock (_state)
        {
            if (_crashReported || _disposed) return;
            _crashReported = true;
            _host = null;
            _browserId = 0;
        }

        CefDiagnostics.Error($"[BPM] browser host exited: {reason}");
        BrowserCrashed?.Invoke();
    }

    /// <summary>
    /// Permanently closes THIS browser and releases its slot. The host process
    /// itself exits on application shutdown (see <see cref="CefHostProcess.ShutdownAll"/>).
    /// Idempotent.
    /// </summary>
    public void Dispose()
    {
        CefHostProcess? host;
        int id;

        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            host = _host;
            id = _browserId;
            _host = null;
            _browserId = 0;
        }

        lock (_eventGate)
        {
            // Drop subscribers so late events cannot touch a disposed control.
            AddressChanged = null;
            TitleChanged = null;
            LoadingStateChanged = null;
            NavigationStateChanged = null;
            LoadError = null;
            OpenPopup = null;
            DownloadEvent = null;
            BrowserCrashed = null;
            WindowHandleReceived = null;
            JsDialogRequested = null;
        }

        if (host == null || id <= 0) return;

        _ = host.SendAsync($"Close|{id}", default, TimeSpan.FromSeconds(2));
        host.Release(id);
    }

    private static string GetCefPath(string name)
    {
        string local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;

        // Published apps must not silently pick up a developer build from an
        // unrelated directory. The fallback is intentionally DEBUG-only.
#if DEBUG
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            dir = Path.GetDirectoryName(dir) ?? "";
            if (dir.Length == 0) break;

            string candidate = Path.Combine(dir, "CefBrowser.Native", "build", "Release", name);
            if (File.Exists(candidate)) return candidate;

            candidate = Path.Combine(dir, "CefBrowser.Native", "build", name);
            if (File.Exists(candidate)) return candidate;
        }
#endif

        return local;
    }
}
