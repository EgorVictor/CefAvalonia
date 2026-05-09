using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Timers;

namespace CefSharp.Avalonia;

public sealed class CefMemoryGuard : IDisposable
{
    private readonly CefSharpBrowserHost browserHost;
    private readonly string memoryLogPath;
    private readonly Timer timer;
    private DateTime highMemorySince;
    private DateTime criticalMemorySince;
    private bool disposed;
    private long lastLoggedMB = -1;

    public CefMemoryGuard(CefSharpBrowserHost browserHost)
    {
        this.browserHost = browserHost;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharp.Avalonia", "logs");

        Directory.CreateDirectory(logDir);
        memoryLogPath = Path.Combine(logDir, "memory.log");

        timer = new Timer(5000);
        timer.Elapsed += OnTimer;
        timer.AutoReset = true;
        timer.Start();
    }

    private void OnTimer(object? sender, ElapsedEventArgs e)
    {
        if (disposed) return;

        var (totalMemory, subprocessCount) = CollectProcessMemory();

        var now = DateTime.UtcNow;
        long totalMB = totalMemory / 1024 / 1024;

        if (totalMemory > 1_000_000_000L)
        {
            if (criticalMemorySince == default)
            {
                criticalMemorySince = now;
                Log($"[{now:HH:mm:ss}] CRITICAL START: {totalMB}MB across {subprocessCount} subprocess(es)");
            }

            var elapsed = now - criticalMemorySince;
            if (elapsed.TotalSeconds >= 60)
            {
                Log($"[{now:HH:mm:ss}] CRITICAL ACTION: {totalMB}MB sustained for {elapsed.TotalSeconds:F0}s -> RecreateBrowserAsync()");
                _ = browserHost.RecreateBrowserAsync();
                highMemorySince = default;
                criticalMemorySince = default;
            }
            return;
        }

        criticalMemorySince = default;

        if (totalMemory > 700_000_000L)
        {
            if (highMemorySince == default)
            {
                highMemorySince = now;
                Log($"[{now:HH:mm:ss}] WARNING START: {totalMB}MB across {subprocessCount} subprocess(es)");
            }

            var elapsed = now - highMemorySince;
            if (elapsed.TotalSeconds >= 30 && Math.Abs(totalMB - lastLoggedMB) > 50)
            {
                Log($"[{now:HH:mm:ss}] WARNING: {totalMB}MB sustained for {elapsed.TotalSeconds:F0}s");
                lastLoggedMB = totalMB;
            }
            return;
        }

        highMemorySince = default;

        if (Math.Abs(totalMB - lastLoggedMB) > 100)
        {
            Log($"[{now:HH:mm:ss}] INFO: {totalMB}MB, {subprocessCount} subprocess(es)");
            lastLoggedMB = totalMB;
        }
    }

    private static (long totalMemory, int subprocessCount) CollectProcessMemory()
    {
        long total = 0;
        int count = 0;

        try
        {
            var current = Process.GetCurrentProcess();
            total += current.PrivateMemorySize64;
        }
        catch { }

        try
        {
            foreach (var p in GetBrowserSubprocesses())
            {
                try { total += p.PrivateMemorySize64; count++; }
                catch { }
                p.Dispose();
            }
        }
        catch { }

        return (total, count);
    }

    private static IEnumerable<Process> GetBrowserSubprocesses()
    {
        var currentPid = Environment.ProcessId;
        var currentName = Process.GetCurrentProcess().ProcessName;

        foreach (var p in Process.GetProcessesByName("CefSharp.BrowserSubprocess"))
        {
            if (p.Id != currentPid) yield return p;
        }

        foreach (var p in Process.GetProcessesByName(currentName))
        {
            if (p.Id == currentPid) continue;
            if (HasCefSubprocessArgs(p))
                yield return p;
        }
    }

    private static bool HasCefSubprocessArgs(Process process)
    {
        try
        {
            using var cmd = process.MainModule;
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--type=")) return true;
            }
        }
        catch { }
        return false;
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(memoryLogPath, message + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Dispose();
        Log($"[{DateTime.UtcNow:HH:mm:ss}] GUARD STOPPED");
    }
}
