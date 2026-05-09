using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public sealed class MemoryGuard : IDisposable
{
    private readonly BrowserIsland _island;
    private readonly long _thresholdBytes;
    private readonly int _checkIntervalMs;
    private Timer? _timer;
    private int _recovering;

    public MemoryGuard(BrowserIsland island, int thresholdMB = 350, int checkIntervalMs = 5000)
    {
        _island = island;
        _thresholdBytes = thresholdMB * 1024L * 1024L;
        _checkIntervalMs = checkIntervalMs;
    }

    public void Start()
    {
        _timer = new Timer(_ => OnCheck(), null, _checkIntervalMs, _checkIntervalMs);
    }

    private async void OnCheck()
    {
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0)
            return;

        try
        {
            var proc = Process.GetCurrentProcess();
            if (proc.PrivateMemorySize64 <= _thresholdBytes)
                return;

            Trace.WriteLine($"[MemoryGuard] Memory={proc.PrivateMemorySize64 / 1024 / 1024}MB, threshold exceeded. Starting recovery...");

            await RecoverAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MemoryGuard] Recovery error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    private async Task RecoverAsync()
    {
        var url = await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => _island.CurrentUrl);

        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => _island.Navigate("about:blank"));

        await Task.Delay(3000);

        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => _island.Recreate());

        var restoredUrl = url;
        if (string.IsNullOrEmpty(restoredUrl) || restoredUrl == "about:blank")
            restoredUrl = "https://www.bing.com";

        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            () => _island.Navigate(restoredUrl));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
