using System;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;

namespace CefSharpBrowser.WinForms
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            var settings = new CefSettings();
            settings.MultiThreadedMessageLoop = true;
            Cef.Initialize(settings);

            Application.Run(new Form1());
        }
    }
}
