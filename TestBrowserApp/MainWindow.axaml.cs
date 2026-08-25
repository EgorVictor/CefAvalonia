using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using CefSharp.Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

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
        try { File.AppendAllText(LogPath, $"--- TestBrowserApp Multi-Tab started pid={Environment.ProcessId} ---\n"); } catch { }

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

        // --smoke:<url> 自动冒烟：导航到 url → 等 25s → 关窗（--smoke-kill 则由外部强杀）
        var args = Environment.GetCommandLineArgs();
        foreach (var a in args)
        {
            if (a.StartsWith("--smoke:", StringComparison.Ordinal))
            {
                string target = a.Substring("--smoke:".Length);
                bool killOnly = Array.IndexOf(args, "--smoke-kill") >= 0;
                _ = RunSmokeAsync(target, killOnly);
                break;
            }
        }

        // --autotest: 自动验证"关闭标签是否释放原生进程"
        if (Array.IndexOf(args, "--autotest") >= 0)
        {
            _ = RunProcessLeakAutoTestAsync();
        }
    }

    /// <summary>
    /// 冒烟：真实导航(完整子进程集) → 等待 → Close() 窗口走正常退出路径。
    /// 配合外部脚本在关窗/强杀后统计 CefBrowser.Native 残留。
    /// </summary>
    private async Task RunSmokeAsync(string url, bool killOnly)
    {
        try
        {
            await Task.Delay(6000);   // 等首个 tab 宿主就绪
            Log($"[SMOKE] navigate -> {url}");
            await GetCurrentWebView()?.NavigateAsync(url)!;
            await Task.Delay(25000);  // 真实页面完全加载 + 各 utility 稳定
            int alive = CountNativeProcesses();
            Log($"[SMOKE] before close: {alive} native processes");
            if (!killOnly)
            {
                Log("[SMOKE] closing window gracefully");
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Close());
            }
            else
            {
                Log("[SMOKE] leaving process for external hard-kill test");
            }
        }
        catch (Exception ex)
        {
            Log($"[SMOKE] error: {ex}");
        }
    }

    private static int CountNativeProcesses() =>
        Process.GetProcessesByName("CefBrowser.Native").Length;

    /// <summary>
    /// 自动化实验：开 3 个新 tab → 统计进程 → 关闭 2 个 → 再次统计。
    /// 结果写入 autotest_result.log，随后退出程序。
    /// </summary>
    private async Task RunProcessLeakAutoTestAsync()
    {
        try
        {
            var resultPath = Path.Combine(AppContext.BaseDirectory, "autotest_result.log");

            await Task.Delay(8000); // 等第一个 tab 的原生进程就绪
            int baseline = CountNativeProcesses();
            Log($"[AUTOTEST] baseline (1 tab) = {baseline}");

            for (int i = 0; i < 3; i++)
            {
                CreateNewTab();
                await Task.Delay(3000);
            }
            int afterOpen = CountNativeProcesses();
            Log($"[AUTOTEST] after opening 3 more tabs = {afterOpen}");

            // 关闭 2 个 tab（tabId 2 和 3，保留首 tab 与最后一个）
            CloseTab(2);
            await Task.Delay(4000);
            CloseTab(3);
            await Task.Delay(4000);
            int afterClose = CountNativeProcesses();
            Log($"[AUTOTEST] after closing 2 tabs = {afterClose}");

            // 共享宿主模型（Chrome 式）：主进程/GPU/网络/存储全局一份，每 tab 只多 1~2 个 renderer。
            // 断言：4 个 tab 的总进程数必须显著低于旧的"每 tab 一整套"(baseline*4)；
            // 关闭 2 个 tab 后至少释放对应 renderer；退出后归零由外部检查。
            bool pass = afterOpen < baseline * 4 - 3          // 明显低于线性增长
                     && afterClose <= afterOpen - 1;           // 关 tab 有释放
            var verdict = pass
                ? $"PASS: 4 tab 进程 {afterOpen} << 线性 {baseline * 4}，关 2 个后回落到 {afterClose}（共享宿主生效）"
                : $"FAIL: baseline={baseline}, afterOpen={afterOpen}, afterClose={afterClose}";
            Log($"[AUTOTEST] {verdict}");

            // 关键不变量：关闭"最后一个"标签后，宿主进程必须仍存活（应用还能开新 tab）
            CloseTab(1);
            await Task.Delay(3000);
            CloseTab(4);
            await Task.Delay(4000);
            int afterCloseAll = CountNativeProcesses();
            bool hostSurvives = afterCloseAll > 0;
            Log($"[AUTOTEST] after closing ALL tabs = {afterCloseAll} (host survive last-close: {hostSurvives})");
            if (!hostSurvives)
                Log("[AUTOTEST] FAIL: 宿主在最后一个标签关闭时退出，应用无法再开新 tab");
            File.AppendAllText(resultPath,
                $"baseline={baseline}, afterOpen={afterOpen}, afterClose={afterClose} => {verdict}{Environment.NewLine}");

            Environment.Exit(pass ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log($"[AUTOTEST] error: {ex}");
            Environment.Exit(2);
        }
    }

    private void CreateNewTab(string? initialUrl = null)
    {
        if (_browserTabs == null) return;

        int tabId = _tabCounter++;
        Log($"Creating tab {tabId}");

        var webView = new WebView
        {
            CefSettings = new CefSettings
            {
                NoSandbox = true,
                // 每个 tab 独立缓存目录，避免多实例争抢同一 Chromium profile 锁
                CachePath = Path.Combine(AppContext.BaseDirectory, "cef_cache", $"tab{tabId}")
            }
        };
        _tabWebViews[tabId] = webView;

        var tabItem = new TabItem
        {
            Header = BuildTabHeader(tabId, $"Tab {tabId}"),
            Content = webView
        };

        _browserTabs.Items.Add(tabItem);
        _browserTabs.SelectedIndex = _browserTabs.Items.Count - 1;

        WireTabEvents(tabId, webView, tabItem);

        if (initialUrl != null)
        {
            _ = webView.NavigateAsync(initialUrl);
            Log($"Created tab {tabId} navigating to {initialUrl}");
        }
        else
        {
            Log($"Created new tab {tabId}");
        }
    }

    /// <summary>构建带关闭按钮的 tab 头。</summary>
    private object BuildTabHeader(int tabId, string title)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var titleBlock = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        panel.Children.Add(titleBlock);

        var closeButton = new Button
        {
            Content = "✕",
            Padding = new Thickness(4, 0),
            MinWidth = 22,
            Height = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        closeButton.Click += (_, _) => CloseTab(tabId);
        panel.Children.Add(closeButton);

        return panel;
    }

    private void WireTabEvents(int tabId, WebView webView, TabItem tabItem)
    {
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
            UpdateTabTitle(tabItem, title);
        };

        webView.OpenPopup += url =>
        {
            Log($"Tab {tabId} open popup: {url}");
            CreateNewTab(url);
        };
    }

    private static void UpdateTabTitle(TabItem tabItem, string title)
    {
        var display = title.Length > 20 ? title.Substring(0, 20) + "..." : title;
        if (tabItem.Header is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is TextBlock tb && !(tb.Text == "✕"))
                {
                    tb.Text = display;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 关闭标签：先移除 UI（此时子 HWND 仍有效，EBPH.OnDetachedFromVisualTree 能安全把它
    /// reparent 回桌面），再 Cleanup 释放原生浏览器。避免对已销毁的 HWND 操作导致消息循环崩溃。
    /// </summary>
    private void CloseTab(int tabId)
    {
        if (!_tabWebViews.TryGetValue(tabId, out var webView)) return;

        Log($"Closing tab {tabId}...");

        // 1) 先从视觉树移除：EBPH 会把仍有效的 CEF 子 HWND reparent 回桌面并隐藏
        if (_browserTabs != null)
        {
            TabItem? target = null;
            foreach (var item in _browserTabs.Items)
            {
                if (item is TabItem ti && ReferenceEquals(ti.Content, webView))
                {
                    target = ti;
                    break;
                }
            }
            if (target != null)
                _browserTabs.Items.Remove(target);
        }

        _tabWebViews.Remove(tabId);

        // 2) 再释放原生浏览器进程（子 HWND 已脱离面板，销毁安全）
        webView.Cleanup();

        Log($"Tab {tabId} closed");
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

