using System;
using System.Diagnostics;
using System.IO;
using System.Timers;

namespace CefSharp.Avalonia;

public sealed class CefMemoryGuard : IDisposable
{
    private readonly CefSharpBrowserHost browserHost;
    private readonly string logPath;
    private readonly Timer timer;
    private DateTime highMemorySince;
    private DateTime criticalMemorySince;
    private bool disposed;

    public CefMemoryGuard(CefSharpBrowserHost browserHost)
    {
        this.browserHost = browserHost;

        logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharp.Avalonia", "logs", "memory.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        timer = new Timer(7000);
        timer.Elapsed += OnTimer;
        timer.AutoReset = true;
        timer.Start();
    }

    private void OnTimer(object? sender, ElapsedEventArgs e)
    {
        if (disposed) return;

        long totalMemory = 0;
        int subprocessCount = 0;

        try
        {
            var currentProcess = Process.GetCurrentProcess();
            totalMemory += currentProcess.PrivateMemorySize64;
        }
        catch { }

        try
        {
            var subprocesses = Process.GetProcessesByName("CefSharp.BrowserSubprocess");
            subprocessCount = subprocesses.Length;
            foreach (var p in subprocesses)
            {
                try { totalMemory += p.PrivateMemorySize64; }
                catch { }
                p.Dispose();
            }
        }
        catch { }

        var now = DateTime.UtcNow;
        long totalMB = totalMemory / 1024 / 1024;

        if (totalMemory > 1_000_000_000L)
        {
            if (criticalMemorySince == default)
                criticalMemorySince = now;

            var elapsed = now - criticalMemorySince;
            Log($"[{now:HH:mm:ss}] CRITICAL: {totalMB}MB across {subprocessCount} subprocess(es) for {elapsed.TotalSeconds:F0}s");

            if (elapsed.TotalSeconds >= 60)
            {
                Log($"[{now:HH:mm:ss}] ACTION: Triggering RecreateBrowser()");
                browserHost.RecreateBrowser();
                criticalMemorySince = default;
                highMemorySince = default;
                return;
            }
        }
        else
        {
            criticalMemorySince = default;
        }

        if (totalMemory > 700_000_000L)
        {
            if (highMemorySince == default)
                highMemorySince = now;

            var elapsed = now - highMemorySince;
            if (elapsed.TotalSeconds >= 30)
                Log($"[{now:HH:mm:ss}] WARNING: {totalMB}MB across {subprocessCount} subprocess(es) for {elapsed.TotalSeconds:F0}s");
        }
        else
        {
            highMemorySince = default;
        }

        Log($"[{now:HH:mm:ss}] INFO: {totalMB}MB, {subprocessCount} subprocess(es)");
    }

    public void FlushLog()
    {
        Log($"[{DateTime.UtcNow:HH:mm:ss}] GUARD STOPPED");
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(logPath, message + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Dispose();
        FlushLog();
    }
}
