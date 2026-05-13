using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Avalonia UserControl that embeds a CEF browser instance via HWND interop.
/// Uses ExternalBrowserProcessHost (NativeControlHost) + BrowserProcessManager (IPC).
/// Lifecycle: OnAttachedToVisualTree → StartAsync → CefBrowser.Native.exe → Embed → events
/// </summary>
public class BrowserView : UserControl
{
    private readonly ExternalBrowserProcessHost _browserHost = new();
    private BrowserProcessManager? _manager;
    private CancellationTokenSource? _resizeCts;
    private string? _lastNavigatedUrl;

    public static readonly StyledProperty<string> UrlProperty =
        AvaloniaProperty.Register<BrowserView, string>(nameof(Url), defaultValue: "");

    /// <summary>Current URL. Set in C++ and passed through as-is.</summary>
    public string Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value ?? "");
    }

    private string _title = "";
    public static readonly DirectProperty<BrowserView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<BrowserView, string>(nameof(Title),
            o => o.Title);

    /// <summary>Browser tab title synced from CEF's OnTitleChange.</summary>
    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public static readonly DirectProperty<BrowserView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<BrowserView, bool>(nameof(IsLoading),
            o => o.IsLoading);

    /// <summary>Whether the browser is currently loading a page.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    /// <summary>
    /// CEF initialization settings mapped from CefSettings in CEF's cef_types.h.
    /// Set before BrowserView is attached to the visual tree.
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

    public BrowserView()
    {
        Content = _browserHost;
    }

    /// <summary>
    /// Lifecycle start: launches CefBrowser.Native.exe when this control is attached to the visual tree.
    /// Skip with --no-cef command-line flag for UI-only testing.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var cmdArgs = Environment.GetCommandLineArgs();
        if (Array.IndexOf(cmdArgs, "--no-cef") >= 0)
            return;

        StartBrowser();
    }

    /// <summary>
    /// Lifecycle end: dispose the native process and pipe when control is removed.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _manager?.Dispose();
        _manager = null;
    }

    private void StartBrowser()
    {
        if (_manager != null) return;

        _manager = new BrowserProcessManager();
        WireManagerEvents();
        _ = StartAsync();
    }

    /// <summary>
    /// Wires BrowserProcessManager events to UI thread and BrowserView properties.
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

        // On Ready event: embed the CEF browser HWND, then signal embed done + push initial size
        _manager.WindowHandleReceived += hwnd =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                _browserHost.EmbedWindow(hwnd);
                await _manager.SendEmbedDoneAsync();
                await SendResizeAsync();
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

    /// <summary>
    /// Launches the native process with configured CefSettings.
    /// </summary>
    private async Task StartAsync()
    {
        if (_manager == null) return;
        try
        {
            await _manager.StartAsync(Url, CefSettings);
        }
        catch { }
    }

    /// <summary>
    /// Navigate to a URL. C++ handles all normalization.
    /// If the native process hasn't started yet, starts it first.
    /// Immediately sets Url + fires AddressChanged for optimistic UI, then delegates to IPC.
    /// </summary>
    public async Task NavigateAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (_manager == null)
        {
            Url = url;
            StartBrowser();
            return;
        }
        _lastNavigatedUrl = url;
        Url = url;
        AddressChanged?.Invoke(url);
        await _manager.NavigateAsync(url);
    }

    /// <summary>Reload the current page.</summary>
    public Task ReloadAsync() => _manager?.ReloadAsync() ?? Task.CompletedTask;
    /// <summary>Stop the current page load.</summary>
    public Task StopAsync() => _manager?.StopAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Debounced resize: when Bounds changes, wait 15ms then send Resize via IPC.
    /// Cancels previous pending resize to avoid flooding the pipe during window dragging.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _manager != null)
        {
            _resizeCts?.Cancel();
            _resizeCts = new CancellationTokenSource();
            var token = _resizeCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(15, token);
                    await Dispatcher.UIThread.InvokeAsync(() => SendResizeAsync(token));
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private async Task SendResizeAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        var w = (int)_browserHost.Bounds.Width;
        var h = (int)_browserHost.Bounds.Height;
        if (w > 0 && h > 0 && _manager != null)
            await _manager.SendResizeAsync(w, h);
    }
}
