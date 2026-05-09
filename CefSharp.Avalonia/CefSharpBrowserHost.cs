using Avalonia.Controls;
using Avalonia.Platform;
using CefSharp;
using CefSharp.WinForms;
using System;
using SWF = System.Windows.Forms;

namespace CefSharp.Avalonia;

public sealed class CefSharpBrowserHost : NativeControlHost
{
    private SWF.Panel? hostPanel;
    private ChromiumWebBrowser? browser;
    private bool disposed;

    public string? CurrentAddress { get; private set; }
    public bool IsBrowserCreated => browser?.IsBrowserInitialized ?? false;
    public bool IsLoading { get; private set; }

    public event EventHandler<string>? AddressChanged;
    public event EventHandler<LoadErrorEventArgs>? LoadError;
    public event EventHandler<LoadingStateChangedEventArgs>? LoadingStateChanged;

    public void Navigate(string url)
    {
        if (disposed || browser == null || browser.IsDisposed)
            return;

        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
            url = "https://" + url;

        browser.Load(url);
    }

    public void Reload()
    {
        if (disposed || browser == null || browser.IsDisposed)
            return;
        browser.Reload();
    }

    public void Stop()
    {
        if (disposed || browser == null || browser.IsDisposed)
            return;
        browser.Stop();
    }

    public void DisposeBrowser()
    {
        if (browser == null || browser.IsDisposed)
            return;

        UnbindBrowserEvents();
        browser.Dispose();
        browser = null;
    }

    public void RecreateBrowser()
    {
        if (disposed)
            return;

        if (hostPanel != null && !hostPanel.IsDisposed && hostPanel.InvokeRequired)
        {
            hostPanel.BeginInvoke(RecreateBrowserCore);
            return;
        }

        RecreateBrowserCore();
    }

    private void RecreateBrowserCore()
    {
        var currentUrl = browser?.Address ?? CurrentAddress ?? "about:blank";

        if (browser != null && !browser.IsDisposed)
            browser.Load("about:blank");

        DisposeBrowser();
        hostPanel?.Controls.Clear();

        browser = new ChromiumWebBrowser(currentUrl)
        {
            Dock = SWF.DockStyle.Fill
        };

        BindBrowserEvents();
        hostPanel?.Controls.Add(browser);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        hostPanel = new SWF.Panel();
        browser = new ChromiumWebBrowser("about:blank")
        {
            Dock = SWF.DockStyle.Fill
        };

        BindBrowserEvents();

        hostPanel.Controls.Add(browser);
        hostPanel.CreateControl();

        return new PlatformHandle(hostPanel.Handle, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Cleanup();
        base.DestroyNativeControlCore(control);
    }

    private void BindBrowserEvents()
    {
        if (browser == null) return;
        browser.AddressChanged += OnBrowserAddressChanged;
        browser.LoadError += OnBrowserLoadError;
        browser.IsBrowserInitializedChanged += OnBrowserInitialized;
        browser.LoadingStateChanged += OnLoadingStateChanged;
    }

    private void UnbindBrowserEvents()
    {
        if (browser == null) return;
        browser.AddressChanged -= OnBrowserAddressChanged;
        browser.LoadError -= OnBrowserLoadError;
        browser.IsBrowserInitializedChanged -= OnBrowserInitialized;
        browser.LoadingStateChanged -= OnLoadingStateChanged;
    }

    private void Cleanup()
    {
        UnbindBrowserEvents();

        if (browser != null && !browser.IsDisposed)
        {
            browser.Dispose();
            browser = null;
        }

        if (hostPanel != null && !hostPanel.IsDisposed)
        {
            hostPanel.Dispose();
            hostPanel = null;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cleanup();
    }

    private void OnBrowserInitialized(object? sender, EventArgs e)
    {
        if (browser != null && browser.IsBrowserInitialized)
            Navigate("https://www.bing.com");
    }

    private void OnBrowserAddressChanged(object? sender, AddressChangedEventArgs e)
    {
        CurrentAddress = e.Address;
        AddressChanged?.Invoke(this, e.Address);
    }

    private void OnBrowserLoadError(object? sender, LoadErrorEventArgs e)
    {
        LoadError?.Invoke(this, e);
    }

    private void OnLoadingStateChanged(object? sender, LoadingStateChangedEventArgs e)
    {
        IsLoading = e.IsLoading;
        LoadingStateChanged?.Invoke(this, e);
    }
}
