using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CefSharp.Avalonia;
using ReactiveUI;
using System;
using System.IO;
using System.Reactive.Linq;

namespace TestBrowserApp;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "testapp_debug.log");

    private readonly MainWindowViewModel _vm = new();

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        try { File.WriteAllText(LogPath, $"--- TestBrowserApp started ---\n"); } catch { }

        var browserControl = BrowserControl;
        browserControl.CefSettings.CommandLineSwitches.Add("--allow-file-access-from-files");
        browserControl.CefSettings.CommandLineSwitches.Add("--disable-web-security");

        // Wire ViewModel commands — closures capture controls directly
        _vm.GoCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var url = UrlTextBox.Text ?? "";
            Log($"GoCommand: '{url}'");
            if (!string.IsNullOrWhiteSpace(url))
                await browserControl.NavigateAsync(url);
        });

        _vm.ReloadCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            Log("ReloadCommand");
            await browserControl.ReloadAsync();
        });

        // BrowserView events → update address bar display + ViewModel title
        browserControl.AddressChanged += url =>
            Dispatcher.UIThread.Post(() => UrlTextBox.Text = url);

        browserControl.TitleChanged += title =>
            Dispatcher.UIThread.Post(() => _vm.Title = title);

        browserControl.LoadingStateChanged += loading =>
            Dispatcher.UIThread.Post(() => _vm.IsLoading = loading);

        browserControl.BrowserCrashed += () =>
            Dispatcher.UIThread.Post(() => _vm.Title = "Browser process exited");

        browserControl.LoadError += info =>
            Dispatcher.UIThread.Post(() => _vm.Title = $"LoadError: {info}");

        // Enter key in TextBox triggers GoCommand
        UrlTextBox.KeyUp += (_, e) =>
        {
            if (e.Key == Key.Enter)
                _vm.GoCommand?.Execute(null);
        };

        // When ViewModel sets Address programmatically → update address bar too
        _vm.WhenAnyValue(x => x.Address).Subscribe(url =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrEmpty(url))
                    UrlTextBox.Text = url;
            });
        });
    }
}
