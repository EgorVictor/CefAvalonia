using System.Collections.Generic;

namespace CefSharp.Avalonia;

/// <summary>
/// Mirrors CEF's cef_log_severity_t enum.
/// Maps to --cef-log-severity command-line argument values.
/// </summary>
public enum CefLogSeverity
{
    Default,
    Verbose,
    Info,
    Warning,
    Error,
    Fatal,
    Disable
}

/// <summary>
/// Mirrors CEF's cef_settings_t struct (CEF 109.1.11).
/// Each property maps 1:1 to the corresponding CefSettings field.
/// Null values are skipped during serialization (CEF default applies).
/// </summary>
public class CefSettings
{
    // ---- Boolean flags ----
    /// <summary>Disable the sandbox for sub-processes. Corresponds to --no-sandbox.</summary>
    public bool? NoSandbox { get; set; }
    /// <summary>Enable Chrome runtime (experimental). CEF issue #2969.</summary>
    public bool? ChromeRuntime { get; set; }
    /// <summary>Run browser message loop on a separate thread (Windows/Linux only).</summary>
    public bool? MultiThreadedMessageLoop { get; set; }
    /// <summary>Control message pump via OnScheduleMessagePumpWork callback.</summary>
    public bool? ExternalMessagePump { get; set; }
    /// <summary>Enable off-screen rendering support.</summary>
    public bool? WindowlessRenderingEnabled { get; set; }
    /// <summary>Disable processing of standard Chromium command-line arguments.</summary>
    public bool? CommandLineArgsDisabled { get; set; }
    /// <summary>Persist session cookies when cache path is set.</summary>
    public bool? PersistSessionCookies { get; set; }
    /// <summary>Persist user preferences as JSON in cache directory.</summary>
    public bool? PersistUserPreferences { get; set; }
    /// <summary>Disable loading of .pak resource/locale pack files.</summary>
    public bool? PackLoadingDisabled { get; set; }
    /// <summary>Exclude default schemes (http, https, ws, wss) from cookieable schemes.</summary>
    public bool? CookieableSchemesExcludeDefaults { get; set; }

    // ---- String paths ----
    /// <summary>Path to sub-process executable (defaults to main executable).</summary>
    public string? BrowserSubprocessPath { get; set; }
    /// <summary>Path to CEF framework directory (macOS).</summary>
    public string? FrameworkDirPath { get; set; }
    /// <summary>Path to main bundle (macOS).</summary>
    public string? MainBundlePath { get; set; }
    /// <summary>Global browser cache directory. Empty = incognito mode.</summary>
    public string? CachePath { get; set; }
    /// <summary>Root directory for all cache_path values. Prevents multi-instance conflicts.</summary>
    public string? RootCachePath { get; set; }
    /// <summary>User data directory (Widevine CDM, spell check, etc.).</summary>
    public string? UserDataPath { get; set; }
    /// <summary>Fully qualified path for resources directory (.pak files).</summary>
    public string? ResourcesDirPath { get; set; }
    /// <summary>Fully qualified path for locales directory.</summary>
    public string? LocalesDirPath { get; set; }
    /// <summary>Debug log file path. Default: debug.log next to executable.</summary>
    public string? LogFile { get; set; }

    // ---- String other ----
    /// <summary>Custom User-Agent HTTP header value.</summary>
    public string? UserAgent { get; set; }
    /// <summary>Product portion of default User-Agent. Ignored if UserAgent is set.</summary>
    public string? UserAgentProduct { get; set; }
    /// <summary>Locale passed to WebKit. Default: en-US.</summary>
    public string? Locale { get; set; }
    /// <summary>Custom V8 JavaScript engine flags.</summary>
    public string? JavascriptFlags { get; set; }
    /// <summary>Comma-delimited Accept-Language header languages (no whitespace).</summary>
    public string? AcceptLanguageList { get; set; }
    /// <summary>Comma-delimited list of cookie-supported schemes.</summary>
    public string? CookieableSchemesList { get; set; }

    // ---- Numeric ----
    /// <summary>Log severity level. Maps to CEF's log_severity_t enum.</summary>
    public CefLogSeverity? LogSeverity { get; set; }
    /// <summary>Remote debugging port (1024-65535). Access via chrome://inspect.</summary>
    public int? RemoteDebuggingPort { get; set; }
    /// <summary>Stack frames for uncaught exceptions (0 = disabled).</summary>
    public int? UncaughtExceptionStackSize { get; set; }
    /// <summary>ARGB background color (0xAARRGGBB). Opaque or fully transparent.</summary>
    public uint? BackgroundColor { get; set; }

    /// <summary>
    /// Serializes all set properties to --cef-{name}={value} arguments.
    /// These are passed to CefBrowser.Native.exe and parsed in ApplyCefSettingsFromArgs().
    /// Null properties are omitted so CEF's defaults apply.
    /// </summary>
    public string ToCommandLineArgs()
    {
        var parts = new List<string>();

        AddBool(parts, "no-sandbox", NoSandbox);
        AddStr(parts, "browser-subprocess-path", BrowserSubprocessPath);
        AddStr(parts, "framework-dir-path", FrameworkDirPath);
        AddStr(parts, "main-bundle-path", MainBundlePath);
        AddBool(parts, "chrome-runtime", ChromeRuntime);
        AddBool(parts, "multi-threaded-message-loop", MultiThreadedMessageLoop);
        AddBool(parts, "external-message-pump", ExternalMessagePump);
        AddBool(parts, "windowless-rendering-enabled", WindowlessRenderingEnabled);
        AddBool(parts, "command-line-args-disabled", CommandLineArgsDisabled);
        AddStr(parts, "cache-path", CachePath);
        AddStr(parts, "root-cache-path", RootCachePath);
        AddStr(parts, "user-data-path", UserDataPath);
        AddBool(parts, "persist-session-cookies", PersistSessionCookies);
        AddBool(parts, "persist-user-preferences", PersistUserPreferences);
        AddStr(parts, "user-agent", UserAgent);
        AddStr(parts, "user-agent-product", UserAgentProduct);
        AddStr(parts, "locale", Locale);
        AddStr(parts, "log-file", LogFile);
        AddEnum(parts, "log-severity", LogSeverity);
        AddStr(parts, "javascript-flags", JavascriptFlags);
        AddStr(parts, "resources-dir-path", ResourcesDirPath);
        AddStr(parts, "locales-dir-path", LocalesDirPath);
        AddBool(parts, "pack-loading-disabled", PackLoadingDisabled);
        AddNum(parts, "remote-debugging-port", RemoteDebuggingPort);
        AddNum(parts, "uncaught-exception-stack-size", UncaughtExceptionStackSize);
        AddHex(parts, "background-color", BackgroundColor);
        AddStr(parts, "accept-language-list", AcceptLanguageList);
        AddStr(parts, "cookieable-schemes-list", CookieableSchemesList);
        AddBool(parts, "cookieable-schemes-exclude-defaults", CookieableSchemesExcludeDefaults);

        return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
    }

    private static void AddBool(List<string> parts, string name, bool? value)
    {
        if (value.HasValue)
            parts.Add($"--cef-{name}={(value.Value ? "true" : "false")}");
    }

    private static void AddStr(List<string> parts, string name, string? value)
    {
        if (value != null)
            parts.Add($"--cef-{name}={value}");
    }

    private static void AddNum(List<string> parts, string name, int? value)
    {
        if (value.HasValue)
            parts.Add($"--cef-{name}={value.Value}");
    }

    private static void AddHex(List<string> parts, string name, uint? value)
    {
        if (value.HasValue)
            parts.Add($"--cef-{name}={value.Value:X8}");
    }

    private static void AddEnum(List<string> parts, string name, CefLogSeverity? value)
    {
        if (value.HasValue)
        {
            var str = value.Value switch
            {
                CefLogSeverity.Default => "default",
                CefLogSeverity.Verbose => "verbose",
                CefLogSeverity.Info => "info",
                CefLogSeverity.Warning => "warning",
                CefLogSeverity.Error => "error",
                CefLogSeverity.Fatal => "fatal",
                CefLogSeverity.Disable => "disable",
                _ => null
            };
            if (str != null)
                parts.Add($"--cef-{name}={str}");
        }
    }
}
