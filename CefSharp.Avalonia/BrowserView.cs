using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public class BrowserView : UserControl
{
    private readonly ExternalBrowserProcessHost _browserHost = new();
    private BrowserProcessManager? _manager;
    private CancellationTokenSource? _resizeCts;
    private string? _pendingNavUrl;
    private DateTime _pendingNavTime;

    public static readonly StyledProperty<string> UrlProperty =
        AvaloniaProperty.Register<BrowserView, string>(nameof(Url), defaultValue: "https://www.baidu.com");

    public string Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, NormalizeUrl(value));
    }

    private string _title = "";
    public static readonly DirectProperty<BrowserView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<BrowserView, string>(nameof(Title),
            o => o.Title);

    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public static readonly DirectProperty<BrowserView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<BrowserView, bool>(nameof(IsLoading),
            o => o.IsLoading);

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    /// <summary>
    /// Additional CEF command-line switches passed to the native subprocess.
    /// Default: --disable-gpu --no-sandbox
    /// Set before BrowserView is attached to the visual tree.
    /// </summary>
    public string[] CefArgs { get; set; } = { "--disable-gpu", "--no-sandbox" };

    public event Action<string>? AddressChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool>? LoadingStateChanged;
    public event Action? BrowserCrashed;
    public event Action<string>? LoadError;

    public BrowserView()
    {
        Content = _browserHost;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var cmdArgs = Environment.GetCommandLineArgs();
        if (Array.IndexOf(cmdArgs, "--no-cef") >= 0)
            return;

        StartBrowser();
    }

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

    private void WireManagerEvents()
    {
        if (_manager == null) return;

        _manager.AddressChanged += url =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_pendingNavUrl != null &&
                    (DateTime.UtcNow - _pendingNavTime).TotalSeconds < 3)
                {
                    try
                    {
                        var pendingHost = new Uri(_pendingNavUrl).Host;
                        var eventHost = new Uri(url).Host;
                        if (!eventHost.Contains(pendingHost) && !pendingHost.Contains(eventHost))
                            return;
                    }
                    catch { }
                }
                _pendingNavUrl = null;
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

    private async Task StartAsync()
    {
        if (_manager == null) return;
        try
        {
            await _manager.StartAsync(Url, CefArgs);
        }
        catch { }
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;
        try
        {
            var host = new Uri(url).Host;
            if (host.Split('.').Length == 2 && !host.StartsWith("www."))
                url = url.Replace(host, "www." + host);
        }
        catch { }
        return url;
    }

    public async Task NavigateAsync(string url)
    {
        url = NormalizeUrl(url);
        if (_manager == null)
        {
            Url = url;
            StartBrowser();
            return;
        }
        // Record pending navigation to filter stale AddressChanged events
        _pendingNavUrl = url;
        _pendingNavTime = DateTime.UtcNow;
        // Immediately notify consumer of the requested URL
        Url = url;
        AddressChanged?.Invoke(url);
        await _manager.NavigateAsync(url);
    }

    public Task ReloadAsync() => _manager?.ReloadAsync() ?? Task.CompletedTask;
    public Task StopAsync() => _manager?.StopAsync() ?? Task.CompletedTask;

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
