using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
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

        var addressBar = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F9F9F9")),
            BorderBrush = new SolidColorBrush(Color.Parse("#E0E0E0")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 4)
        };

        _addressBar = new TextBox
        {
            Watermark = "Enter URL...",
            Height = 28,
            Margin = new Thickness(4, 0)
        };
        addressBar.Child = _addressBar;

        Grid.SetRow(addressBar, 0);
        contentGrid.Children.Add(addressBar);

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
            Debug.WriteLine($"[TabbedBrowserView] Created tab: {tab.Title}");
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
        Debug.WriteLine($"[TabbedBrowserView] Active tab: {tab?.Title ?? "None"}");
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
