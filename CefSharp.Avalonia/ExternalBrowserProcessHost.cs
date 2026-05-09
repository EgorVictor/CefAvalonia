using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;
using SWF = System.Windows.Forms;

namespace CefSharp.Avalonia;

public sealed class ExternalBrowserProcessHost : NativeControlHost
{
    private SWF.Panel? hostPanel;
    private IntPtr embeddedHwnd = IntPtr.Zero;

    public IntPtr ContainerHandle => hostPanel?.Handle ?? IntPtr.Zero;
    public bool IsEmbedded => embeddedHwnd != IntPtr.Zero;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        hostPanel = new SWF.Panel();
        hostPanel.CreateControl();

        var style = GetWindowLong(hostPanel.Handle, GWL_STYLE);
        style |= WS_CLIPCHILDREN;
        SetWindowLong(hostPanel.Handle, GWL_STYLE, style);

        return new PlatformHandle(hostPanel.Handle, "HWND");
    }

    public void EmbedWindow(IntPtr childHwnd)
    {
        if (hostPanel == null || hostPanel.IsDisposed) return;
        embeddedHwnd = childHwnd;

        SetParent(childHwnd, hostPanel.Handle);

        var style = GetWindowLong(childHwnd, GWL_STYLE);
        style |= WS_CHILD;
        style |= WS_VISIBLE;
        SetWindowLong(childHwnd, GWL_STYLE, style);

        MoveWindow(childHwnd, 0, 0, hostPanel.Width, hostPanel.Height, true);
    }

    public void ResizeEmbedded()
    {
        if (hostPanel == null || embeddedHwnd == IntPtr.Zero) return;
        MoveWindow(embeddedHwnd, 0, 0, hostPanel.Width, hostPanel.Height, true);
    }

    public void Unembed()
    {
        embeddedHwnd = IntPtr.Zero;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Unembed();
        if (hostPanel != null && !hostPanel.IsDisposed)
        {
            hostPanel.Dispose();
            hostPanel = null;
        }
        base.DestroyNativeControlCore(control);
    }

    private const int GWL_STYLE = -16;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SetWindowLong(IntPtr hWnd, int nIndex, uint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
}
