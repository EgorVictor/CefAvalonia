using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CefSharp.Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TestBrowserApp;

public partial class MainWindow : Window
{
    private BrowserView browserControl = null!;
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "testapp_debug.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        try { File.WriteAllText(LogPath, $"--- TestBrowserApp started ---\n"); } catch { }

        var urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        var goButton = this.FindControl<Button>("GoButton")!;
        var reloadButton = this.FindControl<Button>("ReloadButton")!;
        var container = this.FindControl<Panel>("BrowserContainer")!;

        browserControl = new BrowserView();
        browserControl.CefSettings.CommandLineSwitches.Add("--allow-file-access-from-files");
        browserControl.CefSettings.CommandLineSwitches.Add("--disable-web-security");
        container.Children.Add(browserControl);

        browserControl.AddressChanged += url => Dispatcher.UIThread.Post(() =>
        {
            Log($"AddressChanged: '{url}'");
            urlTextBox.Text = url;
        });
        browserControl.TitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            Log($"TitleChanged: '{title}'");
            Title = title;
        });
        browserControl.LoadingStateChanged += loading => Dispatcher.UIThread.Post(() =>
        {
            Log($"LoadingStateChanged: {loading}");
            goButton.IsEnabled = !loading;
        });
        browserControl.BrowserCrashed += () => Dispatcher.UIThread.Post(() =>
        {
            Log("BrowserCrashed");
            Title = "Browser process exited";
        });
        browserControl.LoadError += info => Dispatcher.UIThread.Post(() =>
        {
            Log($"LoadError: {info}");
            Title = $"LoadError: {info}";
        });

        goButton.Click += async (_, _) =>
        {
            Log("GoButton clicked");
            await Navigate(urlTextBox);
        };
        urlTextBox.KeyDown += (_, e) => Log($"TextBox KeyDown: Key={e.Key} Handled={e.Handled}");
        urlTextBox.KeyUp += (_, e) =>
        {
            Log($"TextBox KeyUp: Key={e.Key} Handled={e.Handled}");
            if (e.Key == Key.Enter)
                _ = Navigate(urlTextBox);
        };
        this.KeyDown += (_, e) => Log($"Window KeyDown: Key={e.Key} Handled={e.Handled}");
        this.KeyUp += (_, e) => Log($"Window KeyUp: Key={e.Key} Handled={e.Handled}");
        reloadButton.Click += async (_, _) =>
        {
            Log("ReloadButton clicked");
            await browserControl.ReloadAsync();
        };

        Opened += (_, _) =>
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--no-cef") < 0)
                urlTextBox.Text = browserControl.Url;
            Log($"Opened, initial Url='{browserControl.Url}'");
        };
    }

    private async Task Navigate(TextBox urlTextBox)
    {
        var raw = urlTextBox.Text ?? "";
        Log($"Navigate called, raw='{raw}'");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            Title = $"Navigate: {raw}";
            try
            {
                await browserControl.NavigateAsync(raw);
            }
            catch (Exception ex)
            {
                Log($"NavigateAsync threw: {ex.GetType().Name}: {ex.Message}");
                Title = $"NavError: {ex.Message}";
            }
        }
    }
}
