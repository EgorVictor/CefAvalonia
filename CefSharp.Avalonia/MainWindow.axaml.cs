using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using Avalonia.Threading;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private Button goButton = null!;
    private Panel browserPanel = null!;
    private BrowserIsland? island;

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
    }

    private void SetupControls()
    {
        urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        goButton = this.FindControl<Button>("GoButton")!;
        browserPanel = this.FindControl<Panel>("BrowserPanel")!;

        goButton.Click += GoButton_Click;
        urlTextBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) NavigateToUrl();
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var topLevel = TopLevel.GetTopLevel(this);
        var handle = topLevel?.TryGetPlatformHandle();
        if (handle == null) return;

        island = new BrowserIsland();
        island.AddressChanged += OnIslandAddressChanged;
        island.Create(handle.Handle);
        island.Navigate("https://www.bing.com");

        browserPanel.SizeChanged += OnBrowserPanelSizeChanged;
        ResizeIsland();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        browserPanel.SizeChanged -= OnBrowserPanelSizeChanged;
        if (island != null)
        {
            island.AddressChanged -= OnIslandAddressChanged;
            island.Dispose();
            island = null;
        }
        base.OnClosing(e);
    }

    private void OnBrowserPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ResizeIsland();
    }

    private void OnIslandAddressChanged(object? sender, string url)
    {
        Dispatcher.UIThread.Post(() =>
            urlTextBox.Text = url);
    }

    private void ResizeIsland()
    {
        if (island == null) return;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var pos = browserPanel.TranslatePoint(new Point(0, 0), this);
        if (pos == null) return;

        island.Resize(
            (int)(pos.Value.X * scale),
            (int)(pos.Value.Y * scale),
            (int)(browserPanel.Bounds.Width * scale),
            (int)(browserPanel.Bounds.Height * scale));
    }

    private void GoButton_Click(object? sender, RoutedEventArgs e) => NavigateToUrl();

    private void NavigateToUrl()
    {
        if (island == null) return;
        string url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;
        urlTextBox.Text = url;
        island.Navigate(url);
    }
}
