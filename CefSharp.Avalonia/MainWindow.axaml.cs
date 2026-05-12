using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private ExternalBrowserProcessHost browserHost = null!;
    private readonly BrowserProcessManager browserManager = new();
    private CancellationTokenSource? _resizeCts;

    public MainWindow()
    {
        Debug.WriteLine("[MainWindow] Constructor");
        InitializeComponent();
        SetupControls();
    }

    private void SetupControls()
    {
        Debug.WriteLine("[MainWindow] SetupControls");
        urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        browserHost = this.FindControl<ExternalBrowserProcessHost>("BrowserHost")!;
        var goButton = this.FindControl<Button>("GoButton")!;
        var reloadButton = this.FindControl<Button>("ReloadButton")!;

        Debug.WriteLine($"[MainWindow] browserHost found: {browserHost != null}");

        browserManager.AddressChanged += url =>
        {
            Debug.WriteLine($"[MainWindow] AddressChanged -> '{url}'");
            Dispatcher.UIThread.Post(() => urlTextBox.Text = url);
        };

        browserManager.TitleChanged += title =>
        {
            Debug.WriteLine($"[MainWindow] TitleChanged -> '{title}'");
            Dispatcher.UIThread.Post(() => Title = title);
        };

        browserManager.LoadingStateChanged += loading =>
        {
            Debug.WriteLine($"[MainWindow] LoadingStateChanged -> {loading}");
            Dispatcher.UIThread.Post(() =>
            {
                goButton.IsEnabled = !loading;
                if (!loading && urlTextBox.Text != browserManager.LastUrl)
                {
                    Debug.WriteLine($"[MainWindow] Updating address bar from LastUrl: '{browserManager.LastUrl}'");
                    urlTextBox.Text = browserManager.LastUrl ?? urlTextBox.Text;
                }
            });
        };

        browserManager.WindowHandleReceived += hwnd =>
        {
            Debug.WriteLine($"[MainWindow] WindowHandleReceived 0x{hwnd.ToInt64():X8}, dispatching to UI thread");
            Dispatcher.UIThread.Post(async () =>
            {
                Debug.WriteLine($"[MainWindow] UI thread: EmbedWindow(0x{hwnd.ToInt64():X8})");
                browserHost.EmbedWindow(hwnd);
                // Tell native browser process embedding is done, let it manage size/visibility
                await browserManager.SendEmbedDoneAsync();
                // Send initial size
                await SendResizeAsync();
            });
        };

        browserManager.BrowserCrashed += () =>
        {
            Debug.WriteLine("[MainWindow] BrowserCrashed event");
            Dispatcher.UIThread.Post(() =>
            {
                Title = "Browser process exited";
            });
        };

        goButton.Click += async (_, _) => await NavigateToUrl();
        urlTextBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await NavigateToUrl();
        };
        reloadButton.Click += async (_, _) => await browserManager.ReloadAsync();

        Resized += OnWindowResized;

        Opened += async (_, _) =>
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            if (Array.IndexOf(cmdArgs, "--no-cef") >= 0)
            {
                Debug.WriteLine("[MainWindow] --no-cef mode: not starting browser process");
                return;
            }
            Debug.WriteLine("[MainWindow] Opened, starting browser...");
            await browserManager.StartAsync("https://www.baidu.com");
            Debug.WriteLine("[MainWindow] StartAsync completed");
        };
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("http://") || url.StartsWith("https://"))
            return url;
        return "https://" + url;
    }

    private async Task NavigateToUrl()
    {
        var raw = urlTextBox.Text ?? string.Empty;
        Debug.WriteLine($"[MainWindow] NavigateToUrl: '{raw}'");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Debug.WriteLine("[MainWindow] NavigateToUrl: empty URL, skipping");
            return;
        }
        var url = NormalizeUrl(raw);
        await browserManager.NavigateAsync(url);
    }

    private void OnWindowResized(object? sender, EventArgs e)
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

    private async Task SendResizeAsync(CancellationToken ct = default)
    {
        // If a newer resize arrived while we were waiting, skip stale send
        if (ct.IsCancellationRequested) return;
        var w = (int)browserHost.Bounds.Width;
        var h = (int)browserHost.Bounds.Height;
        if (w > 0 && h > 0)
            await browserManager.SendResizeAsync(w, h);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        browserManager.Dispose();
        base.OnClosing(e);
    }
}
