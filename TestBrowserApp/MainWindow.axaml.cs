using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CefSharp.Avalonia;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TestBrowserApp;

public partial class MainWindow : Window
{
    private BrowserView browserControl = null!;

    public MainWindow()
    {
        Debug.WriteLine("[TestBrowserApp] MainWindow constructor");
        InitializeComponent();

        var urlTextBox = this.FindControl<TextBox>("UrlTextBox")!;
        var goButton = this.FindControl<Button>("GoButton")!;
        var reloadButton = this.FindControl<Button>("ReloadButton")!;
        var container = this.FindControl<Panel>("BrowserContainer")!;

        // Create BrowserView in code (cross-project XAML not supported)
        browserControl = new BrowserView();
        container.Children.Add(browserControl);

        browserControl.AddressChanged += url => Dispatcher.UIThread.Post(() => urlTextBox.Text = url);
        browserControl.TitleChanged += title => Dispatcher.UIThread.Post(() => Title = title);
        browserControl.LoadingStateChanged += loading => Dispatcher.UIThread.Post(() => goButton.IsEnabled = !loading);
        browserControl.BrowserCrashed += () => Dispatcher.UIThread.Post(() => Title = "Browser process exited");

        goButton.Click += async (_, _) => await Navigate(urlTextBox);
        urlTextBox.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await Navigate(urlTextBox); };
        reloadButton.Click += async (_, _) => await browserControl.ReloadAsync();

        Opened += (_, _) =>
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--no-cef") < 0)
                urlTextBox.Text = browserControl.Url;
        };
    }

    private async Task Navigate(TextBox urlTextBox)
    {
        var raw = urlTextBox.Text ?? "";
        if (!string.IsNullOrWhiteSpace(raw))
            await browserControl.NavigateAsync(raw);
    }
}
