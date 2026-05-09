using CefSharp;
using CefSharp.WinForms;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CefSharp.Avalonia;

public sealed class BrowserIsland : IDisposable
{
    private IntPtr containerHwnd = IntPtr.Zero;
    private Panel? panel;
    private ChromiumWebBrowser? browser;
    private int currentW = -1;
    private int currentH = -1;
    private bool disposed;

    public event EventHandler<string>? AddressChanged;
    public event EventHandler<LoadErrorEventArgs>? LoadError;

    public void Create(IntPtr parentHwnd)
    {
        if (containerHwnd != IntPtr.Zero) return;

        containerHwnd = CreateWindowEx(0, "Static", "", WS_CHILD,
            0, 0, 0, 0, parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        panel = new Panel();

        browser = new ChromiumWebBrowser("about:blank");
        browser.AddressChanged += OnAddressChanged;
        browser.LoadError += OnLoadError;
        panel.Controls.Add(browser);

        panel.CreateControl();
        SetParent(panel.Handle, containerHwnd);
    }

    public void Resize(int x, int y, int w, int h)
    {
        if (containerHwnd == IntPtr.Zero || disposed) return;
        if (w == currentW && h == currentH) return;
        currentW = w;
        currentH = h;

        if (w <= 0 || h <= 0) return;

        SetWindowPos(containerHwnd, IntPtr.Zero, x, y, w, h, SWP_SHOWWINDOW | SWP_NOZORDER);

        if (panel != null && panel.IsHandleCreated)
            SetWindowPos(panel.Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER);

        if (browser != null && browser.IsHandleCreated)
            SetWindowPos(browser.Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER);
    }

    public void Navigate(string url)
    {
        if (browser != null && !disposed)
            browser.Load(url ?? "about:blank");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (browser != null)
        {
            browser.AddressChanged -= OnAddressChanged;
            browser.LoadError -= OnLoadError;
            panel?.Controls.Remove(browser);
            browser.Dispose();
            browser = null;
        }

        panel?.Dispose();
        panel = null;

        if (containerHwnd != IntPtr.Zero)
        {
            DestroyWindow(containerHwnd);
            containerHwnd = IntPtr.Zero;
        }

        currentW = -1;
        currentH = -1;
    }

    private void OnAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            AddressChanged?.Invoke(this, e.Address));
    }

    private void OnLoadError(object? sender, LoadErrorEventArgs e)
    {
        if (e.ErrorCode != CefErrorCode.Aborted)
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                LoadError?.Invoke(this, e));
    }

    private const int WS_CHILD = 0x40000000;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);
}
