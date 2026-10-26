using System;
using System.Diagnostics;

namespace CefSharp.Avalonia;

/// <summary>Verbosity of the library's diagnostic output.</summary>
public enum CefLogLevel
{
    /// <summary>No output at all.</summary>
    Off = 0,
    /// <summary>Only failures that affect the user.</summary>
    Error = 1,
    /// <summary>Errors plus recoverable problems.</summary>
    Warning = 2,
    /// <summary>Warnings plus lifecycle information.</summary>
    Info = 3,
    /// <summary>Everything, including per-message tracing.</summary>
    Debug = 4,
}

/// <summary>
/// Central logging for CefSharp.Avalonia.
///
/// By default the library stays silent except for <see cref="CefLogLevel.Error"/>.
/// Set <c>CEF_DIAG=1</c> in the environment (process start) or assign
/// <see cref="Level"/> / <see cref="Sink"/> to integrate with your own logger.
/// </summary>
public static class CefDiagnostics
{
    private static CefLogLevel _level = DetectInitialLevel();

    /// <summary>Current verbosity. Defaults to <see cref="CefLogLevel.Error"/>.</summary>
    public static CefLogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>
    /// Optional sink invoked for every accepted message. Runs on the calling
    /// thread (usually the UI thread or the host reader thread), so keep it cheap.
    /// </summary>
    public static Action<CefLogLevel, string>? Sink { get; set; }

    /// <summary>True when messages at <paramref name="level"/> would be emitted.</summary>
    public static bool IsEnabled(CefLogLevel level) => level <= _level;

    public static void Debug(string message) => Write(CefLogLevel.Debug, message);
    public static void Info(string message) => Write(CefLogLevel.Info, message);
    public static void Warn(string message) => Write(CefLogLevel.Warning, message);
    public static void Error(string message) => Write(CefLogLevel.Error, message);

    public static void Error(string message, Exception ex) =>
        Write(CefLogLevel.Error, message + ": " + ex);

    private static void Write(CefLogLevel level, string message)
    {
        if (!IsEnabled(level)) return;

        try
        {
            Sink?.Invoke(level, message);
        }
        catch
        {
            // A broken sink must never take the browser down.
        }

        System.Diagnostics.Debug.WriteLine($"[CefSharp.Avalonia/{level}] {message}");
    }

    private static CefLogLevel DetectInitialLevel()
    {
        string? diag = Environment.GetEnvironmentVariable("CEF_DIAG");
        if (diag == "1" || string.Equals(diag, "true", StringComparison.OrdinalIgnoreCase))
            return CefLogLevel.Debug;

        return CefLogLevel.Error;
    }
}
