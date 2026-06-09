using Avalonia.Controls;
using CefSharp.Avalonia;
using System;
using System.IO;

namespace TestBrowserApp;

/// <summary>
/// Multi-tab browser application using TabbedBrowserView.
/// Demonstrates ProcessPool + TabManager for efficient multi-tab browsing.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "testapp_debug.log");

    private static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try { File.AppendAllText(LogPath, line + "\n"); } catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        try { File.WriteAllText(LogPath, $"--- TestBrowserApp (v1.0.5 Multi-Tab) started ---\n"); } catch { }

        Log("MainWindow initialized - TabbedBrowserView ready for multi-tab browsing");
        Log("Features: ProcessPool caching, IPC freeze/resume, HWND refresh optimization");
    }
}

