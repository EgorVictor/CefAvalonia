using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CefSharp;
using CefSharp.WinForms;
using System;
using System.Runtime.InteropServices;

namespace CefSharp.Avalonia;

public partial class MainWindow : Window
{
    private TextBox urlTextBox;
    private Button goButton;
    private Panel browserPanel;
    private ChromiumWebBrowser? browser;
    private IntPtr containerHwnd = IntPtr.Zero;
    private bool browserCreated;
    private DispatcherTimer? pumpTimer;

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
            if (e.Key == Key.Enter)
            {
                NavigateToUrl();
            }
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = true;

        pumpTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        pumpTimer.Tick += (_, _) => System.Windows.Forms.Application.DoEvents();
        pumpTimer.Start();

        Dispatcher.UIThread.Post(CreateBrowser, DispatcherPriority.Normal);
    }

    private void CreateBrowser()
    {
        if (browserCreated) return;
        browserCreated = true;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var platformHandle = topLevel.TryGetPlatformHandle();
        if (platformHandle == null) return;

        containerHwnd = CreateWindowEx(
            0, "Static", "", 0x40000000, // WS_CHILD only
            0, 0, 0, 0, platformHandle.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        browser = new ChromiumWebBrowser("https://www.bing.com");
        browser.CreateControl();

        SetParent(browser.Handle, containerHwnd);
        ResizeBrowser();

        browser.AddressChanged += (sender, args) =>
        {
            Dispatcher.UIThread.Post(() => urlTextBox.Text = args.Address);
        };

        browser.LoadError += (sender, args) =>
        {
            if (args.ErrorCode != CefErrorCode.Aborted)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var dialog = new Window
                    {
                        Title = "Error",
                        Width = 300,
                        Height = 100,
                        Content = new TextBlock { Text = $"Page failed to load: {args.ErrorText}", Margin = new Thickness(10) }
                    };
                    dialog.ShowDialog(this);
                });
            }
        };

        browserPanel.SizeChanged += (_, _) => ResizeBrowser();
    }

    private void ResizeBrowser()
    {
        if (browser == null || containerHwnd == IntPtr.Zero) return;

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var pos = browserPanel.TranslatePoint(new Point(0, 0), this);
        if (pos == null) return;

        var x = (int)(pos.Value.X * scale);
        var y = (int)(pos.Value.Y * scale);
        var w = (int)(browserPanel.Bounds.Width * scale);
        var h = (int)(browserPanel.Bounds.Height * scale);

        if (w <= 0 || h <= 0) return;

        SetWindowPos(containerHwnd, IntPtr.Zero, x, y, w, h, 0x0020 | 0x0040);
        SetWindowPos(browser.Handle, IntPtr.Zero, 0, 0, w, h, 0x0040);
    }

    private void GoButton_Click(object? sender, RoutedEventArgs e) => NavigateToUrl();

    private void NavigateToUrl()
    {
        if (browser == null || urlTextBox == null) return;
        string url = urlTextBox.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;
            urlTextBox.Text = url;
            browser.Load(url);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
}
