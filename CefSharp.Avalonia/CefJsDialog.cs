using System;
using System.Threading;

namespace CefSharp.Avalonia;

/// <summary>Kind of JavaScript dialog requested by a page.</summary>
public enum CefJsDialogType
{
    /// <summary><c>alert()</c>; only an acknowledgement is possible.</summary>
    Alert,
    /// <summary><c>confirm()</c>; accept maps to <c>true</c>.</summary>
    Confirm,
    /// <summary><c>prompt()</c>; the accepted text is returned to the page.</summary>
    Prompt,
    /// <summary>beforeunload confirmation; accepting allows the unload to proceed.</summary>
    BeforeUnload,
}

/// <summary>
/// A JavaScript dialog raised by the page. Exactly one of <see cref="Accept"/>
/// or <see cref="Dismiss"/> must be called; later calls are ignored.
///
/// Dialogs arrive on the UI thread through <see cref="WebView.JsDialogRequested"/>.
/// If nobody handles the event the library answers with a safe default
/// (accept for <c>alert</c>, dismiss otherwise) so a page can never stay blocked.
/// </summary>
public sealed class CefJsDialog
{
    private readonly Action<int, bool, string?> _reply;
    private int _replied;

    internal CefJsDialog(int id, string typeName, string message, string defaultText,
        Action<int, bool, string?> reply)
    {
        Id = id;
        Type = ParseType(typeName);
        Message = message ?? "";
        DefaultText = defaultText ?? "";
        _reply = reply;
    }

    /// <summary>Native dialog identifier. Unique per browser host process.</summary>
    public int Id { get; }

    /// <summary>Dialog kind.</summary>
    public CefJsDialogType Type { get; }

    /// <summary>Text shown to the user.</summary>
    public string Message { get; }

    /// <summary>Pre-filled value for <see cref="CefJsDialogType.Prompt"/>.</summary>
    public string DefaultText { get; }

    /// <summary>True when the dialog has already been answered.</summary>
    public bool IsReplied => Volatile.Read(ref _replied) != 0;

    /// <summary>True for <see cref="CefJsDialogType.Alert"/>.</summary>
    public bool IsAlert => Type == CefJsDialogType.Alert;

    /// <summary>True when a text value is meaningful for this dialog.</summary>
    public bool HasTextInput => Type is CefJsDialogType.Prompt;

    /// <summary>
    /// Accepts the dialog. For prompts <paramref name="text"/> is returned to the
    /// page; when null the <see cref="DefaultText"/> is used.
    /// </summary>
    public void Accept(string? text = null)
    {
        if (Interlocked.Exchange(ref _replied, 1) != 0) return;
        _reply(Id, true, HasTextInput ? text ?? DefaultText : null);
    }

    /// <summary>Dismisses the dialog (cancel / false / null).</summary>
    public void Dismiss()
    {
        if (Interlocked.Exchange(ref _replied, 1) != 0) return;
        _reply(Id, false, null);
    }

    private static CefJsDialogType ParseType(string? typeName)
    {
        if (string.Equals(typeName, "alert", StringComparison.OrdinalIgnoreCase))
            return CefJsDialogType.Alert;
        if (string.Equals(typeName, "prompt", StringComparison.OrdinalIgnoreCase))
            return CefJsDialogType.Prompt;
        if (string.Equals(typeName, "beforeunload", StringComparison.OrdinalIgnoreCase))
            return CefJsDialogType.BeforeUnload;

        return CefJsDialogType.Confirm;
    }
}
