using Avalonia.Controls;
using Avalonia.Input;
using CefSharp.Avalonia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace TestBrowserApp;

public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "testapp_debug.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
        Debug.WriteLine(line);
    }

    private TabControl? _browserTabs;
    private TextBox? _addressBar;
    private Button? _goButton;
    private Button? _refreshButton;
    private Button? _newTabButton;
    private Dictionary<int, WebView> _tabWebViews = new();
    private int _tabCounter = 1;

    public MainWindow()
    {
        InitializeComponent();
        try { File.WriteAllText(LogPath, $"--- TestBrowserApp Multi-Tab started ---\n"); } catch { }

        _browserTabs = this.FindControl<TabControl>("BrowserTabs");
        _addressBar = this.FindControl<TextBox>("AddressBar");
        _goButton = this.FindControl<Button>("GoButton");
        _refreshButton = this.FindControl<Button>("RefreshButton");
        _newTabButton = this.FindControl<Button>("NewTabButton");

        if (_addressBar != null)
        {
            _addressBar.KeyDown += OnAddressBarKeyDown;
        }

        if (_goButton != null)
        {
            _goButton.Click += (s, e) => Navigate();
        }

        if (_refreshButton != null)
        {
            _refreshButton.Click += (s, e) => Refresh();
        }

        if (_newTabButton != null)
        {
            _newTabButton.Click += (s, e) => CreateNewTab();
        }

        if (_browserTabs != null)
        {
            _browserTabs.SelectionChanged += OnTabSelectionChanged;
        }

        Log("MainWindow initialized - Multi-tab WebView ready");
        CreateNewTab();  // Create first tab
    }

    private void CreateNewTab()
    {
        if (_browserTabs == null) return;

        int tabId = _tabCounter++;
        Log($"Creating tab {tabId}");

        var webView = new WebView
        {
            CefSettings = new CefSettings { NoSandbox = true }
        };
        _tabWebViews[tabId] = webView;

        var tabItem = new TabItem
        {
            Header = $"Tab {tabId}",
            Content = webView
        };

        _browserTabs.Items.Add(tabItem);
        _browserTabs.SelectedIndex = _browserTabs.Items.Count - 1;

        // Wire events to update address bar when tab content changes
        webView.AddressChanged += url =>
        {
            if (_addressBar != null && GetCurrentWebView() == webView)
            {
                Log($"Tab {tabId} address changed: {url}");
                _addressBar.Text = url;
            }
        };

        webView.TitleChanged += title =>
        {
            Log($"Tab {tabId} title changed: {title}");
            if (_browserTabs != null)
            {
                tabItem.Header = title.Length > 20 ? title.Substring(0, 20) + "..." : title;
            }
        };

        webView.OpenPopup += url =>
        {
            Log($"Tab {tabId} open popup: {url}");
            CreateNewTabWithUrl(url);
        };

        Log($"Created new tab {tabId}");
    }

    private void CreateNewTabWithUrl(string url)
    {
        if (_browserTabs == null) return;

        int tabId = _tabCounter++;
        Log($"Creating tab {tabId} for popup: {url}");

        var webView = new WebView
        {
            CefSettings = new CefSettings { NoSandbox = true }
        };
        _tabWebViews[tabId] = webView;

        var tabItem = new TabItem
        {
            Header = $"Tab {tabId}",
            Content = webView
        };

        _browserTabs.Items.Add(tabItem);
        _browserTabs.SelectedIndex = _browserTabs.Items.Count - 1;

        // Wire events
        webView.AddressChanged += addr =>
        {
            if (_addressBar != null && GetCurrentWebView() == webView)
            {
                Log($"Tab {tabId} address changed: {addr}");
                _addressBar.Text = addr;
            }
        };

        webView.TitleChanged += title =>
        {
            Log($"Tab {tabId} title changed: {title}");
            if (_browserTabs != null)
                tabItem.Header = title.Length > 20 ? title.Substring(0, 20) + "..." : title;
        };

        webView.OpenPopup += popupUrl =>
        {
            Log($"Tab {tabId} open popup: {popupUrl}");
            CreateNewTabWithUrl(popupUrl);
        };

        _ = webView.NavigateAsync(url);
        Log($"Created tab {tabId} navigating to {url}");
    }

    private WebView? GetCurrentWebView()
    {
        if (_browserTabs == null || _browserTabs.SelectedIndex < 0)
            return null;

        var tabItem = _browserTabs.Items[_browserTabs.SelectedIndex] as TabItem;
        return tabItem?.Content as WebView;
    }

    private void OnTabSelectionChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        Log($"=== TAB SWITCHED === Index={_browserTabs?.SelectedIndex}");
        var webView = GetCurrentWebView();
        if (webView != null)
        {
            Log($"Active WebView: IsVisible={webView.IsVisible}, Url={webView.Url}");
            Console.Error.WriteLine($"DIAG: TAB SWITCHED to visible WebView");
        }
        UpdateAddressBar();
    }

    private void UpdateAddressBar()
    {
        var webView = GetCurrentWebView();
        if (_addressBar != null)
        {
            _addressBar.Text = webView?.Url ?? "";
        }
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
        var webView = GetCurrentWebView();
        if (webView == null || _addressBar == null || string.IsNullOrWhiteSpace(_addressBar.Text))
        {
            Log($"Navigate: invalid state (wv={webView!=null} ab={_addressBar!=null} txt={_addressBar?.Text})");
            return;
        }

        var url = _addressBar.Text;
        Log($"Navigating to: {url}");
        Console.Error.WriteLine($"DIAG: MAINWINDOW Navigate url={url}");
        _ = webView.NavigateAsync(url);
    }

    private void Refresh()
    {
        var webView = GetCurrentWebView();
        if (webView != null)
        {
            Log("Refreshing current tab");
            _ = webView.ReloadAsync();
        }
    }
}

