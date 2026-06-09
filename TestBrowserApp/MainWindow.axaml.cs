using Avalonia.Controls;
using Avalonia.Input;
using CefSharp.Avalonia;
using System;
using System.IO;

namespace TestBrowserApp;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "testapp_debug.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
    }

    private WebView? _browserView;
    private TextBox? _addressBar;
    private Button? _goButton;

    public MainWindow()
    {
        InitializeComponent();
        try { File.WriteAllText(LogPath, $"--- TestBrowserApp started ---\n"); } catch { }

        _browserView = this.FindControl<WebView>("BrowserView");
        _addressBar = this.FindControl<TextBox>("AddressBar");
        _goButton = this.FindControl<Button>("GoButton");

        if (_addressBar != null)
        {
            _addressBar.KeyDown += OnAddressBarKeyDown;
        }

        if (_goButton != null)
        {
            _goButton.Click += (s, e) => Navigate();
        }

        Log("MainWindow initialized - WebView ready for navigation");
    }

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            Navigate();
            e.Handled = true;
        }
    }

    private void Navigate()
    {
        if (_addressBar == null || _browserView == null || string.IsNullOrWhiteSpace(_addressBar.Text))
            return;

        var url = _addressBar.Text;
        Log($"Navigating to: {url}");
        _ = _browserView.NavigateAsync(url);
    }
}

