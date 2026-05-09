using Avalonia.Controls;
using Avalonia.Input;
using System;
using Avalonia.Threading;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private CefSharpBrowserHost browserHost = null!;
    private CefMemoryGuard? memoryGuard;

    public MainWindow()
    {
        InitializeComponent();
        SetupControls();
    }

    private void SetupControls()
    {
        urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        browserHost = this.FindControl<CefSharpBrowserHost>("BrowserHost")!;
        var goButton = this.FindControl<Button>("GoButton")!;

        goButton.Click += (_, _) => NavigateToUrl();
        urlTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) NavigateToUrl();
        };

        browserHost.AddressChanged += (_, url) =>
        {
            Dispatcher.UIThread.Post(()=> urlTextBox.Text = url);
        };

        memoryGuard = new CefMemoryGuard(browserHost);

        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        memoryGuard?.Dispose();
        memoryGuard = null;
    }

    private void NavigateToUrl()
    {
        string url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;
        browserHost.Navigate(url);
    }
}
