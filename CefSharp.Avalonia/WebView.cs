using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CefSharp.Avalonia;

/// <summary>
/// Avalonia UserControl that embeds a CEF browser instance via HWND interop.
/// Uses ExternalBrowserProcessHost (NativeControlHost) + BrowserProcessManager (IPC).
/// Lifecycle: OnAttachedToVisualTree → StartAsync → CefBrowser.Native.exe → Embed → events
/// </summary>
public class WebView : UserControl
{
    private readonly ExternalBrowserProcessHost _browserHost = new();
    private BrowserProcessManager? _manager;
    private CancellationTokenSource? _resizeCts;
    private string? _lastNavigatedUrl;
    private IntPtr _cefHwnd = IntPtr.Zero;
    private bool _disposed;

    public static readonly StyledProperty<string> UrlProperty =
        AvaloniaProperty.Register<WebView, string>(nameof(Url), defaultValue: "");

    /// <summary>Current URL. Set in C++ and passed through as-is.</summary>
    public string Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value ?? "");
    }

    /// <summary>
    /// Bindable address. Setting this property triggers navigation via NavigateAsync.
    /// Unlike Url (which is display-only from C++), Address is meant for ViewModel binding.
    /// </summary>
    public static readonly StyledProperty<string?> AddressProperty =
        AvaloniaProperty.Register<WebView, string?>(nameof(Address),
            defaultBindingMode: BindingMode.TwoWay);

    public string? Address
    {
        get => GetValue(AddressProperty);
        set => SetValue(AddressProperty, value);
    }

    private string _title = "";
    public static readonly DirectProperty<WebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<WebView, string>(nameof(Title),
            o => o.Title);

    /// <summary>Browser tab title synced from CEF's OnTitleChange.</summary>
    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public static readonly DirectProperty<WebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(nameof(IsLoading),
            o => o.IsLoading);

    /// <summary>Whether the browser is currently loading a page.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    public static readonly StyledProperty<ICommand?> NavigateCommandProperty =
        AvaloniaProperty.Register<WebView, ICommand?>(nameof(NavigateCommand));

    /// <summary>
    /// Bindable command. Bind your ViewModel's RelayCommand here.
    /// When the user presses Enter / clicks Go, bind a Button to this command.
    /// Your ViewModel is responsible for calling NavigateAsync.
    /// </summary>
    public ICommand? NavigateCommand
    {
        get => GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    /// <summary>
    /// CEF initialization settings mapped from CefSettings in CEF's cef_types.h.
    /// Set before WebView is attached to the visual tree.
    /// </summary>
    public CefSettings CefSettings { get; set; } = new() { NoSandbox = true };

    /// <summary>Raised when the browser navigates to a new URL.</summary>
    public event Action<string>? AddressChanged;
    /// <summary>Raised when the page title changes.</summary>
    public event Action<string>? TitleChanged;
    /// <summary>Raised when loading state changes (true = loading, false = done).</summary>
    public event Action<bool>? LoadingStateChanged;
    /// <summary>Raised when the native browser process exits unexpectedly.</summary>
    public event Action? BrowserCrashed;
    /// <summary>Raised when a page load error occurs. Parameter: "code|text|url".</summary>
    public event Action<string>? LoadError;
    /// <summary>Raised when a web page requests to open a popup/new window. URL should open in a new tab.</summary>
    public event Action<string>? OpenPopup;

    public WebView()
    {
        Content = _browserHost;
        LayoutUpdated += OnLayoutUpdated;
    }

    private bool _layoutDone;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_layoutDone) return;
        _layoutDone = true;
        _ = SendResizeAsync();
    }

    /// <summary>
    /// Lifecycle start: launches CefBrowser.Native.exe or re-embeds existing HWND.
    /// Skip with --no-cef command-line flag for UI-only testing.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Debug.WriteLine($"[WebView] OnAttachedToVisualTree - URL={Url}, HasManager={_manager != null}, HasHwnd={_cefHwnd != IntPtr.Zero}");
        base.OnAttachedToVisualTree(e);
        var cmdArgs = Environment.GetCommandLineArgs();
        if (Array.IndexOf(cmdArgs, "--no-cef") >= 0)
            return;

        if (_manager != null && _cefHwnd != IntPtr.Zero)
        {
            // Tab switch back: re-embed existing CEF HWND into new panel
            Debug.WriteLine($"[WebView] RE-EMBEDDING existing HWND=0x{_cefHwnd.ToInt64():X}");
            _layoutDone = false;
            _browserHost.EmbedWindow(_cefHwnd);
            _ = SendResizeAsync();
            _ = _manager.SendEmbedDoneAsync();
        }
        else if (_manager != null)
        {
            // Manager exists but HWND not ready yet — reset layout, embed on WindowHandleReceived
            Debug.WriteLine($"[WebView] Manager exists, waiting for HWND");
            _layoutDone = false;
        }
        else
        {
            Debug.WriteLine($"[WebView] Starting new browser");
            StartBrowser();
        }
    }

    /// <summary>
    /// Lifecycle end: keep the native process alive for tab switch.
    /// Actual cleanup happens via Dispose() or process exit.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Debug.WriteLine($"[WebView] OnDetachedFromVisualTree");
        base.OnDetachedFromVisualTree(e);
        // Don't dispose manager — keep CEF process for tab switch re-embed
    }

    private void StartBrowser()
    {
        if (_manager != null) return;

        _manager = new BrowserProcessManager();
        WireManagerEvents();
        _ = StartAsync();
    }

    /// <summary>
    /// Wires BrowserProcessManager events to UI thread and WebView properties.
    /// AddressChanged filters out stale intermediate-redirect hosts within 3s of a pending navigation.
    /// </summary>
    private void WireManagerEvents()
    {
        if (_manager == null) return;

        _manager.AddressChanged += url =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var last = _lastNavigatedUrl;
                if (last != null)
                {
                    var u = url.Replace('\\', '/');
                    var l = last.Replace('\\', '/');
                    if (!u.Contains(l, StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains(u, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastNavigatedUrl = null;
                        return;
                    }
                    _lastNavigatedUrl = null;
                }
                Url = url;
                AddressChanged?.Invoke(url);
            });
        };

        _manager.TitleChanged += title =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Title = title;
                TitleChanged?.Invoke(title);
            });
        };

        _manager.LoadingStateChanged += loading =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = loading;
                LoadingStateChanged?.Invoke(loading);
            });
        };

        _manager.OpenPopup += url =>
        {
            Dispatcher.UIThread.Post(() => OpenPopup?.Invoke(url));
        };

        // On Ready event: save HWND, embed, then signal embed done + push initial size
        _manager.WindowHandleReceived += hwnd =>
        {
            _cefHwnd = hwnd;
            Dispatcher.UIThread.Post(async () =>
            {
                _browserHost.EmbedWindow(hwnd);
                await SendResizeAsync();
                await _manager.SendEmbedDoneAsync();
            });
        };

        _manager.BrowserCrashed += () =>
        {
            Dispatcher.UIThread.Post(() => BrowserCrashed?.Invoke());
        };

        _manager.LoadError += info =>
        {
            Dispatcher.UIThread.Post(() => LoadError?.Invoke(info));
        };
    }

    private async Task StartAsync()
    {
        if (_manager == null) return;
        try
        {
            Debug.WriteLine("[WebView] StartAsync: Starting browser process");
            await _manager.StartAsync(Url, CefSettings);
            Debug.WriteLine("[WebView] StartAsync: Browser process started successfully");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView] StartAsync error: {ex}");
        }
    }

    /// <summary>
    /// Navigate to a URL. C++ handles all normalization.
    /// If the native process hasn't started yet, starts it first.
    /// Immediately sets Url + fires AddressChanged for optimistic UI, then delegates to IPC.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        Console.Error.WriteLine($"DIAG: WEBVIEW NavigateAsync url={url} manager={_manager != null}");
        try
        {
            if (_manager == null)
            {
                Debug.WriteLine($"[WebView] NavigateAsync: Manager is null, starting browser");
                Url = url;
                StartBrowser();
                return;
            }
            _lastNavigatedUrl = url;
            Url = url;
            AddressChanged?.Invoke(url);
            Debug.WriteLine($"[WebView] NavigateAsync: Navigating to {url}");
            await _manager.NavigateAsync(url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebView] NavigateAsync error: {ex}");
        }
    }

    /// <summary>Reload the current page.</summary>
    public Task ReloadAsync() => _manager?.ReloadAsync() ?? Task.CompletedTask;
    /// <summary>Stop the current page load.</summary>
    public Task StopAsync() => _manager?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Opens developer tools. WebView (HWND interop) does not support DevTools in-process.
    /// Configure remote-debugging-port in CefSettings.CommandLineSwitches and access via browser.
    /// </summary>
    public void ShowDeveloperTools() { }

    /// <summary>
    /// Clean up the native process. Call when the tab is permanently closed (not on tab switch).
    /// Idempotent: safe to call multiple times.
    /// </summary>
    public void Cleanup()
    {
        if (_disposed) return;
        _disposed = true;

        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _resizeCts = null;

        // Forget the embedded HWND BEFORE disposing the manager: the native host will
        // destroy that window, and we must never reparent/touch it afterwards.
        _browserHost.DetachEmbedded();

        _manager?.Dispose();
        _manager = null;
        _cefHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Debounced resize: when Bounds changes, wait 15ms then send Resize via IPC.
    /// Cancels previous pending resize to avoid flooding the pipe during window dragging.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AddressProperty)
        {
            var newUrl = change.GetNewValue<string?>();
            if (!string.IsNullOrEmpty(newUrl))
                _ = NavigateAsync(newUrl);
        }
        else if (change.Property == BoundsProperty && _manager != null && IsVisible)
        {
            // Only send resize if this WebView is visible to avoid IPC flooding
            _resizeCts?.Cancel();
            _resizeCts = new CancellationTokenSource();
            var token = _resizeCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(15, token);
                    if (!token.IsCancellationRequested)
                        await Dispatcher.UIThread.InvokeAsync(() => SendResizeAsync(token));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebView] Resize task error: {ex}");
                }
            }, token);
        }
    }

    private void SendResize()
    {
        if (_manager == null) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = (int)(_browserHost.Bounds.Width * scaling);
        var h = (int)(_browserHost.Bounds.Height * scaling);
        if (w > 0 && h > 0)
            _manager.SendResize(w, h);
    }

    private async Task SendResizeAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        SendResize();
        await Task.CompletedTask;
    }
}
