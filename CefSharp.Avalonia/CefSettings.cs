using System;
using System.Collections.Generic;
using System.Text;

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
///
/// These are <b>process-level</b> settings: because all <see cref="WebView"/>
/// instances share one native host process, only the first browser that starts
/// the host decides them. Per-browser settings (cache isolation, URL) are
/// handled separately by <see cref="BrowserProcessManager"/>.
/// </summary>
public class CefSettings
{
    // ---- Boolean flags ----
    /// <summary>
    /// Disable the CEF sandbox. Defaults to <c>false</c> (sandbox enabled).
    /// Only disable it when you must run on an environment where the sandbox is
    /// unavailable (for example inside some containers or debuggers).
    /// </summary>
    public bool? NoSandbox { get; set; }
    /// <summary>Enable Chrome runtime (experimental). CEF issue #2969.</summary>
    public bool? ChromeRuntime { get; set; }
    /// <summary>Run browser message loop on a separate thread (Windows/Linux only).</summary>
    public bool? MultiThreadedMessageLoop { get; set; }
    /// <summary>Use an external message pump instead of an internal one.</summary>
    public bool? ExternalMessagePump { get; set; }
    /// <summary>Enable windowless (off-screen) rendering.</summary>
    public bool? WindowlessRenderingEnabled { get; set; }
    /// <summary>Disable configuration of the command line via CefApp.</summary>
    public bool? CommandLineArgsDisabled { get; set; }
    /// <summary>Persist session cookies between restarts.</summary>
    public bool? PersistSessionCookies { get; set; }
    /// <summary>Persist user preferences between restarts.</summary>
    public bool? PersistUserPreferences { get; set; }
    /// <summary>Disable pack file loading.</summary>
    public bool? PackLoadingDisabled { get; set; }
    /// <summary>Exclude the default cookieable schemes from the list.</summary>
    public bool? CookieableSchemesExcludeDefaults { get; set; }

    // ---- Paths ----
    /// <summary>Path to the CEF sub-process executable. Empty uses this executable.</summary>
    public string? BrowserSubprocessPath { get; set; }
    /// <summary>Path to the framework directory (macOS).</summary>
    public string? FrameworkDirPath { get; set; }
    /// <summary>Path to the main bundle (macOS).</summary>
    public string? MainBundlePath { get; set; }
    /// <summary>Directory used for the browser cache (profile).</summary>
    public string? CachePath { get; set; }
    /// <summary>Root directory used for the cache when <see cref="CachePath"/> is empty.</summary>
    public string? RootCachePath { get; set; }
    /// <summary>Directory used for user data (cookies, local storage, ...).</summary>
    public string? UserDataPath { get; set; }
    /// <summary>Path of the CEF log file.</summary>
    public string? LogFile { get; set; }
    /// <summary>Directory containing CEF resources (icudtl.dat, *.pak).</summary>
    public string? ResourcesDirPath { get; set; }
    /// <summary>Directory containing the locales.</summary>
    public string? LocalesDirPath { get; set; }

    // ---- Strings ----
    /// <summary>User agent string. Values containing spaces are supported.</summary>
    public string? UserAgent { get; set; }
    /// <summary>User agent product string.</summary>
    public string? UserAgentProduct { get; set; }
    /// <summary>Locale, e.g. "zh-CN".</summary>
    public string? Locale { get; set; }
    /// <summary>Additional V8 flags.</summary>
    public string? JavascriptFlags { get; set; }
    /// <summary>Comma separated list of accepted languages.</summary>
    public string? AcceptLanguageList { get; set; }
    /// <summary>Comma separated list of schemes that can set cookies.</summary>
    public string? CookieableSchemesList { get; set; }

    // ---- Numbers / enums ----
    /// <summary>CEF log verbosity.</summary>
    public CefLogSeverity? LogSeverity { get; set; }
    /// <summary>Remote debugging port; 0 disables it.</summary>
    public int? RemoteDebuggingPort { get; set; }
    /// <summary>Stack size for uncaught exceptions.</summary>
    public int? UncaughtExceptionStackSize { get; set; }
    /// <summary>Background color as 0xAARRGGBB.</summary>
    public uint? BackgroundColor { get; set; }

    /// <summary>
    /// Additional switches passed to the native host verbatim
    /// (for example <c>--disable-gpu</c> or <c>--proxy-server=...</c>).
    /// </summary>
    public List<string> CommandLineSwitches { get; set; } = new();

    /// <summary>
    /// Serializes all set properties to individual <c>--cef-{name}={value}</c>
    /// arguments. Use this together with <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// so values containing spaces or quotes are passed intact.
    /// </summary>
    public IReadOnlyList<string> ToArgumentList()
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

        foreach (string sw in CommandLineSwitches)
        {
            if (!string.IsNullOrWhiteSpace(sw))
                parts.Add(sw);
        }

        return parts;
    }

    /// <summary>
    /// Legacy helper that joins <see cref="ToArgumentList"/> into a single
    /// command line string with Windows quoting rules. Prefer
    /// <see cref="ToArgumentList"/> + <c>ProcessStartInfo.ArgumentList</c>.
    /// </summary>
    public string ToCommandLineArgs()
    {
        IReadOnlyList<string> parts = ToArgumentList();
        if (parts.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (string part in parts)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteArgument(part));
        }
        return sb.ToString();
    }

    /// <summary>Quotes a single argument using CommandLineToArgvW rules.</summary>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";
        if (argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;

        var sb = new StringBuilder(argument.Length + 4);
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
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
        if (!value.HasValue) return;

        string? str = value.Value switch
        {
            CefLogSeverity.Default => "default",
            CefLogSeverity.Verbose => "verbose",
            CefLogSeverity.Info => "info",
            CefLogSeverity.Warning => "warning",
            CefLogSeverity.Error => "error",
            CefLogSeverity.Fatal => "fatal",
            CefLogSeverity.Disable => "disable",
            _ => null,
        };

        if (str != null)
            parts.Add($"--cef-{name}={str}");
    }
}
