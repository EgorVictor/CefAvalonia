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
using System.Linq;
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
    private Button? _backButton;
    private Button? _forwardButton;
    private Button? _devToolsButton;
    private Button? _stopButton;
    private Button? _zoomOutButton;
    private Button? _zoomResetButton;
    private Button? _zoomInButton;
    private Button? _findButton;
    private DockPanel? _findBar;
    private TextBox? _findTextBox;
    private Button? _findCloseButton;
    private Button? _findPrevButton;
    private Button? _findNextButton;
    private TextBlock? _statusBar;
    private ProgressBar? _downloadProgressBar;
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
        _backButton = this.FindControl<Button>("BackButton");
        _forwardButton = this.FindControl<Button>("ForwardButton");
        _devToolsButton = this.FindControl<Button>("DevToolsButton");
        _stopButton = this.FindControl<Button>("StopButton");
        _zoomOutButton = this.FindControl<Button>("ZoomOutButton");
        _zoomResetButton = this.FindControl<Button>("ZoomResetButton");
        _zoomInButton = this.FindControl<Button>("ZoomInButton");
        _findButton = this.FindControl<Button>("FindButton");
        _findBar = this.FindControl<DockPanel>("FindBar");
        _findTextBox = this.FindControl<TextBox>("FindTextBox");
        _findCloseButton = this.FindControl<Button>("FindCloseButton");
        _findPrevButton = this.FindControl<Button>("FindPrevButton");
        _findNextButton = this.FindControl<Button>("FindNextButton");
        _statusBar = this.FindControl<TextBlock>("StatusBar");
        _downloadProgressBar = this.FindControl<ProgressBar>("DownloadProgressBar");

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

        if (_backButton != null)
        {
            _backButton.Click += (s, e) => GetCurrentWebView()?.GoBack();
        }

        if (_forwardButton != null)
        {
            _forwardButton.Click += (s, e) => GetCurrentWebView()?.GoForward();
        }

        if (_devToolsButton != null)
        {
            _devToolsButton.Click += (s, e) => GetCurrentWebView()?.ShowDeveloperTools();
        }

        if (_stopButton != null)
        {
            _stopButton.Click += (s, e) => GetCurrentWebView()?.StopAsync();
        }

        if (_zoomOutButton != null)
            _zoomOutButton.Click += (s, e) => GetCurrentWebView()?.ZoomOut();

        if (_zoomResetButton != null)
            _zoomResetButton.Click += (s, e) => GetCurrentWebView()?.ZoomReset();

        if (_zoomInButton != null)
            _zoomInButton.Click += (s, e) => GetCurrentWebView()?.ZoomIn();

        if (_findButton != null)
        {
            _findButton.Click += (s, e) =>
            {
                if (_findBar != null) _findBar.IsVisible = !_findBar.IsVisible;
                _findTextBox?.Focus();
            };
        }

        if (_findCloseButton != null)
        {
            _findCloseButton.Click += (s, e) =>
            {
                if (_findBar != null) _findBar.IsVisible = false;
                GetCurrentWebView()?.StopFinding();
            };
        }

        if (_findNextButton != null)
            _findNextButton.Click += (s, e) => GetCurrentWebView()?.FindNext();

        if (_findPrevButton != null)
            _findPrevButton.Click += (s, e) => GetCurrentWebView()?.FindPrevious();

        if (_findTextBox != null)
        {
            _findTextBox.TextChanged += (s, e) =>
            {
                var wv = GetCurrentWebView();
                if (wv == null || _findTextBox == null) return;
                var t = _findTextBox.Text ?? "";
                if (string.IsNullOrEmpty(t)) wv.StopFinding();
                else wv.Find(t);
            };
            _findTextBox.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Return)
                {
                    GetCurrentWebView()?.FindNext();
                    e.Handled = true;
                }
            };
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

        // Release every browser when the window closes. The native host is a
        // separate process; without this each tab would keep its renderer alive.
        Closing += (_, _) =>
        {
            foreach (var webView in _tabWebViews.Values.ToList())
                webView.Cleanup();
            _tabWebViews.Clear();
        };

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
            if (GetCurrentWebView() == webView)
                Title = string.IsNullOrEmpty(title) ? "Modern Browser" : title;
        };

        webView.OpenPopup += url =>
        {
            Log($"Tab {tabId} open popup: {url}");
            CreateNewTab(url);
        };

        webView.DownloadEvent += info =>
        {
            Log($"Tab {tabId} download event: {info}");
            // info format: DownloadStart|{path}, DownloadProgress|{received}|{total},
            //             DownloadComplete|{path}, DownloadCanceled
            var sep = info.IndexOf('|');
            var kind = sep >= 0 ? info[..sep] : info;
            var rest = sep >= 0 ? info[(sep + 1)..] : "";
            UpdateDownloadUI(kind, rest);
        };

        // Surface navigation failures (e.g. DNS/connection errors, blocked hosts)
        // in the status bar so a failed load isn't mistaken for "nothing happened".
        webView.LoadError += info =>
        {
            // info = "{code}|{text}|{url}"
            var p = info.Split('|');
            var text = p.Length >= 2 ? p[1] : info;
            var url = p.Length >= 3 ? p[2] : "";
            Log($"Tab {tabId} load error: {info}");
            if (_statusBar != null)
                _statusBar.Text = $"加载失败: {text}  ({url})";
        };

        // Keep Back/Forward buttons in sync with the browser history state.
        webView.PropertyChanged += (_, e) =>
        {
            if (e.Property == WebView.CanGoBackProperty || e.Property == WebView.CanGoForwardProperty)
                UpdateNavButtons();
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
        UpdateNavButtons();
        Title = webView?.Title is { Length: > 0 } t ? t : "Modern Browser";
    }

    private void UpdateAddressBar()
    {
        var webView = GetCurrentWebView();
        if (_addressBar != null)
        {
            _addressBar.Text = webView?.Url ?? "";
        }
    }

    private void UpdateNavButtons()
    {
        var webView = GetCurrentWebView();
        if (_backButton != null)
            _backButton.IsEnabled = webView?.CanGoBack == true;
        if (_forwardButton != null)
            _forwardButton.IsEnabled = webView?.CanGoForward == true;
    }

    // ---- Download UI ----
    private void UpdateDownloadUI(string kind, string rest)
    {
        switch (kind)
        {
            case "DownloadStart":
                if (_statusBar != null)
                    _statusBar.Text = $"下载中: {Path.GetFileName(rest)}";
                if (_downloadProgressBar != null)
                {
                    _downloadProgressBar.IsVisible = true;
                    _downloadProgressBar.IsIndeterminate = true;
                    _downloadProgressBar.Value = 0;
                }
                break;
            case "DownloadProgress":
                var p = rest.Split('|');
                var received = p.Length >= 1 ? p[0] : "0";
                var total = p.Length >= 2 ? p[1] : "0";
                if (long.TryParse(received, out var r) &&
                    long.TryParse(total, out var t) && t > 0)
                {
                    if (_downloadProgressBar != null)
                    {
                        _downloadProgressBar.IsIndeterminate = false;
                        _downloadProgressBar.Value = Math.Min(100.0, (double)r / t * 100);
                    }
                    if (_statusBar != null)
                        _statusBar.Text =
                            $"下载中: {FormatBytes(r)} / {FormatBytes(t)} ({r * 100 / t:F0}%)";
                }
                else if (_downloadProgressBar != null)
                {
                    _downloadProgressBar.IsIndeterminate = true;
                }
                break;
            case "DownloadComplete":
                if (_statusBar != null)
                    _statusBar.Text = $"下载完成: {rest}";
                if (_downloadProgressBar != null)
                    _downloadProgressBar.IsVisible = false;
                break;
            case "DownloadCanceled":
                if (_statusBar != null)
                    _statusBar.Text = "下载已取消";
                if (_downloadProgressBar != null)
                    _downloadProgressBar.IsVisible = false;
                break;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
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

