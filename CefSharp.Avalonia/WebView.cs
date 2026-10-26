using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CefSharp.Avalonia;

/// <summary>
/// Avalonia control that embeds a real CEF (Chromium) browser.
///
/// The actual browser runs in a separate <c>CefBrowser.Native</c> process,
/// shared by every <see cref="WebView"/> in the application, and its native
/// child window is embedded into this control through <see cref="NativeControlHost"/>.
///
/// Typical usage:
/// <code>
/// var web = new WebView { Url = "https://example.com" };
/// web.JsDialogRequested += dialog => ShowDialog(dialog);
/// parent.Children.Add(web);
/// </code>
///
/// Lifecycle: attach → start native host → embed window. Detaching keeps the
/// browser alive so tab switches stay instant; call <see cref="Dispose"/> (or
/// <see cref="Cleanup"/>) when the tab is permanently closed.
/// </summary>
public class WebView : UserControl, IDisposable
{
    private static readonly bool s_noCef =
        Array.IndexOf(Environment.GetCommandLineArgs(), "--no-cef") >= 0;

    private readonly ExternalBrowserProcessHost _browserHost = new();
    private readonly DispatcherTimer _resizeTimer;

    private BrowserProcessManager? _manager;
    private IntPtr _cefHwnd = IntPtr.Zero;
    private bool _disposed;
    private bool _eventsWired;
    private bool _syncingUrl;
    private int _recoveryAttempts;
    private TopLevel? _topLevel;
    private string _lastFindText = "";

    // OSR (Linux only): the rendered frame and its shared-memory source.
    private readonly OsrSurface _osrSurface = new();
    private WriteableBitmap? _osrBitmap;
    private MemoryMappedFile? _osrMmf;
    private MemoryMappedViewAccessor? _osrView;
    private byte[] _osrPixels = Array.Empty<byte>();
    private bool _browserReady;

    /// <summary>
    /// Single, bindable browser location. Setting it navigates; the value is
    /// updated from the browser whenever the page changes address (redirects,
    /// link clicks, history navigation), so a TwoWay binding stays in sync.
    /// </summary>
    public static readonly StyledProperty<string> UrlProperty =
        AvaloniaProperty.Register<WebView, string>(
            nameof(Url), defaultValue: "", defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Current page URL. Setting this property navigates the browser.</summary>
    public string Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value ?? "");
    }

    private string _title = "";
    public static readonly DirectProperty<WebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<WebView, string>(nameof(Title), o => o.Title);

    /// <summary>Page title reported by the browser.</summary>
    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public static readonly DirectProperty<WebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(nameof(IsLoading), o => o.IsLoading);

    /// <summary>True while the browser is loading a page.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    private bool _canGoBack;
    public static readonly DirectProperty<WebView, bool> CanGoBackProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(nameof(CanGoBack), o => o.CanGoBack);

    /// <summary>True when there is a history entry behind the current page.</summary>
    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetAndRaise(CanGoBackProperty, ref _canGoBack, value);
    }

    private bool _canGoForward;
    public static readonly DirectProperty<WebView, bool> CanGoForwardProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(nameof(CanGoForward), o => o.CanGoForward);

    /// <summary>True when there is a history entry ahead of the current page.</summary>
    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetAndRaise(CanGoForwardProperty, ref _canGoForward, value);
    }

    public static readonly StyledProperty<ICommand?> NavigateCommandProperty =
        AvaloniaProperty.Register<WebView, ICommand?>(nameof(NavigateCommand));

    /// <summary>
    /// Optional command invoked when the control itself requests navigation
    /// (kept for MVVM scenarios; the control never invokes it automatically).
    /// </summary>
    public ICommand? NavigateCommand
    {
        get => GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    /// <summary>
    /// Process-level CEF settings. Because all browsers share one native host,
    /// only the first <see cref="WebView"/> that starts decides them.
    /// Must be assigned before the control is attached to the visual tree.
    /// The sandbox is enabled by default.
    /// </summary>
    public CefSettings CefSettings { get; set; } = new();

    /// <summary>
    /// Optional per-browser cache/profile directory. When set, this browser gets
    /// its own CEF request context (isolated cookies, localStorage, cache), which
    /// is what allows several tabs to run side by side. Leave null to share the
    /// host's default profile.
    /// </summary>
    public string? IsolatedCachePath { get; set; }

    /// <summary>
    /// Restart the browser automatically after the native host dies.
    /// Defaults to <see langword="true"/>; at most three consecutive attempts are
    /// made, and the counter resets after a successful start.
    /// </summary>
    public bool AutoRecover { get; set; } = true;

    /// <summary>Raised when the browser navigates to a new URL.</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised when the page title changes.</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Raised when the loading state changes (true = loading).</summary>
    public event Action<bool>? LoadingStateChanged;
    /// <summary>Raised after the native host died and could not be recovered here.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised on page load error. Format: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when a page requests a popup/new window. Open it in a new tab.</summary>
    public event Action<string>? OpenPopup;
    /// <summary>Raised for download lifecycle events, e.g. "DownloadStart|path".</summary>
    public event Action<string>? DownloadEvent;

    /// <summary>
    /// Raised when the page shows a JavaScript dialog. The handler must call
    /// <see cref="CefJsDialog.Accept"/> or <see cref="CefJsDialog.Dismiss"/>.
    /// Without a handler a safe default answer is sent so the page never hangs.
    /// </summary>
    public event Action<CefJsDialog>? JsDialogRequested;

    public WebView()
    {
        // Linux renders off-screen into a bitmap (no native child window);
        // Windows keeps the native-window-embedding host.
        Content = OperatingSystem.IsLinux() ? _osrSurface : _browserHost;
        ClipToBounds = true;

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            SendResize();
        };
    }

    /// <summary>True once the native browser process accepted this browser.</summary>
    public bool IsBrowserAvailable => _manager != null && (_browserReady || _cefHwnd != IntPtr.Zero);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (s_noCef || _disposed) return;

        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
            _topLevel.ScalingChanged += OnScalingChanged;

        _manager ??= CreateManager();
        if (_manager == null) return;

        if (_cefHwnd != IntPtr.Zero)
        {
            // Tab switch back: the HWND survived, only the Avalonia panel was
            // recreated. Re-embed it synchronously (or defer inside the host).
            CefDiagnostics.Debug($"[WebView] re-embed HWND=0x{_cefHwnd.ToInt64():X}");
            if (!OperatingSystem.IsLinux())
                _browserHost.EmbedWindow(_cefHwnd);
            QueueResize();
            _ = _manager.SendEmbedDoneAsync();
        }
        else
        {
            CefDiagnostics.Debug($"[WebView] starting browser for {Url}");
            _ = StartBrowserAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_topLevel != null)
        {
            _topLevel.ScalingChanged -= OnScalingChanged;
            _topLevel = null;
        }

        _resizeTimer.Stop();
        // The browser intentionally stays alive: a tab switch must not tear down
        // the renderer. ExternalBrowserProcessHost moves the child out of the
        // dying panel. Permanent teardown happens in Dispose/Cleanup.
    }

    private BrowserProcessManager CreateManager()
    {
        var manager = new BrowserProcessManager { IsolatedCachePath = IsolatedCachePath };
        _manager = manager;
        WireManagerEvents(manager);
        return manager;
    }

    private void WireManagerEvents(BrowserProcessManager manager)
    {
        if (_eventsWired) return;
        _eventsWired = true;

        manager.AddressChanged += url => Dispatcher.UIThread.Post(() =>
        {
            // Native reports only the main frame, so no heuristic filtering is
            // needed here: every event is the real address.
            ApplyNativeUrl(url);
        });

        manager.TitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            Title = title;
            TitleChanged?.Invoke(title);
        });

        manager.LoadingStateChanged += loading => Dispatcher.UIThread.Post(() =>
        {
            IsLoading = loading;
            LoadingStateChanged?.Invoke(loading);
        });

        manager.NavigationStateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (_manager == null) return;
            CanGoBack = _manager.CanGoBack;
            CanGoForward = _manager.CanGoForward;
        });

        manager.OpenPopup += url => Dispatcher.UIThread.Post(() => OpenPopup?.Invoke(url));

        manager.DownloadEvent += info => Dispatcher.UIThread.Post(() => DownloadEvent?.Invoke(info));

        manager.LoadError += info => Dispatcher.UIThread.Post(() => LoadError?.Invoke(info));

        manager.JsDialogRequested += dialog => Dispatcher.UIThread.Post(() =>
        {
            Action<CefJsDialog>? handler = JsDialogRequested;
            if (handler == null)
            {
                // No UI wired up: answer instead of leaving the renderer blocked.
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
                CefDiagnostics.Error("[WebView] JsDialogRequested handler failed", ex);
                dialog.Dismiss();
            }
        });

        manager.WindowHandleReceived += hwnd => Dispatcher.UIThread.Post(async () =>
        {
            if (_disposed) return;

            _cefHwnd = hwnd;
            _browserReady = true;
            _recoveryAttempts = 0;
            CefDiagnostics.Debug($"[WebView] browser ready HWND=0x{hwnd.ToInt64():X}");

            if (!OperatingSystem.IsLinux())
                _browserHost.EmbedWindow(hwnd);
            SendResize();
            if (_manager != null)
                await _manager.SendEmbedDoneAsync();
        });

        manager.BrowserCrashed += () => Dispatcher.UIThread.Post(OnBrowserCrashed);

        if (OperatingSystem.IsLinux())
        {
            manager.OsrPaint += (w, h, dx, dy, dw, dh) =>
                Dispatcher.UIThread.Post(() => OnOsrPaint(w, h, dx, dy, dw, dh));
            manager.OsrBufferReady += (w, h) =>
                Dispatcher.UIThread.Post(() => OnOsrBufferReady(w, h));
        }
    }

    private async void OnBrowserCrashed()
    {
        if (_disposed) return;

        // The native child window died with the host: forget it before any
        // detach/reattach logic can touch a stale handle.
        _cefHwnd = IntPtr.Zero;
        _browserReady = false;
        if (!OperatingSystem.IsLinux())
            _browserHost.DetachEmbedded();
        BrowserCrashed?.Invoke();

        if (!AutoRecover || _manager == null)
            return;

        if (_recoveryAttempts >= 3)
        {
            CefDiagnostics.Error("[WebView] auto-recovery abandoned after 3 attempts");
            return;
        }

        _recoveryAttempts++;
        CefDiagnostics.Warn($"[WebView] recovering browser (attempt {_recoveryAttempts}/3)");

        await Task.Delay(500);
        if (_disposed) return;

        try
        {
            await _manager.RecoverAsync();
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error("[WebView] recovery failed", ex);
        }
    }

    private async Task<bool> StartBrowserAsync()
    {
        if (_manager == null || _disposed) return false;

        try
        {
            // Linux renders off-screen (no native host window); Windows uses a
            // hidden parent + SetParent, also without a host window handed over
            // up front. Either way the browser is created with hostWindow = Zero.
            bool ok = await _manager.StartAsync(Url, CefSettings, IntPtr.Zero).ConfigureAwait(true);
            if (!ok)
                CefDiagnostics.Error("[WebView] native host could not be started");
            return ok;
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error("[WebView] failed to start browser", ex);
            return false;
        }
    }

    private void ApplyNativeUrl(string url)
    {
        _syncingUrl = true;
        try
        {
            // SetCurrentValue keeps a TwoWay binding alive instead of replacing it.
            SetCurrentValue(UrlProperty, url ?? "");
        }
        finally
        {
            _syncingUrl = false;
        }

        AddressChanged?.Invoke(url ?? "");
    }

    // ---- Navigation -------------------------------------------------------

    /// <summary>
    /// Navigates to <paramref name="url"/>. Returns false only when the command
    /// could not be delivered to the native host; when the control is not
    /// attached yet the URL is remembered and loaded on attach.
    /// </summary>
    public async Task<bool> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url)) return false;

        _syncingUrl = true;
        try
        {
            SetCurrentValue(UrlProperty, url);
        }
        finally
        {
            _syncingUrl = false;
        }

        BrowserProcessManager? manager = _manager;
        if (manager == null) return true; // loaded on attach

        return await manager.NavigateAsync(url, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Reloads the current page.</summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken = default)
    {
        BrowserProcessManager? manager = _manager;
        return manager != null && await manager.ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Stops the current load.</summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        BrowserProcessManager? manager = _manager;
        return manager != null && await manager.StopAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Navigates to the previous history entry.</summary>
    public void GoBack() => _manager?.GoBack();

    /// <summary>Navigates to the next history entry.</summary>
    public void GoForward() => _manager?.GoForward();

    // ---- Zoom -------------------------------------------------------------

    /// <summary>Increases page zoom.</summary>
    public void ZoomIn() => _manager?.ZoomIn();
    /// <summary>Decreases page zoom.</summary>
    public void ZoomOut() => _manager?.ZoomOut();
    /// <summary>Resets page zoom to 100%.</summary>
    public void ZoomReset() => _manager?.ZoomReset();

    // ---- Find in page -----------------------------------------------------

    /// <summary>Starts a new find from the top of the page.</summary>
    public void Find(string text)
    {
        _lastFindText = text ?? "";
        if (_lastFindText.Length == 0) _manager?.StopFinding();
        else _manager?.Find(_lastFindText, forward: true, findNext: false);
    }

    /// <summary>Jumps to the next match.</summary>
    public void FindNext() => _manager?.Find(_lastFindText, forward: true, findNext: true);

    /// <summary>Jumps to the previous match.</summary>
    public void FindPrevious() => _manager?.Find(_lastFindText, forward: false, findNext: true);

    /// <summary>Clears find highlighting.</summary>
    public void StopFinding() => _manager?.StopFinding();

    // ---- Script / DevTools ------------------------------------------------

    /// <summary>Executes JavaScript in the main frame (fire-and-forget).</summary>
    public void ExecuteJavaScriptAsync(string code) => _manager?.ExecuteJavaScript(code);

    /// <summary>Opens the Chrome DevTools window for this browser.</summary>
    public void ShowDeveloperTools() => _manager?.ShowDevTools();

    // ---- Recovery ---------------------------------------------------------

    /// <summary>
    /// Manually restarts the browser on a fresh native host, restoring the last
    /// URL. Useful after a crash when <see cref="AutoRecover"/> is disabled.
    /// </summary>
    public async Task<bool> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _manager == null) return false;

        _cefHwnd = IntPtr.Zero;
        _browserHost.DetachEmbedded();
        _recoveryAttempts = 0;
        return await _manager.RecoverAsync(cancellationToken).ConfigureAwait(true);
    }

    // ---- Resize -----------------------------------------------------------

    private void OnScalingChanged(object? sender, EventArgs e) => QueueResize();

    private void QueueResize()
    {
        if (_disposed) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void SendResize()
    {
        if (_manager == null) return;

        double scaling = _topLevel?.RenderScaling ?? 1.0;
        double width = _browserHost.Bounds.Width;
        double height = _browserHost.Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            width = Bounds.Width;
            height = Bounds.Height;
        }

        int w = (int)Math.Round(width * scaling);
        int h = (int)Math.Round(height * scaling);
        if (w > 0 && h > 0)
            _manager.SendResize(w, h);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == UrlProperty)
        {
            if (_syncingUrl) return;

            string? newUrl = change.GetNewValue<string?>();
            if (!string.IsNullOrWhiteSpace(newUrl))
                _ = NavigateAsync(newUrl);
        }
        else if (change.Property == BoundsProperty)
        {
            QueueResize();
        }
    }

    // ---- OSR (Linux) ------------------------------------------------------

    private void OnOsrBufferReady(int w, int h)
    {
        if (_disposed || w <= 0 || h <= 0) return;

        CloseOsrMap();

        BrowserProcessManager? manager = _manager;
        if (manager == null) return;

        string path = $"/dev/shm/cef_osr_{manager.BrowserId}";
        try
        {
            _osrMmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
            _osrView = _osrMmf.CreateViewAccessor(0, (long)w * h * 4, MemoryMappedFileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error($"[WebView] OSR shared memory map failed ({path})", ex);
            CloseOsrMap();
            return;
        }

        _osrBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormats.Bgra8888);
        _osrSurface.Bitmap = _osrBitmap;
    }

    private void OnOsrPaint(int w, int h, int dx, int dy, int dw, int dh)
    {
        WriteableBitmap? bitmap = _osrBitmap;
        MemoryMappedViewAccessor? view = _osrView;
        if (bitmap == null || view == null || w <= 0 || h <= 0) return;

        int frameBytes = w * h * 4;
        if (_osrPixels.Length < frameBytes)
            _osrPixels = new byte[frameBytes];

        try
        {
            view.ReadArray(0, _osrPixels, 0, frameBytes);
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error("[WebView] OSR read failed", ex);
            return;
        }

        int rowBytes = w * 4;
        using (ILockedFramebuffer fb = bitmap.Lock())
        {
            int copyBytes = Math.Min(rowBytes, fb.RowBytes);
            for (int y = 0; y < h; y++)
                Marshal.Copy(_osrPixels, y * rowBytes, fb.Address + y * fb.RowBytes, copyBytes);
        }

        _osrSurface.InvalidateVisual();
    }

    private void CloseOsrMap()
    {
        _osrView?.Dispose();
        _osrView = null;
        _osrMmf?.Dispose();
        _osrMmf = null;
        _osrBitmap?.Dispose();
        _osrBitmap = null;
        _osrSurface.Bitmap = null;
    }

    // ---- OSR input forwarding (Linux) ------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!OperatingSystem.IsLinux()) return;
        var p = e.GetPosition(this);
        _manager?.SendMouseMove((int)p.X, (int)p.Y, (uint)ToCefModifiers(e.KeyModifiers));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!OperatingSystem.IsLinux()) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        int button = props.IsMiddleButtonPressed ? 1 : props.IsRightButtonPressed ? 2 : 0;
        uint mods = (uint)(ToCefModifiers(e.KeyModifiers) | ButtonFlag(button));
        _manager?.SendMouseClick((int)p.X, (int)p.Y, mods, button, false, 1);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!OperatingSystem.IsLinux()) return;
        var p = e.GetPosition(this);
        int button = e.InitialPressMouseButton == MouseButton.Middle ? 1
                   : e.InitialPressMouseButton == MouseButton.Right ? 2 : 0;
        uint mods = (uint)(ToCefModifiers(e.KeyModifiers) | ButtonFlag(button));
        _manager?.SendMouseClick((int)p.X, (int)p.Y, mods, button, true, 1);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!OperatingSystem.IsLinux()) return;
        var p = e.GetPosition(this);
        var delta = e.Delta;
        _manager?.SendMouseWheel((int)p.X, (int)p.Y,
            (uint)ToCefModifiers(e.KeyModifiers), (int)Math.Round(delta.X), (int)Math.Round(delta.Y));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!OperatingSystem.IsLinux()) return;
        _manager?.SendKeyEvent(true, (uint)ToCefModifiers(e.KeyModifiers), MapKey(e.Key), 0);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!OperatingSystem.IsLinux()) return;
        _manager?.SendKeyEvent(false, (uint)ToCefModifiers(e.KeyModifiers), MapKey(e.Key), 0);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(e.Text)) return;
        foreach (char c in e.Text)
        {
            if (c == '\r' || c == '\n' || char.IsControl(c)) continue;
            _manager?.SendKeyChar(0, c);
        }
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (OperatingSystem.IsLinux())
            _manager?.SendFocus(true);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (OperatingSystem.IsLinux())
            _manager?.SendFocus(false);
    }

    private static int ButtonFlag(int button) => button switch
    {
        0 => 16,   // left
        1 => 32,   // middle
        2 => 64,   // right
        _ => 0
    };

    private static int ToCefModifiers(KeyModifiers m)
    {
        int r = 0;
        if ((m & KeyModifiers.Shift) != 0) r |= 2;
        if ((m & KeyModifiers.Control) != 0) r |= 4;
        if ((m & KeyModifiers.Alt) != 0) r |= 8;
        return r;
    }

    // Maps Avalonia keys to Chromium "windows_key_code" values for KEYEVENT_*.
    // Text entry goes through OnTextInput (char events); this map only supplies
    // the physical key for raw keydown/keyup, so unknown keys map to 0.
    private static int MapKey(Key key) => key switch
    {
        Key.Back => 8,
        Key.Tab => 9,
        Key.Enter or Key.Return => 13,
        Key.Escape => 27,
        Key.Space => 32,
        Key.PageUp => 33,
        Key.PageDown => 34,
        Key.End => 35,
        Key.Home => 36,
        Key.Left => 37,
        Key.Up => 38,
        Key.Right => 39,
        Key.Down => 40,
        Key.Insert => 45,
        Key.Delete => 46,
        Key.D0 => 48, Key.D1 => 49, Key.D2 => 50, Key.D3 => 51, Key.D4 => 52,
        Key.D5 => 53, Key.D6 => 54, Key.D7 => 55, Key.D8 => 56, Key.D9 => 57,
        Key.A => 65, Key.B => 66, Key.C => 67, Key.D => 68, Key.E => 69, Key.F => 70,
        Key.G => 71, Key.H => 72, Key.I => 73, Key.J => 74, Key.K => 75, Key.L => 76,
        Key.M => 77, Key.N => 78, Key.O => 79, Key.P => 80, Key.Q => 81, Key.R => 82,
        Key.S => 83, Key.T => 84, Key.U => 85, Key.V => 86, Key.W => 87, Key.X => 88,
        Key.Y => 89, Key.Z => 90,
        Key.NumPad0 => 96, Key.NumPad1 => 97, Key.NumPad2 => 98, Key.NumPad3 => 99,
        Key.NumPad4 => 100, Key.NumPad5 => 101, Key.NumPad6 => 102, Key.NumPad7 => 103,
        Key.NumPad8 => 104, Key.NumPad9 => 105,
        _ => 0,
    };

    // ---- Teardown ---------------------------------------------------------

    /// <summary>
    /// Permanently releases this browser (tab closed). Idempotent. The shared
    /// native host stays alive for other tabs until the application exits.
    /// </summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;

        _resizeTimer.Stop();
        _eventsWired = false;

        // Forget the embedded HWND before the native browser is destroyed: the
        // host will destroy that window and we must never touch it afterwards.
        _cefHwnd = IntPtr.Zero;
        _browserReady = false;
        if (!OperatingSystem.IsLinux())
            _browserHost.DetachEmbedded();
        CloseOsrMap();

        _manager?.Dispose();
        _manager = null;
    }

    /// <summary>Same as <see cref="Cleanup"/>.</summary>
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
