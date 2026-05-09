using Avalonia.Controls;
using Avalonia.Input;
using System;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox = null!;
    private CefSharpBrowserHost browserHost = null!;
    private CefMemoryGuard? memoryGuard;
    private DateTime lastLoadErrorLog;

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
            urlTextBox.Text = url;
        };

        browserHost.LoadError += OnLoadError;

        memoryGuard = new CefMemoryGuard(browserHost);

        Closed += OnClosed;
    }

    private void OnLoadError(object? sender, CefSharp.LoadErrorEventArgs e)
    {
        if (!e.Frame.IsMain) return;

        var now = DateTime.UtcNow;
        if ((now - lastLoadErrorLog).TotalSeconds < 30) return;
        lastLoadErrorLog = now;

        System.IO.File.AppendAllText(
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CefSharp.Avalonia", "logs", "browser.log"),
            $"[{now:HH:mm:ss}] LoadError: [{e.ErrorCode}] {e.ErrorText} ({e.FailedUrl}){Environment.NewLine}");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        memoryGuard?.Dispose();
        memoryGuard = null;
        browserHost.LoadError -= OnLoadError;
        browserHost.ClearExternalEvents();
    }

    private void NavigateToUrl()
    {
        string url = urlTextBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return;
        browserHost.Navigate(url);
    }
}
