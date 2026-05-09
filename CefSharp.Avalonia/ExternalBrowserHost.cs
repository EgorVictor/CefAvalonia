using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

public sealed class ExternalBrowserHost : IDisposable
{
    private IntPtr containerHwnd = IntPtr.Zero;
    private Process? process;
    private int lastW = -1, lastH = -1;
    private bool disposed;
    private bool restartPending;

    public event EventHandler<string>? AddressChanged;

    public void Create(IntPtr parentHwnd)
    {
        containerHwnd = CreateWindowEx(0, "Static", "", WS_CHILD | WS_VISIBLE,
            0, 0, 100, 100, parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        StartProcess();
    }

    private void StartProcess()
    {
        var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CefSharpBrowser.WinForms.exe");
        if (!File.Exists(exe))
            return;

        process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--parent-hwnd:{containerHwnd.ToInt64():X} --width:{lastW} --height:{lastH}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        process.Exited += OnProcessExited;
        process.Start();
        _ = Task.Run(ReadOutput);
    }

    private async Task ReadOutput()
    {
        try
        {
            using var reader = process?.StandardOutput;
            if (reader == null) return;
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("ADDRESS|"))
                {
                    var url = line.Substring(8);
                    global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        AddressChanged?.Invoke(this, url));
                }
            }
        }
        catch { }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (disposed || restartPending) return;
        restartPending = true;
        Task.Delay(2000).ContinueWith(_ =>
        {
            if (!disposed)
            {
                StartProcess();
                restartPending = false;
            }
        });
    }

    public void Resize(int x, int y, int w, int h)
    {
        if (containerHwnd == IntPtr.Zero || disposed) return;
        if (w <= 0 || h <= 0) return;
        if (w == lastW && h == lastH) return;
        lastW = w; lastH = h;

        SetWindowPos(containerHwnd, IntPtr.Zero, x, y, w, h, SWP_SHOWWINDOW | SWP_NOZORDER);

        if (process?.HasExited == false)
            SendCommand($"RESIZE {w} {h}");
    }

    public void Navigate(string url)
    {
        if (string.IsNullOrEmpty(url)) url = "about:blank";
        SendCommand($"NAVIGATE {url}");
    }

    private void SendCommand(string cmd)
    {
        try
        {
            if (process?.HasExited == false)
            {
                process.StandardInput.WriteLine(cmd);
                process.StandardInput.Flush();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (process != null)
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
                process.WaitForExit(3000);
            }
            process.Dispose();
            process = null;
        }

        if (containerHwnd != IntPtr.Zero)
        {
            DestroyWindow(containerHwnd);
            containerHwnd = IntPtr.Zero;
        }
    }

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
