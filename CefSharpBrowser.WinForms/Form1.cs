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
    private IntPtr ownerHwnd;
    private int targetW = 800;
    private int targetH = 600;

    public Form1(IntPtr ownerHwnd, int width, int height)
    {
        this.ownerHwnd = ownerHwnd;
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
        SetWindowLongPtr(Handle, GWLP_HWNDPARENT, ownerHwnd);
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
                if (line.StartsWith("POSITION "))
                {
                    var parts = line["POSITION ".Length..].Split(' ');
                    if (parts.Length == 4 &&
                        int.TryParse(parts[0], out var x) &&
                        int.TryParse(parts[1], out var y) &&
                        int.TryParse(parts[2], out var w) &&
                        int.TryParse(parts[3], out var h))
                    {
                        targetW = w; targetH = h;
                        BeginInvoke(() => SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, SWP_SHOWWINDOW | SWP_NOZORDER));
                    }
                }
                else if (line.StartsWith("NAVIGATE "))
                {
                    var url = line["NAVIGATE ".Length..];
                    BeginInvoke(() => browser.Load(url));
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

    private const int GWLP_HWNDPARENT = -8;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
