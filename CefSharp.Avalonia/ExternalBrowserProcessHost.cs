using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// Hosts a foreign native window inside Avalonia.
///
/// Windows: reparents the CEF HWND into the panel obtained from
/// <see cref="NativeControlHost"/> with <c>SetParent</c>.
/// Linux: creates an X11 child window and reparents the CEF XID into it with
/// <c>XReparentWindow</c>.
///
/// The class is deliberately defensive: the embedded window belongs to another
/// process and can disappear at any time (native host crash), so every Win32/X11
/// call is guarded by an <c>IsWindow</c> check.
/// </summary>
public sealed class ExternalBrowserProcessHost : NativeControlHost
{
    private static bool IsLinux => OperatingSystem.IsLinux();

    private IntPtr _hostPanelHwnd = IntPtr.Zero;
    private IntPtr _embeddedHwnd = IntPtr.Zero;
    private IntPtr _pendingEmbedHwnd = IntPtr.Zero;
    private IntPtr _xDisplay = IntPtr.Zero;
    private IntPtr _x11HostWindow = IntPtr.Zero;
    private bool _embedding;
    private bool _hostWindowCreated;

    public ExternalBrowserProcessHost()
    {
        // The embedded CEF window is a foreign window: Avalonia keeps keyboard
        // focus on its own window unless we forward it. Without this the page is
        // not typeable after clicking into it.
        GotFocus += (_, _) => ForwardFocus();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>True once a native window has been embedded.</summary>
    public bool IsEmbedded => _embeddedHwnd != IntPtr.Zero;

    /// <summary>Handle of the Avalonia host panel (HWND or XID).</summary>
    public IntPtr HostPanelHandle => _hostPanelHwnd;

    /// <summary>
    /// Handle of the window the native browser should be created into: the X11
    /// host window on Linux, the panel HWND on Windows. Zero until Avalonia has
    /// created the host panel (which happens during the first layout pass).
    /// </summary>
    public IntPtr HostWindowHandle => IsLinux
        ? (_x11HostWindow != IntPtr.Zero ? _x11HostWindow : _hostPanelHwnd)
        : _hostPanelHwnd;

    /// <summary>Raised once Avalonia has created the host window.</summary>
    public event Action? HostWindowCreated;

    /// <summary>
    /// Waits until Avalonia has created the host window and returns its handle,
    /// or <see cref="IntPtr.Zero"/> when it is still missing after
    /// <paramref name="timeout"/> (so callers can fall back to the old path
    /// instead of hanging).
    /// </summary>
    public async Task<IntPtr> WaitForHostWindowAsync(TimeSpan timeout)
    {
        if (_hostWindowCreated) return HostWindowHandle;

        var tcs = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCreated() => tcs.TrySetResult(HostWindowHandle);

        HostWindowCreated += OnCreated;
        try
        {
            Task delay = Task.Delay(timeout);
            Task done = await Task.WhenAny(tcs.Task, delay).ConfigureAwait(true);
            return done == tcs.Task ? await tcs.Task.ConfigureAwait(true) : IntPtr.Zero;
        }
        finally
        {
            HostWindowCreated -= OnCreated;
        }
    }

    /// <summary>Hands keyboard focus to the embedded browser window.</summary>
    public void ForwardFocus()
    {
        IntPtr child = _embeddedHwnd;
        if (child == IntPtr.Zero) return;

        if (IsLinux)
        {
            if (_xDisplay != IntPtr.Zero)
            {
                XSetInputFocus(_xDisplay, child, RevertToParent, CurrentTime);
                XFlush(_xDisplay);
            }
            return;
        }

        if (!IsWindow(child)) return;
        SetFocus(child);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _hostPanelHwnd = parent.Handle;
        CefDiagnostics.Debug(
            $"[BrowserHost] CreateNativeControl panel=0x{_hostPanelHwnd.ToInt64():X}");

        if (IsLinux)
        {
            EnsureXDisplay();
            if (_xDisplay != IntPtr.Zero)
            {
                (uint w, uint h) = GetScaledSize();
                _x11HostWindow = XCreateSimpleWindow(
                    _xDisplay, _hostPanelHwnd, 0, 0, w, h, 0, IntPtr.Zero, IntPtr.Zero);
                XMapWindow(_xDisplay, _x11HostWindow);
                XFlush(_xDisplay);
            }
            else
            {
                CefDiagnostics.Warn(
                    "[BrowserHost] XOpenDisplay failed; X11 embedding is unavailable " +
                    "(a Wayland-only session cannot host a CEF window)");
                _x11HostWindow = IntPtr.Zero;
            }
        }

        // A tab switch may have produced an embed request while the panel was
        // being recreated; run it now that we have a valid parent.
        if (_pendingEmbedHwnd != IntPtr.Zero)
        {
            IntPtr hwnd = _pendingEmbedHwnd;
            _pendingEmbedHwnd = IntPtr.Zero;
            EmbedWindow(hwnd);
        }

        IntPtr handle = IsLinux
            ? (_x11HostWindow != IntPtr.Zero ? _x11HostWindow : _hostPanelHwnd)
            : _hostPanelHwnd;

        _hostWindowCreated = true;
        HostWindowCreated?.Invoke();

        return new PlatformHandle(handle, IsLinux ? "XID" : "HWND");
    }

    /// <summary>Embeds (or re-embeds) a native browser window.</summary>
    public void EmbedWindow(IntPtr childHwnd)
    {
        if (childHwnd == IntPtr.Zero) return;

        if (_hostPanelHwnd == IntPtr.Zero || (IsLinux && _x11HostWindow == IntPtr.Zero))
        {
            CefDiagnostics.Debug(
                $"[BrowserHost] embed deferred (panel not ready) child=0x{childHwnd.ToInt64():X}");
            _pendingEmbedHwnd = childHwnd;
            return;
        }

        _ = EmbedWindowAsync(childHwnd);
    }

    private async Task EmbedWindowAsync(IntPtr childHwnd)
    {
        if (_embedding) return;
        _embedding = true;

        try
        {
            if (_hostPanelHwnd == IntPtr.Zero) return;

            (int w, int h) = GetPixelSize();

            if (IsLinux)
            {
                if (_xDisplay == IntPtr.Zero || _x11HostWindow == IntPtr.Zero)
                    return;

                // When the browser was created directly inside our host window it
                // is already a child. Reparenting it onto the same parent again
                // emits an unmap/map pair that can invalidate the native drawing
                // surface, so only move it when it really lives somewhere else
                // (tab-switch re-embed).
                if (GetXParent(_xDisplay, childHwnd) != _x11HostWindow)
                    XReparentWindow(_xDisplay, childHwnd, _x11HostWindow, 0, 0);

                if (w > 0 && h > 0)
                    XMoveResizeWindow(_xDisplay, childHwnd, 0, 0, (uint)w, (uint)h);
                XMapRaised(_xDisplay, childHwnd);
                XSync(_xDisplay, false);

                _embeddedHwnd = childHwnd;
                ForwardFocus();
                CefDiagnostics.Debug($"[BrowserHost] X11 embed complete {w}x{h}");
                return;
            }

            if (!IsWindow(childHwnd) || !IsWindow(_hostPanelHwnd))
            {
                CefDiagnostics.Warn("[BrowserHost] embed skipped: window handle is no longer valid");
                return;
            }

            // 1. Child style BEFORE reparenting (prevents a top-level flash).
            long style = GetWindowLongPtr(childHwnd, GWL_STYLE).ToInt64();
            style &= ~(long)(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
            style |= WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN;
            SetWindowLongPtr(childHwnd, GWL_STYLE, new IntPtr(style));

            // 2. Reparent into the Avalonia panel.
            SetParent(childHwnd, _hostPanelHwnd);

            // 3. Let the OS settle, then apply geometry and the new frame.
            await Task.Delay(25);

            if (!IsWindow(childHwnd) || !IsWindow(_hostPanelHwnd))
                return;

            if (w > 0 && h > 0)
                MoveWindow(childHwnd, 0, 0, w, h, false);

            SetWindowPos(childHwnd, HWND_TOP, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            ShowWindow(childHwnd, SW_SHOW);

            RedrawWindow(_hostPanelHwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            RedrawWindow(childHwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW);

            _embeddedHwnd = childHwnd;
            ForwardFocus();
            CefDiagnostics.Debug($"[BrowserHost] Windows embed complete {w}x{h}");
        }
        catch (Exception ex)
        {
            CefDiagnostics.Error("[BrowserHost] embed failed", ex);
        }
        finally
        {
            _embedding = false;
        }
    }

    private (int Width, int Height) GetPixelSize()
    {
        (uint w, uint h) = GetScaledSize();
        return ((int)w, (int)h);
    }

    private (uint Width, uint Height) GetScaledSize()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double width = Bounds.Width > 0 ? Bounds.Width : 1;
        double height = Bounds.Height > 0 ? Bounds.Height : 1;
        return ((uint)Math.Max(1, Math.Round(width * scaling)),
                (uint)Math.Max(1, Math.Round(height * scaling)));
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (IsLinux)
        {
            if (_xDisplay == IntPtr.Zero || _x11HostWindow == IntPtr.Zero) return;

            (uint w, uint h) = GetScaledSize();
            XMoveResizeWindow(_xDisplay, _x11HostWindow, 0, 0, w, h);
            if (_embeddedHwnd != IntPtr.Zero)
                XMoveResizeWindow(_xDisplay, _embeddedHwnd, 0, 0, w, h);
            XFlush(_xDisplay);
            return;
        }

        if (_embeddedHwnd == IntPtr.Zero || !IsWindow(_embeddedHwnd)) return;

        (int width, int height) = GetPixelSize();
        MoveWindow(_embeddedHwnd, 0, 0, width, height, true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Detach the CEF child BEFORE Avalonia destroys the host window, which
        // would otherwise cascade-destroy the child.
        DetachFromCurrentParent();
        base.OnDetachedFromVisualTree(e);
    }

    private void DetachFromCurrentParent()
    {
        IntPtr child = _embeddedHwnd;
        if (child == IntPtr.Zero) return;

        if (IsLinux)
        {
            if (_xDisplay != IntPtr.Zero)
            {
                XReparentWindow(_xDisplay, child, XDefaultRootWindow(_xDisplay), 0, 0);
                XUnmapWindow(_xDisplay, child);
                XFlush(_xDisplay);
            }
        }
        else if (IsWindow(child))
        {
            SetParent(child, IntPtr.Zero);
            ShowWindow(child, SW_HIDE);
        }

        _embeddedHwnd = IntPtr.Zero;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _pendingEmbedHwnd = IntPtr.Zero;
        _embeddedHwnd = IntPtr.Zero;
        _hostPanelHwnd = IntPtr.Zero;
        _hostWindowCreated = false;

        if (IsLinux && _xDisplay != IntPtr.Zero)
        {
            if (_x11HostWindow != IntPtr.Zero)
            {
                XDestroyWindow(_xDisplay, _x11HostWindow);
                XFlush(_xDisplay);
            }

            XCloseDisplay(_xDisplay);
            _xDisplay = IntPtr.Zero;
        }

        _x11HostWindow = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    /// <summary>
    /// Forgets the embedded window without touching it. Used during permanent
    /// teardown (WebView.Cleanup) when the native host is about to destroy the
    /// window; a later detach must never call SetParent/XReparent on it.
    /// </summary>
    public void DetachEmbedded()
    {
        _pendingEmbedHwnd = IntPtr.Zero;
        _embeddedHwnd = IntPtr.Zero;
        _hostPanelHwnd = IntPtr.Zero;
        _x11HostWindow = IntPtr.Zero;
    }

    private void EnsureXDisplay()
    {
        if (!IsLinux || _xDisplay != IntPtr.Zero) return;
        _xDisplay = XOpenDisplay(IntPtr.Zero);
    }

    // ---- Windows constants ----
    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    // GetWindowLongPtrW only exists on 64-bit Windows; on 32-bit the *W variant
    // is the correct entry point, so pick the right one at runtime.
    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height,
        [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect,
        IntPtr updateRegion, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    // ---- X11 P/Invoke ----
    private const string X11Lib = "libX11.so.6";
    private const int RevertToParent = 2;
    private const ulong CurrentTime = 0;

    [DllImport(X11Lib)]
    private static extern IntPtr XOpenDisplay(IntPtr display);

    [DllImport(X11Lib)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport(X11Lib)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport(X11Lib)]
    private static extern IntPtr XCreateSimpleWindow(
        IntPtr display, IntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, IntPtr border, IntPtr background);

    [DllImport(X11Lib)]
    private static extern int XDestroyWindow(IntPtr display, IntPtr window);

    [DllImport(X11Lib)]
    private static extern int XReparentWindow(IntPtr display, IntPtr window, IntPtr parent, int x, int y);

    [DllImport(X11Lib)]
    private static extern int XMoveResizeWindow(IntPtr display, IntPtr window, int x, int y,
        uint width, uint height);

    [DllImport(X11Lib)]
    private static extern int XMapWindow(IntPtr display, IntPtr window);

    [DllImport(X11Lib)]
    private static extern int XMapRaised(IntPtr display, IntPtr window);

    [DllImport(X11Lib)]
    private static extern int XUnmapWindow(IntPtr display, IntPtr window);

    [DllImport(X11Lib)]
    private static extern int XFlush(IntPtr display);

    [DllImport(X11Lib)]
    private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport(X11Lib)]
    private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr rootReturn,
        out IntPtr parentReturn, out IntPtr childrenReturn, out int nChildrenReturn);

    [DllImport(X11Lib)]
    private static extern int XFree(IntPtr data);

    /// <summary>Current parent XID of <paramref name="window"/>, or Zero on failure.</summary>
    private static IntPtr GetXParent(IntPtr display, IntPtr window)
    {
        if (display == IntPtr.Zero || window == IntPtr.Zero) return IntPtr.Zero;

        if (XQueryTree(display, window, out _, out IntPtr parent, out IntPtr children, out _) == 0)
            return IntPtr.Zero;

        if (children != IntPtr.Zero) XFree(children);
        return parent;
    }

    [DllImport(X11Lib)]
    private static extern int XSetInputFocus(IntPtr display, IntPtr focus, int revertTo, ulong time);
}
