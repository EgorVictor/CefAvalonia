using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CefSharp.Avalonia;

/// <summary>
/// HWND interop host: embeds a foreign window (CEF browser HWND) into an Avalonia panel via
/// SetParent with proper style flags (WS_CHILD) to avoid popup flash and coordinate issues.
/// Uses NativeControlHost to obtain a native HWND from the Avalonia layout engine.
/// </summary>
public sealed class ExternalBrowserProcessHost : NativeControlHost
{
    private IntPtr hostPanelHwnd = IntPtr.Zero;
    private IntPtr embeddedHwnd = IntPtr.Zero;
    private IntPtr _pendingEmbedHwnd = IntPtr.Zero;

    /// <summary>True once EmbedWindow has been called successfully.</summary>
    public bool IsEmbedded => embeddedHwnd != IntPtr.Zero;

    /// <summary>Current host panel HWND. Used to detect panel changes during tab switch.</summary>
    public IntPtr HostPanelHandle => hostPanelHwnd;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        hostPanelHwnd = parent.Handle;
        Console.Error.WriteLine($"DIAG: [EBPH] CreateNativeControlCore panel=0x{hostPanelHwnd.ToInt64():X} pending=0x{_pendingEmbedHwnd.ToInt64():X}");
        // Process any pending embed from tab switch
        if (_pendingEmbedHwnd != IntPtr.Zero)
        {
            var hwnd = _pendingEmbedHwnd;
            _pendingEmbedHwnd = IntPtr.Zero;
            Console.Error.WriteLine($"DIAG: [EBPH] Processing deferred embed of HWND=0x{hwnd.ToInt64():X}");
            _ = EmbedWindowAsync(hwnd);
        }
        return new PlatformHandle(hostPanelHwnd, "HWND");
    }

    public void EmbedWindow(IntPtr childHwnd)
    {
        if (hostPanelHwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"DIAG: [EBPH] EmbedWindow DEFERRED hwnd=0x{childHwnd.ToInt64():X}");
            _pendingEmbedHwnd = childHwnd;
            return;
        }
        _ = EmbedWindowAsync(childHwnd);
    }

    /// <summary>
    /// Async version of EmbedWindow with enhanced refresh sequence to eliminate black screen.
    /// Implements double-redraw with delays to ensure complete HWND embedding and visibility.
    /// </summary>
    private async Task EmbedWindowAsync(IntPtr childHwnd)
    {
        try
        {
            if (hostPanelHwnd == IntPtr.Zero) return;

            embeddedHwnd = childHwnd;
            Debug.WriteLine($"[EBPH] EmbedWindowAsync START: child=0x{childHwnd.ToInt64():X8}, panel=0x{hostPanelHwnd.ToInt64():X8}");

            // Step 1: Change style to WS_CHILD BEFORE reparenting (prevents popup flash)
            var style = GetWindowLong(childHwnd, GWL_STYLE);
            style &= ~WS_POPUP;
            style |= WS_CHILD | WS_VISIBLE;
            SetWindowLong(childHwnd, GWL_STYLE, style);
            Debug.WriteLine($"[EBPH] Step 1: SetWindowLong(WS_CHILD|WS_VISIBLE) done");

            // Step 2: Reparent into the panel
            var oldParent = SetParent(childHwnd, hostPanelHwnd);
            Debug.WriteLine($"[EBPH] Step 2: SetParent done, oldParent=0x{oldParent.ToInt64():X8}");

            // Step 3: Wait for OS to process SetParent (25ms buffer ensures kernel completes reparent)
            await Task.Delay(25);
            Debug.WriteLine($"[EBPH] Step 3: Delay 25ms after SetParent");

            // Step 4: Force position/size with DPI scaling
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var w = (int)(Bounds.Width * scaling);
            var h = (int)(Bounds.Height * scaling);
            if (w > 0 && h > 0)
            {
                MoveWindow(childHwnd, 0, 0, w, h, false);
                Debug.WriteLine($"[EBPH] Step 4: MoveWindow({w}x{h}) with scaling={scaling}");
            }

            // Step 5: Set Z-order and make visible
            SetWindowPos(childHwnd, HWND_TOP, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Debug.WriteLine($"[EBPH] Step 5: SetWindowPos(HWND_TOP) done");

            // Step 6: First RedrawWindow on parent container
            RedrawWindow(hostPanelHwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            Debug.WriteLine($"[EBPH] Step 6: RedrawWindow(parent) - first pass");

            // Step 7: Brief delay to ensure first redraw processes
            await Task.Delay(10);

            // Step 8: Second RedrawWindow directly on the child HWND (critical for eliminating black frame)
            RedrawWindow(childHwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_UPDATENOW);
            Debug.WriteLine($"[EBPH] Step 8: RedrawWindow(child) - second pass");

            Debug.WriteLine($"[EBPH] EmbedWindowAsync COMPLETE");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EBPH] EmbedWindowAsync ERROR: {ex}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Reparent CEF child to desktop BEFORE base destroys DumbWindow (which would cascade-destroy child windows)
        if (embeddedHwnd != IntPtr.Zero)
        {
            Console.Error.WriteLine($"DIAG: [EBPH] OnDetachedFromVisualTree reparenting CEF child 0x{embeddedHwnd.ToInt64():X} to desktop");
            SetParent(embeddedHwnd, IntPtr.Zero);
            ShowWindow(embeddedHwnd, SW_HIDE);
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _pendingEmbedHwnd = IntPtr.Zero;
        embeddedHwnd = IntPtr.Zero;
        hostPanelHwnd = IntPtr.Zero;  // Force defer on next attach until CreateNativeControlCore provides new panel
        base.DestroyNativeControlCore(control);
    }

    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ALLCHILDREN = 0x0080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private static readonly IntPtr HWND_TOP = new IntPtr(0);
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
