using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private Border browserPlaceholder = null!;
    private readonly BrowserProcessManager browserManager = new();

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
    }

    private void SetupControls()
    {
        urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        browserPlaceholder = this.FindControl<Border>("BrowserPlaceholder")!;
        var goButton = this.FindControl<Button>("GoButton")!;
        var reloadButton = this.FindControl<Button>("ReloadButton")!;

        browserManager.AddressChanged += url =>
            Dispatcher.UIThread.Post(() => urlTextBox.Text = url);

        browserManager.Ready += () =>
            Dispatcher.UIThread.Post(() => Title = "CefSharp Avalonia Browser");

        browserManager.BrowserCrashed += () =>
            Dispatcher.UIThread.Post(async () =>
            {
                Title = "Browser crashed - restarting...";
                await Task.Delay(2000);
                _ = browserManager.RestartAsync();
            });

        goButton.Click += async (_, _) => await NavigateToUrl();
        urlTextBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await NavigateToUrl();
        };
        reloadButton.Click += async (_, _) => await browserManager.ReloadAsync();

        PositionChanged += OnWindowMoved;
        Resized += OnWindowResized;

        Opened += async (_, _) =>
        {
            await browserManager.StartAsync("https://www.bing.com");
            SyncBrowserPosition();
        };
    }

    private async Task NavigateToUrl()
    {
        var url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;
        await browserManager.NavigateAsync(url);
    }

    private void OnWindowMoved(object? sender, PixelPointEventArgs e)
    {
        SyncBrowserPosition();
    }

    private void OnWindowResized(object? sender, EventArgs e)
    {
        SyncBrowserPosition();
    }

    private void SyncBrowserPosition()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
            return;

        var pos = Position;
        var bx = pos.X + (int)browserPlaceholder.Bounds.X;
        var by = pos.Y + (int)browserPlaceholder.Bounds.Y;
        var bw = (int)browserPlaceholder.Bounds.Width;
        var bh = (int)browserPlaceholder.Bounds.Height;

        if (bw > 0 && bh > 0)
            _ = browserManager.MoveResizeAsync(bx, by, bw, bh);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        browserManager.Dispose();
        base.OnClosing(e);
    }
}
