using System;
using System.Collections.Generic;

namespace CefSharp.Avalonia;

/// <summary>
/// Represents a single browser tab with its associated WebView, process, and state.
/// </summary>
public class TabItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "New Tab";
    public string Url { get; set; } = "about:blank";
    public bool IsActive { get; set; }
    public bool IsFrozen { get; set; }

    /// <summary>Navigation history stack for back/forward navigation.</summary>
    public Stack<string> BackHistory { get; } = new();
    public Stack<string> ForwardHistory { get; } = new();

    /// <summary>Last recorded browser process manager for this tab.</summary>
    public BrowserProcessManager? BrowserProcess { get; set; }

    public TabItem(string? initialUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(initialUrl))
            Url = initialUrl;
    }

    /// <summary>Add URL to back history when navigating forward.</summary>
    public void PushBackHistory(string url)
    {
        BackHistory.Push(url);
        ForwardHistory.Clear();
    }

    /// <summary>Get URL from back history.</summary>
    public string? PopBackHistory()
    {
        if (BackHistory.Count > 0)
            return BackHistory.Pop();
        return null;
    }

    /// <summary>Get URL from forward history.</summary>
    public string? PopForwardHistory()
    {
        if (ForwardHistory.Count > 0)
            return ForwardHistory.Pop();
        return null;
    }

    public override string ToString() => $"Tab: {Title} ({Id:N})";
}
