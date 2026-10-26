// CefBrowser.Native — CEF subprocess + shared browser host
// Architecture:
//   WinMain → CefExecuteProcess (subprocess) | CefInitialize (main process)
//   Main process: hosts MULTIPLE CefBrowser instances (Chrome-style shared model),
//   pumps messages, dispatches commands/events between C# (stdio) and CEF.
//   Shared across browsers: GPU process, network service, storage service.
//   Isolated per browser: renderer processes + optional RequestContext (own cache dir).
//
// IPC protocol v3 (line oriented over stdin/stdout; text fields are escaped with
// proto::Escape so '|', CR and LF never break framing):
//   C#→Native: SetHostWindow|{id}|{xid} (optional, before Create),
//              Create|{id}|{cachePath}|{url}, Navigate|{id}|{url}, Reload|{id},
//              Stop|{id}, Resize|{id}|{w}|{h}, EmbedDone|{id}, Close|{id}, Quit,
//              Back|{id}, Forward|{id}, Zoom|{id}|{delta},
//              Find|{id}|{text}|{forward}|{findNext}, StopFind|{id}, DevTools|{id},
//              ExecJS|{id}|{base64 script},
//              JsDialogResult|{id}|{dialogId}|{accept}|{text}
//   Native→C#: Hello|3, Ready|{id}|{HWND_HEX}, AddressChanged|{id}|{url},
//              LoadError|{id}|{code}|{text}|{url}, NavState|{id}|{l}|{b}|{f},
//              TitleChanged|{id}|{title}, OpenPopup|{id}|{url},
//              JsDialog|{id}|{dialogId}|{type}|{message}|{default},
//              DownloadStart|{id}|{path}, DownloadProgress|{id}|{received}|{total},
//              DownloadComplete|{id}|{path}, DownloadCanceled|{id}
//   The process exits on Quit or when the managed side closes stdin.

#include "browser_handler.h"
#include "protocol.h"
#include "stdio_server.h"
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_frame.h"
#include "include/cef_request_context.h"
#include "include/cef_request_context_handler.h"
#include "include/internal/cef_types_wrappers.h"
#include "include/cef_task.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/base/cef_bind.h"
#include "include/base/cef_callback_helpers.h"

#if defined(_WIN32)
#include <Windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include "include/cef_sandbox_win.h"
#elif defined(__linux__)
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#include <gtk/gtk.h>
#include <limits.h>
#include <sys/stat.h>
#include <unistd.h>
#endif

#include <atomic>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <map>
#include <vector>
#include <queue>
#include <mutex>


// ---- Forward declarations ----
static bool HasArg(const char* arg);
static std::string GetArgValue(const std::string& key);
static std::string GetEnv(const char* name);
static void StripTypeFromCommandLine();
static void RequestShutdown();
static void ResetBrowserReady();
static void MarkBrowserReady();
static void WakePump();

// ---- Globals ----
static CefRefPtr<BrowserHandler> g_handler;
static StdioServer* g_stdioServer = nullptr;
#if defined(_WIN32)
static HANDLE g_browserReadyEvent = nullptr;
static HANDLE g_shutdownEvent = nullptr;
static HANDLE g_pumpWakeEvent = nullptr;   // auto-reset, wakes pump for early deadlines / IPC
#else
static std::atomic<bool> g_browserReady{false};
static std::atomic<bool> g_shutdownRequested{false};
static bool g_pumpWake = false;
static std::mutex g_waitMutex;
static std::condition_variable g_waitCv;
#endif
static CefWindowHandle g_browserWindow = 0;
static CefWindowHandle g_hiddenParent = 0;

#if defined(_WIN32)
static const wchar_t kHiddenClass[] = L"CefHidden_{B3A0B1C2}";
#elif defined(__linux__)
static Display* g_xdisplay = nullptr;
static Atom g_wmDeleteAtom = None;
bool g_gtkInitialized = false;
#endif

// ---- DIAG logging (silent unless CEF_DIAG=1) ----
static bool g_diagEnabled = false;
static void DiagLog(const char* fmt, ...) {
    if (!g_diagEnabled) return;
    va_list args;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fputc('\n', stderr);
    fflush(stderr);
}

// ---- Time / external message pump state ----
// With external_message_pump=true CEF asks us (OnScheduleMessagePumpWork) to run
// CefDoMessageLoopWork within delay_ms. We park the main thread on kernel waits
// (Windows) or a condition variable (Linux) instead of a Sleep(1) poll loop.
static uint64_t NowMs() {
#if defined(_WIN32)
    return (uint64_t)GetTickCount64();
#else
    using namespace std::chrono;
    return (uint64_t)duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
#endif
}

static std::mutex g_pumpMutex;
static uint64_t g_pumpDueTick = 0;         // absolute deadline; 0 = nothing pending
static uint32_t g_pumpGen = 0;             // bumped on every schedule request

class PumpApp : public CefApp, public CefBrowserProcessHandler {
public:
    PumpApp() = default;

    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }

    void OnScheduleMessagePumpWork(int64 delay_ms) override {
        if (delay_ms < 0) delay_ms = 0;
        uint64_t due = NowMs() + (uint64_t)delay_ms;
        bool wake = false;
        {
            std::lock_guard<std::mutex> lock(g_pumpMutex);
            if (g_pumpDueTick == 0 || due < g_pumpDueTick) { g_pumpDueTick = due; wake = true; }
            g_pumpGen++;
        }
        if (wake) WakePump();
    }
private:
    IMPLEMENT_REFCOUNTING(PumpApp);
};
static CefRefPtr<PumpApp> g_pumpApp;

static void WakePump() {
#if defined(_WIN32)
    if (g_pumpWakeEvent) SetEvent(g_pumpWakeEvent);
#else
    {
        std::lock_guard<std::mutex> lock(g_waitMutex);
        g_pumpWake = true;
    }
    g_waitCv.notify_all();
#endif
}

static uint32_t PumpGenSnapshot() {
    std::lock_guard<std::mutex> lock(g_pumpMutex);
    return g_pumpGen;
}

static uint32_t PumpWaitTimeoutMs() {
    std::lock_guard<std::mutex> lock(g_pumpMutex);
    if (g_pumpDueTick == 0) return UINT32_MAX;
    uint64_t now = NowMs();
    return now >= g_pumpDueTick ? 0 : (uint32_t)(g_pumpDueTick - now);
}

static bool g_standaloneMode = false;

static void RequestShutdown() {
#if defined(_WIN32)
    if (g_shutdownEvent) SetEvent(g_shutdownEvent);
#else
    g_shutdownRequested = true;
    g_waitCv.notify_all();
#endif
}

static void MarkBrowserReady() {
#if defined(_WIN32)
    if (g_browserReadyEvent) SetEvent(g_browserReadyEvent);
#else
    g_browserReady = true;
    g_waitCv.notify_all();
#endif
}

static void ResetBrowserReady() {
#if defined(_WIN32)
    if (g_browserReadyEvent) ResetEvent(g_browserReadyEvent);
#else
    g_browserReady = false;
#endif
}

static bool IsBrowserReady() {
#if defined(_WIN32)
    return g_browserReadyEvent && WaitForSingleObject(g_browserReadyEvent, 0) == WAIT_OBJECT_0;
#else
    return g_browserReady.load();
#endif
}

enum class PumpWaitResult { Timeout, Wake, Message, Shutdown, BrowserReady };

// Waits up to |timeoutMs| for CEF work / IPC / native input. On Windows this
// also drains the Win32 message queue. On Linux it drains our own X11
// connection (helper windows only; CEF owns its own X connection) and GTK.
static PumpWaitResult WaitForPump(uint32_t timeoutMs) {
#if defined(_WIN32)
    HANDLE waits[2] = { g_shutdownEvent, g_pumpWakeEvent };
    DWORD wr = MsgWaitForMultipleObjectsEx(2, waits, timeoutMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
    if (wr == WAIT_OBJECT_0) return PumpWaitResult::Shutdown;

    MSG msg;
    bool haveMsgs = false;
    while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
        haveMsgs = true;
        if (msg.message == WM_QUIT) return PumpWaitResult::Shutdown;
        if (g_standaloneMode && msg.message == WM_CLOSE) return PumpWaitResult::Shutdown;
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    if (haveMsgs) return PumpWaitResult::Message;
    if (wr == WAIT_OBJECT_0 + 1) return PumpWaitResult::Wake;
    return PumpWaitResult::Timeout;
#else
    {
        std::unique_lock<std::mutex> lock(g_waitMutex);
        g_waitCv.wait_for(lock, std::chrono::milliseconds(timeoutMs), [] {
            return g_pumpWake || g_shutdownRequested.load() || g_browserReady.load();
        });
    }
    if (g_shutdownRequested.load()) return PumpWaitResult::Shutdown;
    if (g_browserReady.exchange(false)) return PumpWaitResult::BrowserReady;

    bool wake;
    {
        std::lock_guard<std::mutex> lock(g_waitMutex);
        wake = g_pumpWake;
        g_pumpWake = false;
    }

    if (g_xdisplay) {
        while (XPending(g_xdisplay)) {
            XEvent ev;
            XNextEvent(g_xdisplay, &ev);
            if (ev.type == ClientMessage && (Atom)ev.xclient.data.l[0] == g_wmDeleteAtom)
                return PumpWaitResult::Shutdown;
        }
    }
    if (g_gtkInitialized) {
        while (gtk_events_pending())
            gtk_main_iteration_do(FALSE);
    }
    return wake ? PumpWaitResult::Wake : PumpWaitResult::Timeout;
#endif
}

// ---- URL normalization (all browser logic in C++) ----
static std::string NormalizeUrl(const std::string& input) {
    std::string url = input;
    size_t s = url.find_first_not_of(" \t\r\n");
    if (s == std::string::npos) return {};
    size_t e = url.find_last_not_of(" \t\r\n");
    url = url.substr(s, e - s + 1);
    if (url.empty()) return {};

    // Any explicit scheme is kept verbatim: https:, http:, file:, about:,
    // data:, mailto:, custom-app: ... Windows drive letters (C:/ or C:\) are
    // paths, not schemes.
    size_t colon = url.find(':');
    if (colon != std::string::npos && colon > 0) {
        bool validScheme = true;
        for (size_t i = 0; i < colon; ++i) {
            unsigned char c = (unsigned char)url[i];
            bool ok = (i == 0) ? isalpha(c) : (isalnum(c) || c == '+' || c == '-' || c == '.');
            if (!ok) { validScheme = false; break; }
        }
        bool driveLetter = colon == 1 && isalpha((unsigned char)url[0]) &&
                           url.size() > 2 && (url[2] == '/' || url[2] == '\\');
        if (validScheme && !driveLetter)
            return url;
    }

    if (url.find("file://") == 0 || url.find("about:") == 0 ||
        url.find("http://") == 0 || url.find("https://") == 0)
        return url;
    if (url.find('\\') != std::string::npos ||
        url.find(":/") != std::string::npos || url[0] == '/') {
        for (auto& c : url) if (c == '\\') c = '/';
        if (url.find("file://") != 0) {
            if (url[0] != '/') url = "/" + url;
            url = "file://" + url;
        }
        return url;
    }
    url = "https://" + url;
    size_t hostEnd = url.find('/', 8);
    if (hostEnd == std::string::npos) hostEnd = url.length();
    std::string host = url.substr(8, hostEnd - 8);
    int dots = 0;
    for (char c : host) if (c == '.') dots++;
    if (dots == 1 && host.find("www.") != 0)
        url.insert(8, "www.");
    return url;
}

// Strip URL prefix for display (e.g. "file:///F:/test" → "/F:/test", "about:blank" → "blank")
// Display URL: the real address is reported (no host heuristics), only the
// file:// prefix is shortened because it is just noise in an address bar.
static std::string DisplayUrl(const std::string& url) {
    if (url.find("file:///") == 0)
        return url.substr(8);
    if (url.find("file://") == 0)
        return url.substr(7);
    return url;
}

// ---- Base64 decoding ----
// Used to smuggle multi-line JavaScript through the line-oriented stdio pipe.
static std::string Base64Decode(const std::string& in) {
    static const char kTable[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    int val = 0;
    int valBits = -8;
    for (unsigned char c : in) {
        if (c == '=')
            break;
        const char* p = strchr(kTable, c);
        if (!p)
            continue;                       // tolerate stray whitespace/CR
        val = (val << 6) + static_cast<int>(p - kTable);
        valBits += 6;
        if (valBits >= 0) {
            out.push_back(static_cast<char>((val >> valBits) & 0xFF));
            valBits -= 8;
        }
    }
    return out;
}

// ---- Command queue (pipe thread -> main pump) ----
enum class CmdType { Create, Navigate, Reload, Stop, CloseOne, Resize, EmbedDone, Quit,
                     Back, Forward, Zoom, Find, StopFind, DevTools, ExecJS,
                     JsDialogResult, SetHostWindow, Nop };
struct Cmd { CmdType type; int id = 0; std::string arg; };
static std::queue<Cmd> g_cmdQueue;
static std::mutex g_cmdMutex;

static void PushCmd(CmdType type, int id = 0, const std::string& arg = "") {
    {
        std::lock_guard<std::mutex> lock(g_cmdMutex);
        g_cmdQueue.push({type, id, arg});
    }
    WakePump();
}

static Cmd PopCmd() {
    std::lock_guard<std::mutex> lock(g_cmdMutex);
    if (g_cmdQueue.empty()) return {CmdType::Nop};
    auto c = g_cmdQueue.front();
    g_cmdQueue.pop();
    return c;
}

// ---- Browser instance registry (shared host, N browsers) ----
struct BrowserInstance {
    int id = 0;
    CefRefPtr<BrowserHandler> handler;
    CefWindowHandle parentWindow = 0;
    bool ownsParentWindow = false;  // false when the parent belongs to the host app
    double zoomLevel = 0.0;   // CEF zoom level: 0 = 100%
};
static std::map<int, BrowserInstance> g_browsers;

// Host window handed over by the managed side with SetHostWindow, consumed by
// DoCreateBrowser. Only used where creating the browser under a hidden parent
// does not work reliably (X11).
static std::map<int, CefWindowHandle> g_hostWindows;
static std::mutex g_hostWindowsMutex;
static std::mutex g_browsersMutex;

static void SendEv(const std::string& line) {
    if (g_stdioServer) g_stdioServer->SendEvent(line);
}

#if defined(_WIN32)
static bool HasArg(const char* arg) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool found = false;
    size_t argLen = strlen(arg);
    std::wstring warg(arg, arg + argLen);
    for (int i = 1; i < argc; i++) {
        if (_wcsnicmp(argv[i], warg.c_str(), argLen) == 0) { found = true; break; }
    }
    LocalFree(argv);
    return found;
}

static std::string GetArgValue(const std::string& key) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return {};
    std::string result;
    for (int i = 1; i < argc; i++) {
        std::wstring warg(argv[i]);
        auto eq = warg.find(L'=');
        if (eq == std::wstring::npos) continue;
        std::string argKey;
        argKey.resize(eq);
        for (size_t j = 0; j < eq; j++) argKey[j] = (char)warg[j];
        if (argKey != key) continue;
        int vlen = WideCharToMultiByte(CP_UTF8, 0, warg.c_str() + eq + 1, -1, nullptr, 0, nullptr, nullptr);
        if (vlen > 0) vlen--;
        if (vlen > 0) {
            result.resize(vlen);
            WideCharToMultiByte(CP_UTF8, 0, warg.c_str() + eq + 1, -1, &result[0], vlen + 1, nullptr, nullptr);
        }
        break;
    }
    LocalFree(argv);
    if (result.size() >= 2 && result.front() == '"' && result.back() == '"')
        result = result.substr(1, result.size() - 2);
    return result;
}
#elif defined(__linux__)
static int g_argc = 0;
static char** g_argv = nullptr;

static bool HasArg(const char* arg) {
    size_t len = strlen(arg);
    for (int i = 1; i < g_argc; i++) {
        if (strncmp(g_argv[i], arg, len) == 0)
            return true;
    }
    return false;
}

static std::string GetArgValue(const std::string& key) {
    for (int i = 1; i < g_argc; i++) {
        const char* a = g_argv[i];
        if (strncmp(a, key.c_str(), key.size()) == 0 && a[key.size()] == '=')
            return std::string(a + key.size() + 1);
    }
    return {};
}
#endif

static std::string GetEnv(const char* name) {
    auto val = getenv(name);
    return val ? std::string(val) : std::string();
}

static void ApplyCefSettingsFromArgs(CefSettings& settings) {
    auto setStr = [](CefSettings& s, const char* cefKey, cef_string_t& field) {
        std::string val = GetArgValue(cefKey);
        if (!val.empty()) CefString(&field).FromString(val);
    };
    auto setBool = [](const char* cefKey) -> int {
        std::string val = GetArgValue(cefKey);
        return (!val.empty() && (val == "true" || val == "1")) ? 1 : -1;
    };

    int b;
    if ((b = setBool("--cef-no-sandbox")) >= 0) settings.no_sandbox = b;
    setStr(settings, "--cef-browser-subprocess-path", settings.browser_subprocess_path);
    setStr(settings, "--cef-framework-dir-path", settings.framework_dir_path);
    setStr(settings, "--cef-main-bundle-path", settings.main_bundle_path);
    if ((b = setBool("--cef-chrome-runtime")) >= 0) settings.chrome_runtime = b;
    if ((b = setBool("--cef-multi-threaded-message-loop")) >= 0) settings.multi_threaded_message_loop = b;
    if ((b = setBool("--cef-external-message-pump")) >= 0) settings.external_message_pump = b;
    if ((b = setBool("--cef-windowless-rendering-enabled")) >= 0) settings.windowless_rendering_enabled = b;
    if ((b = setBool("--cef-command-line-args-disabled")) >= 0) settings.command_line_args_disabled = b;
    setStr(settings, "--cef-cache-path", settings.cache_path);
    setStr(settings, "--cef-root-cache-path", settings.root_cache_path);
    setStr(settings, "--cef-user-data-path", settings.user_data_path);
    if ((b = setBool("--cef-persist-session-cookies")) >= 0) settings.persist_session_cookies = b;
    if ((b = setBool("--cef-persist-user-preferences")) >= 0) settings.persist_user_preferences = b;
    setStr(settings, "--cef-user-agent", settings.user_agent);
    setStr(settings, "--cef-user-agent-product", settings.user_agent_product);
    setStr(settings, "--cef-locale", settings.locale);
    setStr(settings, "--cef-log-file", settings.log_file);

    std::string ls = GetArgValue("--cef-log-severity");
    if (!ls.empty()) {
        if (ls == "verbose") settings.log_severity = LOGSEVERITY_VERBOSE;
        else if (ls == "info") settings.log_severity = LOGSEVERITY_INFO;
        else if (ls == "warning") settings.log_severity = LOGSEVERITY_WARNING;
        else if (ls == "error") settings.log_severity = LOGSEVERITY_ERROR;
        else if (ls == "fatal") settings.log_severity = LOGSEVERITY_FATAL;
        else if (ls == "disable") settings.log_severity = LOGSEVERITY_DISABLE;
        else if (ls == "default") settings.log_severity = LOGSEVERITY_DEFAULT;
    }

    setStr(settings, "--cef-javascript-flags", settings.javascript_flags);
    setStr(settings, "--cef-resources-dir-path", settings.resources_dir_path);
    setStr(settings, "--cef-locales-dir-path", settings.locales_dir_path);
    if ((b = setBool("--cef-pack-loading-disabled")) >= 0) settings.pack_loading_disabled = b;

    std::string rdp = GetArgValue("--cef-remote-debugging-port");
    if (!rdp.empty()) settings.remote_debugging_port = atoi(rdp.c_str());

    std::string ues = GetArgValue("--cef-uncaught-exception-stack-size");
    if (!ues.empty()) settings.uncaught_exception_stack_size = atoi(ues.c_str());

    std::string bc = GetArgValue("--cef-background-color");
    if (!bc.empty()) {
        unsigned long color = 0;
#if defined(_WIN32)
        sscanf_s(bc.c_str(), "%lx", &color);
#else
        sscanf(bc.c_str(), "%lx", &color);
#endif
        settings.background_color = (cef_color_t)color;
    }

    setStr(settings, "--cef-accept-language-list", settings.accept_language_list);
    setStr(settings, "--cef-cookieable-schemes-list", settings.cookieable_schemes_list);
    if ((b = setBool("--cef-cookieable-schemes-exclude-defaults")) >= 0) settings.cookieable_schemes_exclude_defaults = b;
}

static void StripTypeFromCommandLine() {
#if defined(_WIN32)
    LPWSTR cmd = GetCommandLineW();
    std::wstring wcmd(cmd);
    bool changed = false;
    auto pos = wcmd.find(L"--type=");
    while (pos != std::wstring::npos) {
        auto end = wcmd.find(L' ', pos + 7);
        if (end == std::wstring::npos) end = wcmd.length();
        wcmd.erase(pos, end - pos + (end < wcmd.length() ? 1 : 0));
        pos = wcmd.find(L"--type=");
        changed = true;
    }
    if (changed) {
        size_t len = wcslen(cmd) + 1;
        wcscpy_s(cmd, len, wcmd.c_str());
    }
#else
    // On Linux CefExecuteProcess already consumed any --type= subprocess
    // invocation; main-process argv is owned by the CRT and is left untouched.
#endif
}

// ---- Platform window / path helpers ----

static std::string WindowToHex(CefWindowHandle window) {
    char buf[32];
#if defined(_WIN32)
    snprintf(buf, sizeof(buf), "%I64X", (unsigned long long)(uintptr_t)window);
#else
    snprintf(buf, sizeof(buf), "%lX", (unsigned long)window);
#endif
    return std::string(buf);
}

#if defined(__linux__)
static std::string GetExePath() {
    char buf[PATH_MAX] = {0};
    ssize_t n = readlink("/proc/self/exe", buf, sizeof(buf) - 1);
    return n > 0 ? std::string(buf, (size_t)n) : std::string();
}
#endif

// Per-user cache directory used when the managed side does not configure one.
// Never falls back to the executable directory: that is often read-only
// (Program Files) and makes CEF fail to create its profile.
static std::string GetDefaultCachePath() {
#if defined(_WIN32)
    char buf[MAX_PATH] = {0};
    if (FAILED(SHGetFolderPathA(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, buf))) {
        std::string local = GetEnv("LOCALAPPDATA");
        if (!local.empty()) {
            strncpy_s(buf, local.c_str(), _TRUNCATE);
        }
    }
    std::string root = buf[0] ? std::string(buf) : std::string(".");
    return root + "\\CefAvalonia\\cef_cache";
#else
    std::string xdg = GetEnv("XDG_CACHE_HOME");
    if (!xdg.empty())
        return xdg + "/cefavalonia/cef_cache";

    std::string home = GetEnv("HOME");
    if (home.empty())
        home = ".";
    return home + "/.cache/cefavalonia/cef_cache";
#endif
}

#if defined(__linux__)
// CEF's Linux sandbox needs a SUID-root "chrome-sandbox" helper next to the
// executable. Distributions rarely ship it preconfigured, and starting with the
// sandbox enabled would abort at CefInitialize. Returning false lets the host
// fall back to --no-sandbox with a clear warning instead of a black window.
static bool LinuxSandboxAvailable() {
    std::string exePath = GetExePath();
    auto sep = exePath.find_last_of('/');
    if (sep == std::string::npos)
        return false;

    std::string helper = exePath.substr(0, sep + 1) + "chrome-sandbox";

    struct stat st;
    if (stat(helper.c_str(), &st) != 0 || !S_ISREG(st.st_mode))
        return false;

    return (st.st_mode & S_ISUID) != 0 && st.st_uid == 0;
}
#endif

#if defined(_WIN32)
static ATOM RegisterHiddenClass() {
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kHiddenClass;
    return RegisterClassExW(&wc);
}
#endif

static CefWindowHandle CreateHiddenParentWindow(bool visible, const char* title) {
#if defined(_WIN32)
    (void)title;
    if (!visible) {
        return CreateWindowExW(0, kHiddenClass, L"", WS_POPUP | WS_CLIPCHILDREN,
                               0, 0, 1, 1, nullptr, nullptr,
                               GetModuleHandleW(nullptr), nullptr);
    }
    return CreateWindowExW(0, kHiddenClass, L"CEF Browser Test",
                           WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_VISIBLE,
                           CW_USEDEFAULT, CW_USEDEFAULT, 1280, 800,
                           nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
#else
    if (!g_xdisplay)
        return 0;
    int screen = DefaultScreen(g_xdisplay);
    Window root = RootWindow(g_xdisplay, screen);
    XSetWindowAttributes attrs = {};
    attrs.background_pixel = BlackPixel(g_xdisplay, screen);
    attrs.event_mask = StructureNotifyMask | ExposureMask;
    Window w = XCreateWindow(
        g_xdisplay, root, 0, 0, visible ? 1280 : 1, visible ? 800 : 1, 0,
        CopyFromParent, InputOutput, CopyFromParent,
        CWBackPixel | CWEventMask, &attrs);
    if (!w)
        return 0;
    if (title)
        XStoreName(g_xdisplay, w, title);
    if (visible) {
        XSetWMProtocols(g_xdisplay, w, &g_wmDeleteAtom, 1);
        XMapWindow(g_xdisplay, w);
    }
    XFlush(g_xdisplay);
    return w;
#endif
}

static void MoveNativeWindow(CefWindowHandle window, int width, int height) {
    if (!window)
        return;
#if defined(_WIN32)
    MoveWindow(window, 0, 0, width, height, FALSE);
#else
    if (!g_xdisplay)
        return;
    XMoveResizeWindow(g_xdisplay, window, 0, 0, (unsigned)width, (unsigned)height);
    XFlush(g_xdisplay);
#endif
}

static void ShowNativeWindow(CefWindowHandle window, bool show) {
    if (!window)
        return;
#if defined(_WIN32)
    ShowWindow(window, show ? SW_SHOW : SW_HIDE);
#else
    if (!g_xdisplay)
        return;
    if (show) {
        XMapRaised(g_xdisplay, window);
    } else {
        XUnmapWindow(g_xdisplay, window);
    }
    XFlush(g_xdisplay);
#endif
}

static void DestroyNativeWindow(CefWindowHandle window) {
    if (!window)
        return;
#if defined(_WIN32)
    DestroyWindow(window);
#else
    if (!g_xdisplay)
        return;
    XDestroyWindow(g_xdisplay, window);
    XFlush(g_xdisplay);
#endif
}

static CefRefPtr<CefBrowser> FindBrowser(int id) {
    std::lock_guard<std::mutex> lock(g_browsersMutex);
    auto it = g_browsers.find(id);
    return it != g_browsers.end() ? it->second.handler->GetBrowser() : nullptr;
}

static void DoNavigate(int id, const std::string& url) {
    DiagLog("DIAG: [C++] DoNavigate id=%d url=%s", id, url.c_str());
    auto b = FindBrowser(id);
    if (b) b->GetMainFrame()->LoadURL(url);
}

static void DoReload(int id) {
    auto b = FindBrowser(id);
    if (b) b->Reload();
}

static void DoStop(int id) {
    auto b = FindBrowser(id);
    if (b) b->StopLoad();
}

static void DoCloseBrowser(int id) {
    auto b = FindBrowser(id);
    if (b) b->GetHost()->CloseBrowser(true);
}

static void DoGoBack(int id) {
    auto b = FindBrowser(id);
    if (b && b->CanGoBack()) b->GoBack();
}

static void DoGoForward(int id) {
    auto b = FindBrowser(id);
    if (b && b->CanGoForward()) b->GoForward();
}

// |delta| > 0 zooms in, < 0 zooms out, == 0 resets to 100%.
static void DoZoom(int id, int delta) {
    auto b = FindBrowser(id);
    if (!b) return;

    double level = 0.0;
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        auto it = g_browsers.find(id);
        if (it == g_browsers.end()) return;
        if (delta > 0)      it->second.zoomLevel += 0.5;
        else if (delta < 0) it->second.zoomLevel -= 0.5;
        else                it->second.zoomLevel = 0.0;
        // Clamp to a sane range (roughly 25% .. 500%).
        if (it->second.zoomLevel >  7.0) it->second.zoomLevel =  7.0;
        if (it->second.zoomLevel < -7.0) it->second.zoomLevel = -7.0;
        level = it->second.zoomLevel;
    }
    b->GetHost()->SetZoomLevel(level);
}

static void DoFind(int id, const std::string& payload) {
    auto b = FindBrowser(id);
    if (!b) return;
    // payload = "{text}|{forward}|{findNext}" (text is escaped)
    std::vector<std::string> fields = proto::Split(payload, 3);
    std::string text = fields.size() > 0 ? fields[0] : "";
    bool forward = fields.size() > 1 ? fields[1] != "0" : true;
    bool findNext = fields.size() > 2 && fields[2] == "1";
    // CEF 109 signature: Find(searchText, forward, matchCase, findNext)
    b->GetHost()->Find(text, forward, false, findNext);
}

static void DoStopFind(int id) {
    auto b = FindBrowser(id);
    if (b) b->GetHost()->StopFinding(true);
}

static void DoShowDevTools(int id) {
    CefRefPtr<BrowserHandler> handler;
    CefRefPtr<CefBrowser> b;
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        auto it = g_browsers.find(id);
        if (it != g_browsers.end()) {
            handler = it->second.handler;
            b = handler->GetBrowser();
        }
    }
    if (!b || !handler) return;
    CefWindowInfo wi;
#if defined(_WIN32)
    wi.SetAsPopup(nullptr, "DevTools");
#else
    // Linux CefWindowInfo has no SetAsPopup; host DevTools in a normal
    // top-level window so the window is usable outside the embedded panel.
    CefWindowHandle devToolsParent = CreateHiddenParentWindow(true, "DevTools");
    if (!devToolsParent) return;
    wi.SetAsChild(devToolsParent, CefRect(0, 0, 1200, 800));
#endif
    CefBrowserSettings bs;
    // Reuse the inspected browser's own CefClient. Passing nullptr would leave
    // the DevTools window without handlers and can crash on first interaction.
    b->GetHost()->ShowDevTools(wi, handler, bs, CefPoint());
}

// Executes JavaScript in the main frame. The script travels base64-encoded so
// that newlines (which would break the line-oriented stdio protocol) survive.
static void DoExecJS(int id, const std::string& b64) {
    auto b = FindBrowser(id);
    if (!b) return;
    std::string code = Base64Decode(b64);
    if (code.empty()) return;
    auto frame = b->GetMainFrame();
    if (frame) frame->ExecuteJavaScript(code, frame->GetURL(), 0);
}

// Answers a pending JavaScript dialog.
static void DoResolveJsDialog(int id, const std::string& payload) {
    CefRefPtr<BrowserHandler> handler;
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        auto it = g_browsers.find(id);
        if (it == g_browsers.end()) return;
        handler = it->second.handler;
    }

    // payload = "{dialogId}|{accept}|{text}" (text is escaped)
    std::vector<std::string> fields = proto::Split(payload, 3);
    if (fields.empty()) return;

    int dialogId = atoi(fields[0].c_str());
    bool accept = fields.size() > 1 && fields[1] == "1";
    std::string text = fields.size() > 2 ? fields[2] : "";
    handler->ResolveJsDialog(dialogId, accept, text);
}

static void RequestCloseAllBrowsers() {
    std::vector<int> ids;
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        for (auto& [k, v] : g_browsers) ids.push_back(k);
    }
    for (int id : ids)
        CefPostTask(TID_UI, base::BindOnce(&DoCloseBrowser, id));
}

// Create a browser on the UI thread. cachePath empty → shared default context,
// otherwise a dedicated RequestContext (isolated cookies/storage per browser).
static void DoCreateBrowser(int id, const std::string& cachePath, const std::string& url) {
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        if (g_browsers.count(id)) { DiagLog("DIAG: [C++] Create id=%d already exists", id); return; }
    }

    // Creating the browser under a hidden, unmapped parent and reparenting it
    // afterwards can leave the X11 window unrealized (the control stays
    // transparent). When the managed side hands over its own — already mapped —
    // host window, create the browser directly inside it instead. That window
    // belongs to the host app, so we must never destroy it.
    CefWindowHandle hostWindow = 0;
    {
        std::lock_guard<std::mutex> lock(g_hostWindowsMutex);
        auto it = g_hostWindows.find(id);
        if (it != g_hostWindows.end()) { hostWindow = it->second; g_hostWindows.erase(it); }
    }

    CefWindowHandle parent = 0;
    bool ownsParent = false;
    if (hostWindow) {
        parent = hostWindow;
    } else {
        parent = CreateHiddenParentWindow(false, nullptr);
        ownsParent = true;
        if (!parent) { DiagLog("DIAG: [C++] Create id=%d parent window FAILED", id); SendEv("LoadError|" + std::to_string(id) + "|-100|CreateWindow failed|"); return; }
    }

    auto handler = new BrowserHandler();
    handler->SetBrowserId(id);
    handler->OnDownloadEvent = [id](const std::string& s) {
        SendEv(s);
    };
    handler->OnBrowserReady = [id](CefWindowHandle window) {
        SendEv("Ready|" + std::to_string(id) + "|" + WindowToHex(window));
    };
    handler->OnBrowserClosed = [id]() {
        CefWindowHandle parentToDestroy = 0;
        bool ownsParentToDestroy = false;
        {
            std::lock_guard<std::mutex> lock(g_browsersMutex);
            auto it = g_browsers.find(id);
            if (it != g_browsers.end()) {
                parentToDestroy = it->second.parentWindow;
                ownsParentToDestroy = it->second.ownsParentWindow;
                g_browsers.erase(it);
            }
        }
        // Only windows we created ourselves may be destroyed: a parent handed
        // over by the managed side belongs to the host application (Avalonia),
        // and destroying it would take the app window down with it.
        if (parentToDestroy && ownsParentToDestroy) DestroyNativeWindow(parentToDestroy);
        // Host lifetime is owned by the C# side: it stays alive until an explicit
        // Quit or stdin EOF (parent process death). Do NOT exit just because the
        // last browser closed — the app may open a new tab afterwards.
    };
    handler->OnAddressChanged = [id](const std::string& u) {
        // The handler already ignores sub-frame navigations, so this is the
        // real top-level address: no host-based filtering is needed.
        SendEv("AddressChanged|" + std::to_string(id) + "|" + proto::Escape(DisplayUrl(u)));
    };
    handler->OnLoadErrorEvent = [id](const std::string& s) {
        // s = "{code}|{text}|{url}" (already escaped by BrowserHandler)
        SendEv("LoadError|" + std::to_string(id) + "|" + s);
    };
    handler->OnLoadingStateChanged = [id](bool isLoading, bool canGoBack, bool canGoForward) {
        SendEv("NavState|" + std::to_string(id) + "|" + std::string(isLoading ? "1" : "0") + "|" +
               std::string(canGoBack ? "1" : "0") + "|" + std::string(canGoForward ? "1" : "0"));
    };
    handler->OnTitleChangedCB = [id](const std::string& title) {
        SendEv("TitleChanged|" + std::to_string(id) + "|" + proto::Escape(title));
    };
    handler->OnBeforePopupCB = [id](const std::string& popupUrl) {
        SendEv("OpenPopup|" + std::to_string(id) + "|" + proto::Escape(popupUrl));
    };
    handler->OnJsDialog = [id](int dialogId, const std::string& type,
                               const std::string& message, const std::string& defaultText) {
        SendEv("JsDialog|" + std::to_string(id) + "|" + std::to_string(dialogId) + "|" +
               type + "|" + proto::Escape(message) + "|" + proto::Escape(defaultText));
    };

    CefRequestContextSettings rcs;
    CefRefPtr<CefRequestContext> requestCtx;
    if (!cachePath.empty()) {
        CefString(&rcs.cache_path) = cachePath;
        requestCtx = CefRequestContext::CreateContext(rcs, nullptr);
    }

    CefWindowInfo wi;
    wi.SetAsChild(parent, CefRect(0, 0, 1280, 800));
    CefBrowserSettings bs;
    DiagLog("DIAG: [C++] CreateBrowserSync id=%d url=%s cache=%s", id, url.c_str(), cachePath.c_str());

    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        g_browsers[id] = {id, handler, parent, ownsParent};
    }
    CefBrowserHost::CreateBrowserSync(wi, handler, url.empty() ? "about:blank" : url, bs, nullptr, requestCtx);
}

static void ExecuteCmd(const Cmd& c) {
    DiagLog("DIAG: [C++] ExecuteCmd type=%d id=%d arg=%s", (int)c.type, c.id, c.arg.c_str());
    switch (c.type) {
        case CmdType::Create: {
            // "{cachePath}|{url}" (either may be empty, both escaped)
            std::vector<std::string> fields = proto::Split(c.arg, 2);
            std::string cachePath = fields.size() > 0 ? fields[0] : "";
            std::string url = fields.size() > 1 ? fields[1] : "";
            DoCreateBrowser(c.id, cachePath, NormalizeUrl(url));
            break;
        }
        case CmdType::SetHostWindow: {
            // "{xid}" — a window owned by the managed side that the browser must
            // be created into. Accepted everywhere; only meaningful on X11, where
            // creating the browser under a hidden parent does not work reliably.
            std::string s = proto::Unescape(c.arg);
            CefWindowHandle hw = 0;
            if (!s.empty() && s != "0")
                hw = (CefWindowHandle)strtoull(s.c_str(), nullptr, 0);
            {
                std::lock_guard<std::mutex> lock(g_hostWindowsMutex);
                g_hostWindows[c.id] = hw;
            }
            DiagLog("DIAG: [C++] SetHostWindow id=%d hw=%llu", c.id, (unsigned long long)hw);
            break;
        }
        case CmdType::Navigate:
            {
                std::string navUrl = NormalizeUrl(proto::Unescape(c.arg));
                DiagLog("DIAG: [C++] PostTask DoNavigate id=%d url=%s", c.id, navUrl.c_str());
                if (navUrl.empty()) break;
                CefPostTask(TID_UI, base::BindOnce(&DoNavigate, c.id, navUrl));
            }
            break;
        case CmdType::Reload:
            CefPostTask(TID_UI, base::BindOnce(&DoReload, c.id));
            break;
        case CmdType::Stop:
            CefPostTask(TID_UI, base::BindOnce(&DoStop, c.id));
            break;
        case CmdType::CloseOne:
            CefPostTask(TID_UI, base::BindOnce(&DoCloseBrowser, c.id));
            break;
        case CmdType::Resize: {
            // "w|h" — apply directly to this browser's native window (latest wins)
            CefWindowHandle target = 0;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                auto it = g_browsers.find(c.id);
                if (it != g_browsers.end()) {
                    // coalesce via per-instance dirty handled inline; direct move is fine here
                    target = it->second.handler->GetBrowserWindowHandle();
                }
            }
            if (!target) break;
            auto sep2 = c.arg.find('|');
            if (sep2 == std::string::npos) break;
            int w = atoi(c.arg.substr(0, sep2).c_str());
            int h = atoi(c.arg.substr(sep2 + 1).c_str());
            if (w > 0 && h > 0) MoveNativeWindow(target, w, h);
            break;
        }
        case CmdType::EmbedDone: {
            CefWindowHandle target = 0;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                auto it = g_browsers.find(c.id);
                if (it != g_browsers.end()) target = it->second.handler->GetBrowserWindowHandle();
            }
            if (target) ShowNativeWindow(target, true);
            break;
        }
        case CmdType::Back:
            CefPostTask(TID_UI, base::BindOnce(&DoGoBack, c.id));
            break;
        case CmdType::Forward:
            CefPostTask(TID_UI, base::BindOnce(&DoGoForward, c.id));
            break;
        case CmdType::Zoom:
            CefPostTask(TID_UI, base::BindOnce(&DoZoom, c.id, atoi(c.arg.c_str())));
            break;
        case CmdType::Find:
            CefPostTask(TID_UI, base::BindOnce(&DoFind, c.id, c.arg));
            break;
        case CmdType::StopFind:
            CefPostTask(TID_UI, base::BindOnce(&DoStopFind, c.id));
            break;
        case CmdType::DevTools:
            CefPostTask(TID_UI, base::BindOnce(&DoShowDevTools, c.id));
            break;
        case CmdType::ExecJS:
            CefPostTask(TID_UI, base::BindOnce(&DoExecJS, c.id, c.arg));
            break;
        case CmdType::JsDialogResult:
            // Dialog answers must run on the UI thread: the CefJSDialogCallback
            // is thread-affine to the browser's UI thread.
            CefPostTask(TID_UI, base::BindOnce(&DoResolveJsDialog, c.id, c.arg));
            break;
        case CmdType::Quit:
            RequestShutdown();
            break;
        default: break;
    }
}

// ========================================================================
//  Entry point
// ========================================================================
static int RunMain(int argc, char** argv
#if defined(_WIN32)
                   , HINSTANCE hInstance
#endif
) {
    g_diagEnabled = GetEnv("CEF_DIAG") == "1";

#if defined(_WIN32)
    (void)argc;
    (void)argv;
#else
    g_argc = argc;
    g_argv = argv;
#endif

    // ---- Step 1: CEF subprocess detection ----
    // If --type= is present (CEF-spawned child process: renderer, GPU, etc.),
    // CefExecuteProcess handles the subprocess message loop and never returns.
    // Otherwise it returns -1 and we continue as the main browser process.
#if defined(_WIN32)
    CefMainArgs mainArgs(hInstance);
#else
    std::vector<std::string> cefArgStorage;
    cefArgStorage.reserve((size_t)argc);
    for (int i = 0; i < argc; i++)
        cefArgStorage.emplace_back(argv[i] ? argv[i] : "");
    std::vector<char*> cefArgPtrs;
    cefArgPtrs.reserve((size_t)argc + 1);
    for (auto& s : cefArgStorage)
        cefArgPtrs.push_back(s.data());
    cefArgPtrs.push_back(nullptr);
    CefMainArgs mainArgs(argc, cefArgPtrs.data());
#endif
    g_pumpApp = new PumpApp();

#if defined(_WIN32)
    // Windows sandbox: the very same sandbox_info pointer must be handed to the
    // subprocess entry point AND to CefInitialize. cef_sandbox.lib provides the
    // implementation (linked in CMakeLists.txt).
    CefScopedSandboxInfo sandboxInfo;
    void* sandboxInfoPtr = sandboxInfo.sandbox_info();
#else
    void* sandboxInfoPtr = nullptr;
#endif

#if defined(__linux__)
    // Must run before CefExecuteProcess/gtk_init: CEF/GDK may touch the
    // locale during platform initialization.
    if (!HasArg("--type="))
        gtk_disable_setlocale();
#endif
    int cefRet = CefExecuteProcess(mainArgs, g_pumpApp, sandboxInfoPtr);
    if (cefRet >= 0)
        return cefRet;

    // ---- Main process: strip CEF-injected --type=xxx (shouldn't be present) ----
    StripTypeFromCommandLine();

    // ---- Step 3: Read parameters (command line first, env var fallback) ----
    std::string pipeName;
    std::string url = "about:blank";
    int hostPid = 0;
    bool standalone = HasArg("--standalone");

    std::string cliPipe = GetArgValue("--cef-pipe");
    std::string cliUrl  = GetArgValue("--cef-url");
    std::string cliHost = GetArgValue("--cef-host-pid");

    if (!cliPipe.empty()) pipeName = cliPipe;
    if (!cliUrl.empty())  url      = NormalizeUrl(cliUrl);
    if (!cliHost.empty()) { hostPid = atoi(cliHost.c_str()); }

    if (pipeName.empty()) pipeName = GetEnv("CEF_PIPE");
    if (hostPid == 0) { std::string e = GetEnv("CEF_HOST_PID"); if (!e.empty()) { hostPid = atoi(e.c_str()); } }

    // ---- Step 4: Platform events ----
#if defined(_WIN32)
    g_browserReadyEvent  = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_shutdownEvent      = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_pumpWakeEvent      = CreateEventW(nullptr, FALSE, FALSE, nullptr);   // auto-reset
    if (!g_browserReadyEvent || !g_shutdownEvent || !g_pumpWakeEvent)
        return 1;
    RegisterHiddenClass();
#endif

    // ---- Step 5: CEF Initialize ----

    CefSettings settings;
    settings.multi_threaded_message_loop = false;
    // Event-driven pump: idle main thread blocks instead of polling
    // CefDoMessageLoopWork at 1kHz. Overridable via --cef-external-message-pump=false.
    settings.external_message_pump = true;
    // Sandbox is ON unless the managed side passes --cef-no-sandbox=false
    // (CefSettings default is already false, so nothing to set here).

    // Default profile lives under the per-user cache dir; the executable
    // directory is often read-only (e.g. Program Files) and would make CEF
    // fail to create its profile.
    CefString(&settings.cache_path) = GetDefaultCachePath();

    ApplyCefSettingsFromArgs(settings);

#if defined(__linux__)
    // Without a SUID-root chrome-sandbox helper Chromium refuses to start. Fall
    // back to --no-sandbox with a loud warning rather than showing a black
    // window; an explicit --cef-no-sandbox always wins.
    if (!settings.no_sandbox && !HasArg("--cef-no-sandbox")) {
        if (!LinuxSandboxAvailable()) {
            settings.no_sandbox = true;
            fprintf(stderr,
                    "WARNING: chrome-sandbox (SUID root) not found next to the "
                    "executable; starting without the CEF sandbox.\n");
            fflush(stderr);
        }
    }
#endif

#if defined(__linux__)
    // CEF on Linux needs the X11 backend for child-window embedding. GTK is
    // used for native dialogs and also forced to X11 for consistency.
    // (gtk_disable_setlocale() was called before CefExecuteProcess.)
    gdk_set_allowed_backends("x11");
    // GTK may consume/reorder its own arguments; use copies so our command-line
    // helpers (--standalone, --cef-pipe, ...) keep working.
    std::vector<std::string> gtkArgStorage;
    gtkArgStorage.reserve((size_t)argc);
    for (int i = 0; i < argc; i++)
        gtkArgStorage.emplace_back(argv[i] ? argv[i] : "");
    std::vector<char*> gtkArgv;
    gtkArgv.reserve((size_t)argc + 1);
    for (auto& s : gtkArgStorage)
        gtkArgv.push_back(s.data());
    gtkArgv.push_back(nullptr);
    int gtkArgc = argc;
    char** gtkArgvPtr = gtkArgv.data();
    if (!gtk_init_check(&gtkArgc, &gtkArgvPtr)) {
        DiagLog("DIAG: [C++] gtk_init_check FAILED (native dialogs disabled)");
    } else {
        g_gtkInitialized = true;
    }
    g_xdisplay = XOpenDisplay(nullptr);
    if (!g_xdisplay) {
        DiagLog("DIAG: [C++] XOpenDisplay FAILED (X11 embedding unavailable)");
    } else {
        g_wmDeleteAtom = XInternAtom(g_xdisplay, "WM_DELETE_WINDOW", False);
        XSetErrorHandler([](Display*, XErrorEvent* e) -> int {
            DiagLog("DIAG: [C++] X11 error code=%d request=%d", e->error_code, e->request_code);
            return 0;
        });
    }
#endif

    DiagLog("DIAG: [C++] Calling CefInitialize");
    if (!CefInitialize(mainArgs, settings, g_pumpApp, sandboxInfoPtr)) {
        DiagLog("DIAG: [C++] CefInitialize FAILED");
        return 1;
    }
    DiagLog("DIAG: [C++] CefInitialize OK");
    g_standaloneMode = standalone;

    // ---- Step 5b: Standalone mode auto-creates a single visible browser ----
    if (standalone) {
        g_handler = new BrowserHandler();
        g_handler->OnBrowserReady = [](CefWindowHandle window) {
            g_browserWindow = window;
            MarkBrowserReady();
        };
        g_handler->OnAddressChanged = [](const std::string& u) {
            SendEv("AddressChanged|" + DisplayUrl(u));
        };
        g_handler->OnDownloadEvent = [](const std::string& s) {
            SendEv(s);
        };

        g_hiddenParent = CreateHiddenParentWindow(true, "CEF Browser Test");
        if (!g_hiddenParent) {
            DiagLog("DIAG: [C++] standalone parent window FAILED");
            CefShutdown();
            return 1;
        }

        CefWindowInfo wi;
        wi.SetAsChild(g_hiddenParent, CefRect(0, 0, 1280, 800));
        CefBrowserSettings bs;
        DiagLog("DIAG: [C++] CreateBrowserSync url=%s", url.c_str());
        CefBrowserHost::CreateBrowserSync(wi, g_handler, url, bs, nullptr, nullptr);
        DiagLog("DIAG: [C++] CreateBrowserSync returned");

        // Wait for browser ready (50ms cap is a safety floor only).
        while (!IsBrowserReady()) {
            uint32_t waitMs = PumpWaitTimeoutMs();
            if (waitMs > 50) waitMs = 50;
            uint32_t genBefore = PumpGenSnapshot();
            auto result = WaitForPump(waitMs);
            if (result == PumpWaitResult::Shutdown) {
                CefShutdown();
                return 1;
            }
            if (result == PumpWaitResult::BrowserReady)
                break;

            bool timerDue = false;
            {
                std::lock_guard<std::mutex> lock(g_pumpMutex);
                if (g_pumpDueTick != 0 && NowMs() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
            }
            if (timerDue || PumpGenSnapshot() != genBefore ||
                result == PumpWaitResult::Wake || result == PumpWaitResult::Message)
                CefDoMessageLoopWork();
        }
        ResetBrowserReady();
        DiagLog("DIAG: [C++] Browser ready");
    }

    // ---- Step 6: Stdio server (skipped in standalone mode) ----
    // Protocol v3: every command carries a browser id.
    //   SetHostWindow|{id}|{xid}  (optional; must precede Create)
    //   Create|{id}|{cachePath}|{url}   Navigate|{id}|{url}   Reload|{id}
    //   Stop|{id}   Resize|{id}|{w}|{h}   EmbedDone|{id}   Close|{id}   Quit
    StdioServer* ps = nullptr;
    if (!standalone) {

        ps = new StdioServer();
        g_stdioServer = ps;

        ps->Start(
            [&](const std::string& cmd, const std::string& arg) {
                DiagLog("DIAG: [C++] onCommand cmd=%s arg=%s", cmd.c_str(), arg.c_str());
                // Helper: split leading "{id}|" off arg. Returns -1 on malformed input.
                auto splitId = [](const std::string& s, int& idOut, std::string& restOut) -> bool {
                    auto sp = s.find('|');
                    if (sp == std::string::npos) return false;
                    idOut = atoi(s.substr(0, sp).c_str());
                    restOut = s.substr(sp + 1);
                    return idOut > 0;
                };

                if (cmd == "Create") {
                    int id; std::string rest;
                    if (!splitId(arg, id, rest)) return;
                    // rest = "{cachePath}|{url}" (either may be empty)
                    PushCmd(CmdType::Create, id, rest);
                } else if (cmd == "Navigate") {
                    int id; std::string navUrl;
                    if (!splitId(arg, id, navUrl)) return;
                    // Escaped on the managed side; normalized after unescaping
                    // in ExecuteCmd so the escape sequences stay intact here.
                    if (navUrl.empty()) return;
                    PushCmd(CmdType::Navigate, id, navUrl);
                } else if (cmd == "SetHostWindow") {
                    // arg = "{id}|{xid}" — must be sent before Create so the
                    // browser is born inside the managed host window.
                    int id; std::string payload;
                    if (!splitId(arg, id, payload)) return;
                    PushCmd(CmdType::SetHostWindow, id, payload);
                } else if (cmd == "JsDialogResult") {
                    // arg = "{id}|{dialogId}|{accept}|{text}"
                    int id; std::string payload;
                    if (!splitId(arg, id, payload)) return;
                    PushCmd(CmdType::JsDialogResult, id, payload);
                } else if (cmd == "Reload" || cmd == "Stop" || cmd == "EmbedDone" ||
                           cmd == "Back" || cmd == "Forward" ||
                           cmd == "StopFind" || cmd == "DevTools") {
                    int id = atoi(arg.c_str());
                    if (id <= 0) return;
                    CmdType t = cmd == "Reload"   ? CmdType::Reload
                              : cmd == "Stop"     ? CmdType::Stop
                              : cmd == "EmbedDone"? CmdType::EmbedDone
                              : cmd == "Back"     ? CmdType::Back
                              : cmd == "Forward"  ? CmdType::Forward
                              : cmd == "StopFind" ? CmdType::StopFind
                                                  : CmdType::DevTools;
                    PushCmd(t, id);
                } else if (cmd == "Zoom" || cmd == "Find" || cmd == "ExecJS") {
                    // arg = "{id}|{payload}"
                    int id; std::string payload;
                    if (!splitId(arg, id, payload)) return;
                    CmdType t = cmd == "Zoom" ? CmdType::Zoom
                              : cmd == "Find" ? CmdType::Find
                                              : CmdType::ExecJS;
                    PushCmd(t, id, payload);
                } else if (cmd == "Resize") {
                    // arg = "{id}|{w}|{h}"
                    int id; std::string wh;
                    if (!splitId(arg, id, wh)) return;
                    PushCmd(CmdType::Resize, id, wh);
                } else if (cmd == "Close") {
                    int id = atoi(arg.c_str());
                    if (id <= 0) return;
                    PushCmd(CmdType::CloseOne, id);
                } else if (cmd == "Quit") {
                    PushCmd(CmdType::Quit);
                }
            },
            [&]() {
                RequestShutdown();   // host disconnected → shut down
            },
            [&]() {
                // Handshake: the managed client verifies the version before it
                // relies on escaped fields / JS dialog round-trips.
                SendEv("Hello|" + std::to_string(proto::kVersion));
            }
        );
    } else {
        if (g_browserWindow)
            ShowNativeWindow(g_browserWindow, true);
    }

    // ---- Step 7: Main message pump (event-driven external_message_pump) ----
    // Blocks until shutdown, CEF-scheduled work, IPC commands, or native input.
    // Idle = 0% CPU.
    bool running = true;
    while (running) {
        uint32_t genBefore = PumpGenSnapshot();
        uint32_t waitMs = PumpWaitTimeoutMs();

        auto result = WaitForPump(waitMs);
        if (result == PumpWaitResult::Shutdown)
            break;

        // IPC commands (PushCmd woke the pump)
        for (;;) {
            auto c = PopCmd();
            if (c.type == CmdType::Nop) break;
            ExecuteCmd(c);
        }

        // CEF work: due timer elapsed, or new work was scheduled, or native input
        bool timerDue = false;
        {
            std::lock_guard<std::mutex> lock(g_pumpMutex);
            if (g_pumpDueTick != 0 && NowMs() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
        }
        if (timerDue || PumpGenSnapshot() != genBefore ||
            result == PumpWaitResult::Wake || result == PumpWaitResult::Message)
            CefDoMessageLoopWork();
    }

    // ---- Step 8: Shutdown ----
    // Detach all instance callbacks so late CEF events don't touch dead pipes.
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        for (auto& [k, inst] : g_browsers) {
            if (inst.handler) {
                inst.handler->OnAddressChanged = nullptr;
                inst.handler->OnLoadErrorEvent = nullptr;
                inst.handler->OnLoadingStateChanged = nullptr;
                inst.handler->OnTitleChangedCB = nullptr;
                inst.handler->OnBeforePopupCB = nullptr;
                inst.handler->OnDownloadEvent = nullptr;
                inst.handler->OnBrowserReady = nullptr;
            }
        }
    }

    // Gracefully close every remaining browser FIRST (server still up for events),
    // then tear down stdio LAST — Stop() unblocks the reader via CancelIoEx.
    // Answer anything still waiting on a JS dialog: CloseBrowser(true) would
    // otherwise block until the renderer is answered.
    {
        std::lock_guard<std::mutex> lock(g_browsersMutex);
        for (auto& [k, inst] : g_browsers) {
            if (inst.handler)
                inst.handler->DismissAllDialogs();
        }
    }

    RequestCloseAllBrowsers();
    {
        uint64_t deadline = NowMs() + 5000;
        for (;;) {
            bool empty = false;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                empty = g_browsers.empty();
            }
            if (empty) break;
            uint64_t now = NowMs();
            if (now >= deadline) break;

            uint32_t waitMs = PumpWaitTimeoutMs();
            if (waitMs > (uint32_t)(deadline - now)) waitMs = (uint32_t)(deadline - now);
            if (waitMs > 50) waitMs = 50;
            uint32_t genBefore = PumpGenSnapshot();

            auto result = WaitForPump(waitMs);

            bool timerDue = false;
            {
                std::lock_guard<std::mutex> lock(g_pumpMutex);
                if (g_pumpDueTick != 0 && NowMs() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
            }
            if (timerDue || PumpGenSnapshot() != genBefore ||
                result == PumpWaitResult::Wake || result == PumpWaitResult::Message)
                CefDoMessageLoopWork();
        }
    }

    if (ps) {
        ps->Stop();
        delete ps;
        ps = nullptr;
        g_stdioServer = nullptr;
    }

    DestroyNativeWindow(g_hiddenParent);

    CefShutdown();

#if defined(__linux__)
    if (g_xdisplay) {
        XCloseDisplay(g_xdisplay);
        g_xdisplay = nullptr;
    }
#endif

    return 0;
}

#if defined(_WIN32)
int APIENTRY WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {
    return RunMain(__argc, __argv, hInstance);
}
#else
int main(int argc, char** argv) {
    XInitThreads();
    return RunMain(argc, argv);
}
#endif
