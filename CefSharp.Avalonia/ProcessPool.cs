using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Global process pool for managing CEF browser processes across multiple tabs.
/// Implements process reuse and caching to minimize memory overhead.
///
/// Design: One active process per browser container. When a tab becomes inactive,
/// its process is frozen (IPC paused). When switching tabs, active process changes.
/// Cached processes are reused when new tabs are opened, reducing memory footprint.
/// </summary>
public sealed class ProcessPool : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<BrowserProcessManager> _processCache = new();
    private readonly Dictionary<Guid, BrowserProcessManager> _tabProcessMap = new();
    private readonly ConcurrentDictionary<Guid, int> _tabResizeCache = new();
    private Guid _activeTabId = Guid.Empty;
    private bool _disposed;
    private const int MAX_CACHED_PROCESSES = 3;

    /// <summary>Raised when a process crashes unexpectedly.</summary>
    public event Action<Guid>? ProcessCrashed;

    public ProcessPool()
    {
        AppDomain.CurrentDomain.ProcessExit += OnAppExit;
    }

    /// <summary>
    /// Get or create a BrowserProcessManager for the given tab.
    /// If a cached process is available, reuse it; otherwise create a new one.
    /// </summary>
    public async Task<BrowserProcessManager> GetOrCreateProcessAsync(Guid tabId)
    {
        lock (_lock)
        {
            // Already have a process for this tab
            if (_tabProcessMap.TryGetValue(tabId, out var existing))
            {
                Debug.WriteLine($"[ProcessPool] GetOrCreateProcessAsync({tabId:N}): Reusing existing process");
                return existing;
            }

            // Try to reuse a cached process
            if (_processCache.Count > 0)
            {
                var cached = _processCache.Dequeue();
                _tabProcessMap[tabId] = cached;
                Debug.WriteLine($"[ProcessPool] GetOrCreateProcessAsync({tabId:N}): Reusing cached process (cache size: {_processCache.Count})");
                return cached;
            }
        }

        // Create new process
        Debug.WriteLine($"[ProcessPool] GetOrCreateProcessAsync({tabId:N}): Creating new process");
        var newProcess = new BrowserProcessManager();
        newProcess.BrowserCrashed += () => OnProcessCrashed(tabId);

        lock (_lock)
        {
            _tabProcessMap[tabId] = newProcess;
        }

        return newProcess;
    }

    /// <summary>
    /// Switch active tab from one to another.
    /// Freezes the old tab's IPC and resumes the new tab's IPC.
    /// This maintains single active process for memory efficiency.
    /// </summary>
    public async Task SwitchActiveTabAsync(Guid fromTabId, Guid toTabId)
    {
        BrowserProcessManager? oldProcess = null;
        BrowserProcessManager? newProcess = null;

        lock (_lock)
        {
            if (_tabProcessMap.TryGetValue(fromTabId, out var old))
                oldProcess = old;

            if (_tabProcessMap.TryGetValue(toTabId, out var next))
                newProcess = next;

            _activeTabId = toTabId;
        }

        // Freeze old tab's IPC
        if (oldProcess != null)
        {
            Debug.WriteLine($"[ProcessPool] SwitchActiveTabAsync: Freezing process for tab {fromTabId:N}");
            oldProcess.FreezeIpc();
        }

        // Resume new tab's IPC
        if (newProcess != null)
        {
            Debug.WriteLine($"[ProcessPool] SwitchActiveTabAsync: Resuming process for tab {toTabId:N}");
            await newProcess.ResumeIpcAsync();
        }

        Debug.WriteLine($"[ProcessPool] SwitchActiveTabAsync: Switched from {fromTabId:N} to {toTabId:N}");
    }

    /// <summary>
    /// Release a tab's process. Decides whether to cache or kill based on cache size.
    /// </summary>
    public void ReleaseTabProcess(Guid tabId)
    {
        BrowserProcessManager? process = null;

        lock (_lock)
        {
            if (_tabProcessMap.TryGetValue(tabId, out process))
            {
                _tabProcessMap.Remove(tabId);
            }

            if (process == null) return;

            // If cache is full, kill the process; otherwise, cache it
            if (_processCache.Count >= MAX_CACHED_PROCESSES)
            {
                Debug.WriteLine($"[ProcessPool] ReleaseTabProcess({tabId:N}): Cache full, killing process");
                process.Dispose();
            }
            else
            {
                Debug.WriteLine($"[ProcessPool] ReleaseTabProcess({tabId:N}): Caching process (cache size: {_processCache.Count + 1})");
                _processCache.Enqueue(process);
            }
        }

        _tabResizeCache.TryRemove(tabId, out _);
    }

    /// <summary>
    /// Cache resize dimensions for a tab. Used to restore layout when tab is resumed.
    /// </summary>
    public void CacheTabResize(Guid tabId, int width, int height)
    {
        _tabResizeCache[tabId] = (width << 16) | (height & 0xFFFF);
    }

    /// <summary>
    /// Get cached resize dimensions for a tab.
    /// </summary>
    public (int width, int height) GetCachedResize(Guid tabId)
    {
        if (_tabResizeCache.TryGetValue(tabId, out var packed))
        {
            return (packed >> 16, packed & 0xFFFF);
        }
        return (1024, 768);  // Default fallback
    }

    /// <summary>
    /// Get the current active tab ID.
    /// </summary>
    public Guid GetActiveTabId() => _activeTabId;

    /// <summary>
    /// Check if a tab has an active process.
    /// </summary>
    public bool HasProcessForTab(Guid tabId)
    {
        lock (_lock)
        {
            return _tabProcessMap.ContainsKey(tabId);
        }
    }

    /// <summary>
    /// Get process count (for diagnostics).
    /// </summary>
    public (int active, int cached) GetProcessCounts()
    {
        lock (_lock)
        {
            return (_tabProcessMap.Count, _processCache.Count);
        }
    }

    private void OnProcessCrashed(Guid tabId)
    {
        Debug.WriteLine($"[ProcessPool] Process crashed for tab {tabId:N}");

        lock (_lock)
        {
            _tabProcessMap.Remove(tabId);
        }

        ProcessCrashed?.Invoke(tabId);
    }

    private void OnAppExit(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.ProcessExit -= OnAppExit;

        lock (_lock)
        {
            // Kill all cached processes
            while (_processCache.Count > 0)
            {
                var proc = _processCache.Dequeue();
                try
                {
                    Debug.WriteLine("[ProcessPool] Dispose: Killing cached process");
                    proc.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessPool] Dispose error: {ex}");
                }
            }

            // Kill all active processes
            foreach (var proc in _tabProcessMap.Values)
            {
                try
                {
                    Debug.WriteLine("[ProcessPool] Dispose: Killing active process");
                    proc.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProcessPool] Dispose error: {ex}");
                }
            }

            _tabProcessMap.Clear();
        }
    }
}
