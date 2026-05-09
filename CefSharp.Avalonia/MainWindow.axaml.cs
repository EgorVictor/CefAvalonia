using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private ExternalBrowserProcessHost browserHost = null!;
    private readonly BrowserProcessManager browserManager = new();

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
    }

    private void SetupControls()
    {
        urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        browserHost = this.FindControl<ExternalBrowserProcessHost>("BrowserHost")!;
        var goButton = this.FindControl<Button>("GoButton")!;
        var reloadButton = this.FindControl<Button>("ReloadButton")!;

        browserManager.AddressChanged += url =>
            Dispatcher.UIThread.Post(() => urlTextBox.Text = url);

        browserManager.WindowHandleReceived += hwnd =>
            Dispatcher.UIThread.Post(() => browserHost.EmbedWindow(hwnd));

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

        Resized += OnWindowResized;

        Opened += async (_, _) =>
        {
            await browserManager.StartAsync("https://www.bing.com");
        };
    }

    private async Task NavigateToUrl()
    {
        var url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;
        await browserManager.NavigateAsync(url);
    }

    private void OnWindowResized(object? sender, EventArgs e)
    {
        browserHost.ResizeEmbedded();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        browserManager.Dispose();
        base.OnClosing(e);
    }
}
