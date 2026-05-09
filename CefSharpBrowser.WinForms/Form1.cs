using CefSharp;
using CefSharp.WinForms;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

public class Form1 : Form
{
    private readonly ChromiumWebBrowser browser;
    private readonly IntPtr parentHwnd;
    private int targetW = 800;
    private int targetH = 600;

    public Form1(IntPtr parentHwnd, int width, int height)
    {
        this.parentHwnd = parentHwnd;
        targetW = width > 0 ? width : 800;
        targetH = height > 0 ? height : 600;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Width = targetW;
        Height = targetH;

        browser = new ChromiumWebBrowser("about:blank")
        {
            Dock = DockStyle.Fill
        };
        browser.AddressChanged += OnAddressChanged;
        Controls.Add(browser);

        Load += OnFormLoad;
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        SetParent(Handle, parentHwnd);
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, targetW, targetH, SWP_SHOWWINDOW | SWP_NOZORDER);
        browser.Load("https://www.bing.com");

        Task.Run(ReadCommands);
    }

    private async Task ReadCommands()
    {
        try
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.StartsWith("NAVIGATE "))
                {
                    var url = line["NAVIGATE ".Length..];
                    BeginInvoke(() => browser.Load(url));
                }
                else if (line.StartsWith("RESIZE "))
                {
                    var parts = line["RESIZE ".Length..].Split(' ');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    {
                        targetW = w;
                        targetH = h;
                        BeginInvoke(() => SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, SWP_SHOWWINDOW | SWP_NOZORDER));
                    }
                }
            }
        }
        catch { }
    }

    private void OnAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        try
        {
            Console.WriteLine($"ADDRESS|{e.Address}");
            Console.Out.Flush();
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Cef.IsInitialized)
        {
            var settings = new CefSettings { MultiThreadedMessageLoop = true };
            Cef.Initialize(settings);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Cef.IsInitialized)
            Cef.Shutdown();
        base.OnFormClosing(e);
    }

    private const int SWP_SHOWWINDOW = 0x0040;
    private const int SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
