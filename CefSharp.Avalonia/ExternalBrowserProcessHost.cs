using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

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

    /// <summary>True once EmbedWindow has been called successfully.</summary>
    public bool IsEmbedded => embeddedHwnd != IntPtr.Zero;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        hostPanelHwnd = parent.Handle;
        Debug.WriteLine($"[EBPH] CreateNativeControlCore parent=0x{hostPanelHwnd.ToInt64():X}");
        return new PlatformHandle(hostPanelHwnd, "HWND");
    }

    public void EmbedWindow(IntPtr childHwnd)
    {
        if (hostPanelHwnd == IntPtr.Zero)
        {
            Debug.WriteLine($"[EBPH] EmbedWindow SKIP: no host panel");
            return;
        }
        embeddedHwnd = childHwnd;
        Debug.WriteLine($"[EBPH] EmbedWindow child=0x{childHwnd.ToInt64():X8}, panel=0x{hostPanelHwnd.ToInt64():X8}");

        // Step 1: Change style to WS_CHILD BEFORE reparenting
        // This avoids the brief "popup" state where the window is a top-level window
        // that has been reparented, which causes coordinate offset / black screen.
        var style = GetWindowLong(childHwnd, GWL_STYLE);
        style &= ~WS_POPUP;
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLong(childHwnd, GWL_STYLE, style);
        Debug.WriteLine($"[EBPH] SetWindowLong(WS_CHILD|WS_VISIBLE) LastError={Marshal.GetLastWin32Error()}");

        // Step 2: Reparent into the panel
        var oldParent = SetParent(childHwnd, hostPanelHwnd);
        Debug.WriteLine($"[EBPH] SetParent result=0x{oldParent.ToInt64():X8}, LastError={Marshal.GetLastWin32Error()}");

        // Step 3: Force position/size to client-origin (0,0), no repaint needed
        MoveWindow(childHwnd, 0, 0, (int)Bounds.Width, (int)Bounds.Height, false);

        // Step 4: Ensure correct Z-order within parent, show window
        SetWindowPos(childHwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Step 5: Force immediate repaint of the parent area to eliminate black frames
        RedrawWindow(hostPanelHwnd, IntPtr.Zero, IntPtr.Zero,
            RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        embeddedHwnd = IntPtr.Zero;
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
}
