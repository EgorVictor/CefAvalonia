using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public sealed class BrowserProcessManager : IDisposable
{
    private readonly string pipeName;
    private readonly string exePath;
    private Process? browserProcess;
    private NamedPipeClientStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Timer? watchdogTimer;
    private string? lastUrl;
    private bool disposed;
    private DateTime highMemorySince;
    private DateTime criticalMemorySince;

    public event Action<string>? AddressChanged;
    public event Action<string>? LoadError;
    public event Action? BrowserCrashed;
    public event Action<IntPtr>? WindowHandleReceived;

    public BrowserProcessManager()
    {
        pipeName = $"CefAvalonia_{Environment.ProcessId}";
        exePath = ResolveExePath();
    }

    private static string ResolveExePath()
    {
        var name = "CefSharpBrowser.WinForms.exe";

        var local = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(local)) return local;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
            var test = Path.Combine(dir, "CefSharpBrowser.WinForms",
                "bin", "Debug", "net8.0-windows", name);
            if (File.Exists(test)) return test;
        }

        return Path.Combine(AppContext.BaseDirectory, name);
    }

    public async Task StartAsync(string url)
    {
        lastUrl = url;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--pipe={pipeName} --url=\"{url}\" --host-pid={Environment.ProcessId}",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        browserProcess = Process.Start(psi);
        if (browserProcess == null)
            throw new InvalidOperationException("Failed to start browser process");

        await ConnectPipeAsync();

        watchdogTimer = new Timer(WatchdogTick, null, 5000, 5000);
    }

    private async Task ConnectPipeAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            try
            {
                pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1000);
                reader = new StreamReader(pipe);
                writer = new StreamWriter(pipe) { AutoFlush = true };
                _ = ReadPipeLoopAsync();
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
        throw new TimeoutException("Failed to connect to browser process via named pipe");
    }

    private async Task ReadPipeLoopAsync()
    {
        try
        {
            while (!disposed && reader != null)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                DispatchPipeMessage(line);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        if (!disposed)
            BrowserCrashed?.Invoke();
    }

    private void DispatchPipeMessage(string line)
    {
        var sep = line.IndexOf('|');
        var cmd = sep >= 0 ? line[..sep] : line;
        var arg = sep >= 0 ? line[(sep + 1)..] : "";

        switch (cmd)
        {
            case "Ready":
                if (!string.IsNullOrEmpty(arg) &&
                    long.TryParse(arg, System.Globalization.NumberStyles.HexNumber, null, out var hwnd))
                {
                    WindowHandleReceived?.Invoke(new IntPtr(hwnd));
                }
                break;
            case "AddressChanged":
                lastUrl = arg;
                AddressChanged?.Invoke(arg);
                break;
            case "LoadError":
                LoadError?.Invoke(arg);
                break;
        }
    }

    public async Task NavigateAsync(string url)
    {
        lastUrl = url;
        await SendAsync("Navigate", url);
    }

    public async Task ReloadAsync() => await SendAsync("Reload", "");
    public async Task StopAsync() => await SendAsync("Stop", "");

    private async Task SendAsync(string cmd, string arg)
    {
        if (writer == null) return;
        try
        {
            await writer.WriteLineAsync($"{cmd}|{arg}");
        }
        catch { }
    }

    private void WatchdogTick(object? state)
    {
        if (disposed || browserProcess == null) return;

        long totalMemory = 0;
        int subCount = 0;

        try
        {
            if (!browserProcess.HasExited)
                totalMemory += browserProcess.PrivateMemorySize64;
        }
        catch { }

        try
        {
            foreach (var p in Process.GetProcessesByName("CefSharp.BrowserSubprocess"))
            {
                try { totalMemory += p.PrivateMemorySize64; subCount++; }
                catch { }
                p.Dispose();
            }
        }
        catch { }

        var now = DateTime.UtcNow;

        if (totalMemory > 1_000_000_000L)
        {
            if (criticalMemorySince == default)
                criticalMemorySince = now;

            if ((now - criticalMemorySince).TotalSeconds >= 60)
            {
                LogMemory(totalMemory, subCount, "CRITICAL: restarting browser process");
                _ = RestartAsync();
                criticalMemorySince = default;
                highMemorySince = default;
            }
            return;
        }
        criticalMemorySince = default;

        if (totalMemory > 700_000_000L)
        {
            if (highMemorySince == default)
                highMemorySince = now;

            if ((now - highMemorySince).TotalSeconds >= 30)
                LogMemory(totalMemory, subCount, "WARNING");
            return;
        }
        highMemorySince = default;
    }

    private void LogMemory(long totalBytes, int subCount, string level)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CefSharp.Avalonia", "logs", "memory.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath,
            $"[{DateTime.UtcNow:HH:mm:ss}] {level}: {totalBytes / 1024 / 1024}MB, {subCount} subprocess(es){Environment.NewLine}");
    }

    public async Task RestartAsync()
    {
        await SendAsync("Close", "");
        await Task.Delay(1500);
        Kill();
        await StartAsync(lastUrl ?? "https://www.bing.com");
    }

    private void Kill()
    {
        if (browserProcess != null)
        {
            try { if (!browserProcess.HasExited) browserProcess.Kill(); }
            catch { }
            browserProcess.Dispose();
            browserProcess = null;
        }

        if (pipe != null)
        {
            pipe.Dispose();
            pipe = null;
            reader = null;
            writer = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watchdogTimer?.Dispose();
        Kill();
    }
}
