using CefSharp;
using CefSharp.WinForms;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CefSharp.Avalonia;

public sealed class BrowserIsland : IDisposable
{
    private IntPtr containerHwnd = IntPtr.Zero;
    private IntPtr parentHwnd = IntPtr.Zero;
    private Panel? panel;
    private ChromiumWebBrowser? browser;
    private int currentW = -1;
    private int currentH = -1;
    private int lastX, lastY, lastW, lastH;
    private string lastUrl = "about:blank";
    private bool disposed;
    private bool created;

    public event EventHandler<string>? AddressChanged;
    public event EventHandler<LoadErrorEventArgs>? LoadError;

    public string CurrentUrl => lastUrl;

    public void Create(IntPtr parentHwnd)
    {
        if (created) return;
        this.parentHwnd = parentHwnd;

        containerHwnd = CreateWindowEx(0, "Static", "", WS_CHILD,
            0, 0, 0, 0, parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        panel = new Panel();
        browser = new ChromiumWebBrowser("about:blank");
        browser.AddressChanged += OnAddressChanged;
        browser.LoadError += OnLoadError;
        panel.Controls.Add(browser);
        panel.CreateControl();
        SetParent(panel.Handle, containerHwnd);
        created = true;
    }

    public void Resize(int x, int y, int w, int h)
    {
        lastX = x; lastY = y; lastW = w; lastH = h;
        if (!created || disposed) return;
        if (w == currentW && h == currentH) return;
        if (w <= 0 || h <= 0) return;
        currentW = w; currentH = h;

        SetWindowPos(containerHwnd, IntPtr.Zero, x, y, w, h, SWP_SHOWWINDOW | SWP_NOZORDER);
        if (panel?.IsHandleCreated == true)
            SetWindowPos(panel.Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER);
        if (browser?.IsHandleCreated == true)
            SetWindowPos(browser.Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOZORDER);
    }

    public void Navigate(string url)
    {
        if (string.IsNullOrEmpty(url)) url = "about:blank";
        lastUrl = url;
        browser?.Load(url);
    }

    public void Recreate()
    {
        if (disposed) return;

        var savedPx = lastX; var savedPy = lastY;
        var savedPw = lastW; var savedPh = lastH;
        var savedParent = parentHwnd;
        var savedUrl = lastUrl;

        created = false;
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
        currentW = -1; currentH = -1;

        Create(savedParent);
        Navigate(savedUrl);

        if (savedPw > 0 && savedPh > 0)
            Resize(savedPx, savedPy, savedPw, savedPh);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        created = false;
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
        currentW = -1; currentH = -1;
    }

    private void OnAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        lastUrl = e.Address;
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
