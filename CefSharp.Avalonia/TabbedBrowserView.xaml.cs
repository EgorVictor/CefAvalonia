using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Complete tabbed browser control with tab bar, address bar, and WebView hosting.
/// Pure C# implementation (no XAML code-behind dependencies).
/// </summary>
public class TabbedBrowserView : UserControl
{
    private TabManager? _tabManager;
    private ProcessPool? _processPool;
    private StackPanel? _tabBar;
    private TextBox? _addressBar;
    private Grid? _browserContainer;
    private TextBlock? _statusBar;
    private readonly Dictionary<Guid, WebView> _tabWebViews = new();  // ← 存储每个tab的WebView

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
        try
        {
            InitializeUI();
            InitializeBrowser();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabbedBrowserView] Constructor error: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Build complete UI from code (no XAML code-behind issues).
    /// </summary>
    private void InitializeUI()
    {
        // Root grid with rows: menu, tabs, content, status
        var rootGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = Brushes.White
        };

        // Row 0: Menu bar
        var menuBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            Height = 32
        };

        var menuTitle = new TextBlock
        {
            Text = "CefBrowser",
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#333")),
            Margin = new Thickness(8, 0, 0, 0)
        };
        menuBar.Children.Add(menuTitle);

        var newTabBtn = new Button { Content = "⊕", Width = 28, Height = 28, Margin = new Thickness(4, 0) };
        newTabBtn.Click += OnNewTabClick;
        Grid.SetColumn(newTabBtn, 1);
        menuBar.Children.Add(newTabBtn);

        Grid.SetRow(menuBar, 0);
        rootGrid.Children.Add(menuBar);

        // Row 1: Tab bar (placeholder - will be updated by TabManager)
        _tabBar = new StackPanel { Orientation = Orientation.Horizontal };
        var tabScroll = new ScrollViewer
        {
            Content = _tabBar,
            Background = new SolidColorBrush(Color.Parse("#ECECEC")),
            Height = 36
        };
        Grid.SetRow(tabScroll, 1);
        rootGrid.Children.Add(tabScroll);

        // Row 2: Address bar + content
        var contentGrid = new Grid { RowDefinitions = new RowDefinitions("36,*"), Background = Brushes.White };

        // Address bar container with Grid inside for TextBox + Button
        var addressBarBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9F9F9")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(4, 2)
        };

        var addressBarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };

        _addressBar = new TextBox
        {
            Watermark = "输入网址 (例如: https://www.google.com) - 按Enter导航",
            Height = 28,
            MinWidth = 400
        };
        _addressBar.KeyDown += OnAddressBarKeyDown;
        addressBarPanel.Children.Add(_addressBar);

        // "Go" 按钮
        var goBtn = new Button
        {
            Content = "Go",
            Width = 50,
            Height = 28
        };
        goBtn.Click += OnGoButtonClick;
        addressBarPanel.Children.Add(goBtn);

        addressBarBorder.Child = addressBarPanel;
        Grid.SetRow(addressBarBorder, 0);
        contentGrid.Children.Add(addressBarBorder);

        // Row 2, Content area
        _browserContainer = new Grid { Background = Brushes.White };
        Grid.SetRow(_browserContainer, 1);
        contentGrid.Children.Add(_browserContainer);

        Grid.SetRow(contentGrid, 2);
        rootGrid.Children.Add(contentGrid);

        // Row 3: Status bar
        _statusBar = new TextBlock
        {
            Text = "Ready",
            Padding = new Thickness(8, 4),
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            Foreground = new SolidColorBrush(Color.Parse("#666")),
            FontSize = 11
        };
        Grid.SetRow(_statusBar, 3);
        rootGrid.Children.Add(_statusBar);

        Content = rootGrid;
        Debug.WriteLine("[TabbedBrowserView] UI initialized");
    }

    private void InitializeBrowser()
    {
        try
        {
            _processPool = new ProcessPool();
            _tabManager = new TabManager(_processPool);

            _tabManager.ActiveTabChanged += OnActiveTabChanged;
            _tabManager.TabAdded += OnTabAdded;
            _tabManager.TabRemoved += OnTabRemoved;
            _tabManager.TabProcessCrashed += OnTabProcessCrashed;

            Debug.WriteLine("[TabbedBrowserView] Browser initialized");
            _ = CreateNewTabAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabbedBrowserView] InitializeBrowser error: {ex}");
        }
    }

    private async Task CreateNewTabAsync()
    {
        if (_tabManager == null) return;

        try
        {
            var tab = await _tabManager.AddTabAsync("about:blank", "New Tab");

            // 创建WebView并关联到Tab
            var webView = new WebView
            {
                Url = tab.Url,
                CefSettings = CefSettings
            };

            // 保存WebView引用供后续显示
            if (!_tabWebViews.ContainsKey(tab.Id))
                _tabWebViews[tab.Id] = webView;

            // 绑定事件
            webView.AddressChanged += url =>
            {
                tab.Url = url;
                tab.Title = url.Length > 30 ? url.Substring(0, 30) + "..." : url;
                _addressBar.Text = url;  // 同步地址栏
            };

            webView.TitleChanged += title =>
            {
                tab.Title = title;
            };

            webView.LoadingStateChanged += loading =>
            {
                _statusBar.Text = loading ? "Loading..." : "Ready";
            };

            webView.LoadError += info =>
            {
                _statusBar.Text = $"Error: {info}";
            };

            Debug.WriteLine($"[TabbedBrowserView] Created tab: {tab.Title}");

            // 自动选中新创建的tab
            await _tabManager.SelectTabAsync(tab.Id);
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

    /// <summary>地址栏Enter键导航</summary>
    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
    {
        Debug.WriteLine($"[TabbedBrowserView] AddressBar KeyDown: Key={e.Key}");

        if (e.Key == Key.Return)
        {
            Debug.WriteLine("[TabbedBrowserView] Enter key pressed in address bar");
            var url = _addressBar?.Text;

            if (string.IsNullOrWhiteSpace(url))
            {
                Debug.WriteLine("[TabbedBrowserView] URL is empty");
                return;
            }

            if (_tabManager?.ActiveTab == null)
            {
                Debug.WriteLine("[TabbedBrowserView] No active tab");
                return;
            }

            Debug.WriteLine($"[TabbedBrowserView] Navigating to: {url}");
            NavigateCurrentTab(url);
            e.Handled = true;
        }
    }

    /// <summary>Go按钮点击导航</summary>
    private void OnGoButtonClick(object? sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[TabbedBrowserView] Go button clicked");
        var url = _addressBar?.Text;

        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.WriteLine("[TabbedBrowserView] URL is empty");
            return;
        }

        if (_tabManager?.ActiveTab == null)
        {
            Debug.WriteLine("[TabbedBrowserView] No active tab");
            return;
        }

        Debug.WriteLine($"[TabbedBrowserView] Navigating to: {url}");
        NavigateCurrentTab(url);
    }

    /// <summary>导航当前活跃标签页</summary>
    private void NavigateCurrentTab(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.WriteLine("[TabbedBrowserView] NavigateCurrentTab: URL is empty");
            return;
        }

        if (_tabManager?.ActiveTab == null)
        {
            Debug.WriteLine("[TabbedBrowserView] NavigateCurrentTab: No active tab");
            return;
        }

        var tab = _tabManager.ActiveTab;
        Debug.WriteLine($"[TabbedBrowserView] NavigateCurrentTab({url}) for tab {tab.Title}");

        if (_tabWebViews.TryGetValue(tab.Id, out var webView))
        {
            Debug.WriteLine($"[TabbedBrowserView] Found WebView for tab, navigating...");
            _ = webView.NavigateAsync(url);
        }
        else
        {
            Debug.WriteLine($"[TabbedBrowserView] ERROR: No WebView found for tab {tab.Title}");
        }
    }

    private void OnActiveTabChanged(TabItem? tab)
    {
        try
        {
            if (tab == null)
            {
                // 清空浏览器容器
                _browserContainer?.Children.Clear();
                _addressBar.Text = "";
                Debug.WriteLine("[TabbedBrowserView] No active tab");
                return;
            }

            Debug.WriteLine($"[TabbedBrowserView] Switched to tab: {tab.Title}");
            _addressBar.Text = tab.Url;

            // 显示当前tab的WebView
            if (_tabWebViews.TryGetValue(tab.Id, out var webView))
            {
                _browserContainer?.Children.Clear();
                _browserContainer?.Children.Add(webView);
                Debug.WriteLine($"[TabbedBrowserView] Displayed WebView for tab {tab.Title}");
            }
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
        Debug.WriteLine($"[TabbedBrowserView] Tab crashed: {tab.Title}");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tabManager?.Dispose();
        _processPool?.Dispose();
        Debug.WriteLine("[TabbedBrowserView] Disposed");
    }
}
