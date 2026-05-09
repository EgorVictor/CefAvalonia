using System;
using System.Globalization;
using System.Windows.Forms;

namespace CefSharpBrowser.WinForms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var ownerHwnd = IntPtr.Zero;
        var width = 800;
        var height = 600;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--owner-hwnd:"))
            {
                var hex = arg["--owner-hwnd:".Length..];
                ownerHwnd = new IntPtr(long.Parse(hex, NumberStyles.HexNumber));
            }
            else if (arg.StartsWith("--width:"))
                int.TryParse(arg["--width:".Length..], out width);
            else if (arg.StartsWith("--height:"))
                int.TryParse(arg["--height:".Length..], out height);
        }

        Application.Run(new Form1(ownerHwnd, width, height));
    }
}
