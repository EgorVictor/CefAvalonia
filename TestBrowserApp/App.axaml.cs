using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CefSharp.Avalonia;

namespace TestBrowserApp;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // Ensure the native browser host is torn down on exit. Without this
            // CefBrowser.Native can outlive the app.
            desktop.Exit += (_, _) => CefHostProcess.ShutdownAll();
        }

        base.OnFrameworkInitializationCompleted();
    }
}