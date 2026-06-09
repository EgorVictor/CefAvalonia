using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Manages browser tabs lifecycle, switching, and process allocation.
/// Serves as the central orchestrator between UI, WebView instances, and ProcessPool.
/// </summary>
public sealed class TabManager : IDisposable
{
    private readonly ProcessPool _processPool;
    private readonly object _lock = new();
    public ObservableCollection<TabItem> Tabs { get; } = new();
    private TabItem? _activeTab;
    private bool _disposed;

    public TabItem? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (_activeTab != value)
            {
                _activeTab = value;
                ActiveTabChanged?.Invoke(value);
            }
        }
    }

    /// <summary>Raised when the active tab changes.</summary>
    public event Action<TabItem?>? ActiveTabChanged;

    /// <summary>Raised when a tab is added.</summary>
    public event Action<TabItem>? TabAdded;

    /// <summary>Raised when a tab is removed.</summary>
    public event Action<TabItem>? TabRemoved;

    /// <summary>Raised when a process crashes.</summary>
    public event Action<TabItem>? TabProcessCrashed;

    public TabManager(ProcessPool processPool)
    {
        _processPool = processPool;
        _processPool.ProcessCrashed += OnProcessCrashed;
    }

    /// <summary>
    /// Create and add a new tab.
    /// </summary>
    public async Task<TabItem> AddTabAsync(string? url = null, string? title = null)
    {
        var tab = new TabItem(url ?? "about:blank");
        if (title != null)
            tab.Title = title;

        lock (_lock)
        {
            Tabs.Add(tab);
        }

        Debug.WriteLine($"[TabManager] AddTabAsync: Created tab {tab.Id:N} ({tab.Title})");

        // Allocate process
        try
        {
            var process = await _processPool.GetOrCreateProcessAsync(tab.Id);
            tab.BrowserProcess = process;
            Debug.WriteLine($"[TabManager] AddTabAsync: Allocated process for tab {tab.Id:N}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabManager] AddTabAsync error: {ex}");
        }

        TabAdded?.Invoke(tab);

        // If this is the first tab, make it active
        if (ActiveTab == null)
        {
            await SelectTabAsync(tab.Id);
        }

        return tab;
    }

    /// <summary>
    /// Activate a tab and switch process if needed.
    /// </summary>
    public async Task SelectTabAsync(Guid tabId)
    {
        TabItem? tab = null;

        lock (_lock)
        {
            tab = Tabs.FirstOrDefault(t => t.Id == tabId);
        }

        if (tab == null)
        {
            Debug.WriteLine($"[TabManager] SelectTabAsync: Tab {tabId:N} not found");
            return;
        }

        try
        {
            if (ActiveTab != null && ActiveTab.Id != tab.Id)
            {
                // Switch from old tab to new tab
                await _processPool.SwitchActiveTabAsync(ActiveTab.Id, tab.Id);
                ActiveTab.IsActive = false;
            }

            ActiveTab = tab;
            ActiveTab.IsActive = true;
            ActiveTab.IsFrozen = false;

            Debug.WriteLine($"[TabManager] SelectTabAsync: Switched to tab {tab.Id:N}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabManager] SelectTabAsync error: {ex}");
        }
    }

    /// <summary>
    /// Close a tab and release its resources.
    /// </summary>
    public async Task CloseTabAsync(Guid tabId)
    {
        TabItem? tab = null;

        lock (_lock)
        {
            tab = Tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
            {
                Tabs.Remove(tab);
            }
        }

        if (tab == null) return;

        try
        {
            // Release process back to pool
            _processPool.ReleaseTabProcess(tab.Id);
            tab.BrowserProcess = null;

            Debug.WriteLine($"[TabManager] CloseTabAsync: Closed tab {tab.Id:N}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TabManager] CloseTabAsync error: {ex}");
        }

        TabRemoved?.Invoke(tab);

        // If closed tab was active, switch to next available tab
        if (ActiveTab?.Id == tabId)
        {
            TabItem? nextTab = null;
            lock (_lock)
            {
                nextTab = Tabs.FirstOrDefault();
            }

            if (nextTab != null)
            {
                await SelectTabAsync(nextTab.Id);
            }
            else
            {
                ActiveTab = null;
            }
        }
    }

    /// <summary>
    /// Get a tab by ID.
    /// </summary>
    public TabItem? GetTab(Guid tabId)
    {
        lock (_lock)
        {
            return Tabs.FirstOrDefault(t => t.Id == tabId);
        }
    }

    /// <summary>
    /// Get tab count.
    /// </summary>
    public int GetTabCount()
    {
        lock (_lock)
        {
            return Tabs.Count;
        }
    }

    private void OnProcessCrashed(Guid tabId)
    {
        var tab = GetTab(tabId);
        if (tab != null)
        {
            Debug.WriteLine($"[TabManager] Process crashed for tab {tab.Title}");
            TabProcessCrashed?.Invoke(tab);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _processPool.ProcessCrashed -= OnProcessCrashed;

        lock (_lock)
        {
            Tabs.Clear();
        }
    }
}
