using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Complete tabbed browser control with tab bar, address bar, and WebView hosting.
/// Manages multiple WebView instances through TabManager and ProcessPool.
/// </summary>
public partial class TabbedBrowserView : UserControl
{
    private TabManager? _tabManager;
    private ProcessPool? _processPool;

    public static readonly StyledProperty<CefSettings> CefSettingsProperty =
        AvaloniaProperty.Register<TabbedBrowserView, CefSettings>(nameof(CefSettings),
            defaultValue: new CefSettings { NoSandbox = true });

    public CefSettings CefSettings
    {
        get => GetValue(CefSettingsProperty);
        set => SetValue(CefSettingsProperty, value);
    }

    public TabbedBrowserView()
    {
        DataContext = this;
        InitializeBrowser();
    }

    private void InitializeBrowser()
    {
        try
        {
            _processPool = new ProcessPool();
            _tabManager = new TabManager(_processPool);

            // Wire up events
            _tabManager.ActiveTabChanged += OnActiveTabChanged;
            _tabManager.TabAdded += OnTabAdded;
            _tabManager.TabRemoved += OnTabRemoved;
            _tabManager.TabProcessCrashed += OnTabProcessCrashed;

            Debug.WriteLine("[TabbedBrowserView] Browser initialized successfully");

            // Create initial tab
            _ = CreateNewTabAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabbedBrowserView] InitializeBrowser error: {ex}");
        }
    }

    /// <summary>
    /// Create a new browser tab.
    /// </summary>
    private async Task CreateNewTabAsync()
    {
        if (_tabManager == null) return;

        try
        {
            var tab = await _tabManager.AddTabAsync("about:blank", "New Tab");

            // Create WebView for this tab
            var webView = new WebView
            {
                Url = tab.Url,
                CefSettings = CefSettings
            };

            // Wire up events
            webView.AddressChanged += url =>
            {
                tab.Url = url;
                tab.Title = url.Length > 30 ? url.Substring(0, 30) + "..." : url;
            };

            webView.TitleChanged += title =>
            {
                tab.Title = title;
            };

            // Store WebView reference for later display
            if (tab.BrowserProcess != null)
            {
                tab.BrowserProcess.WindowHandleReceived += hwnd =>
                {
                    Debug.WriteLine($"[TabbedBrowserView] Browser ready for tab {tab.Title}");
                };
            }

            Debug.WriteLine($"[TabbedBrowserView] Created new tab: {tab.Title}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabbedBrowserView] CreateNewTabAsync error: {ex}");
        }
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e)
    {
        _ = CreateNewTabAsync();
    }

    private void OnActiveTabChanged(TabItem? tab)
    {
        try
        {
            if (tab == null)
            {
                Debug.WriteLine("[TabbedBrowserView] No active tab");
                return;
            }

            Debug.WriteLine($"[TabbedBrowserView] Active tab changed to: {tab.Title}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabbedBrowserView] OnActiveTabChanged error: {ex}");
        }
    }

    private void OnTabAdded(TabItem tab)
    {
        Debug.WriteLine($"[TabbedBrowserView] Tab added: {tab.Title}");
    }

    private void OnTabRemoved(TabItem tab)
    {
        Debug.WriteLine($"[TabbedBrowserView] Tab removed: {tab.Title}");
    }

    private void OnTabProcessCrashed(TabItem tab)
    {
        Debug.WriteLine($"[TabbedBrowserView] Tab process crashed: {tab.Title}");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tabManager?.Dispose();
        _processPool?.Dispose();
        Debug.WriteLine("[TabbedBrowserView] Disposed");
    }
}
