// CefBrowser.Native — CEF subprocess + shared browser host
// Architecture:
//   WinMain → CefExecuteProcess (subprocess) | CefInitialize (main process)
//   Main process: hosts MULTIPLE CefBrowser instances (Chrome-style shared model),
//   pumps messages, dispatches commands/events between C# (stdio) and CEF.
//   Shared across browsers: GPU process, network service, storage service.
//   Isolated per browser: renderer processes + optional RequestContext (own cache dir).
// IPC protocol v2 (pipe messages, all commands/events carry a browser id):
//   C#→Native: Create|{id}|{cachePath}|{url}, Navigate|{id}|{url}, Reload|{id},
//              Stop|{id}, Resize|{id}|{w}|{h}, EmbedDone|{id}, Close|{id}, Quit
//   Native→C#: Ready|{id}|{HWND_HEX}, AddressChanged|{id}|{url},
//              LoadError|{id}|{code}|{text}|{url}, NavState|{id}|{l}|{b}|{f},
//              TitleChanged|{id}|{title}, OpenPopup|{id}|{url}
//   The process exits when the last browser closes or Quit is received.

#include "browser_handler.h"
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
#include <Windows.h>
#include <shellapi.h>
#include <cstdlib>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <string>
#include <map>
#include <vector>
#include <queue>
#include <mutex>


// ---- Forward declarations ----
static bool HasArg(LPCWSTR arg);
static std::string GetArgValue(const std::string& key);
static std::string GetEnv(const char* name);
static void StripTypeFromCommandLine();

// ---- Globals ----
static CefRefPtr<BrowserHandler> g_handler;
static StdioServer* g_stdioServer = nullptr;
static HANDLE g_browserReadyEvent = nullptr;
static HANDLE g_shutdownEvent = nullptr;
static HANDLE g_browserClosedEvent = nullptr;
static HWND g_browserHwnd = nullptr;
static HWND g_hiddenParent = nullptr;

static const wchar_t kHiddenClass[] = L"CefHidden_{B3A0B1C2}";

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

// ---- External message pump state ----
// With external_message_pump=true CEF asks us (OnScheduleMessagePumpWork) to run
// CefDoMessageLoopWork within delay_ms. We park the main thread on kernel waits
// instead of the old Sleep(1) poll loop: idle CPU drops to ~0%.
static HANDLE g_pumpWakeEvent = nullptr;   // auto-reset, wakes pump for early deadlines / IPC
static std::mutex g_pumpMutex;
static ULONGLONG g_pumpDueTick = 0;        // absolute GetTickCount64 deadline; 0 = nothing pending
static uint32_t g_pumpGen = 0;             // bumped on every schedule request

class PumpApp : public CefApp, public CefBrowserProcessHandler {
public:
    PumpApp() = default;

    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }

    void OnScheduleMessagePumpWork(int64 delay_ms) override {
        if (delay_ms < 0) delay_ms = 0;
        ULONGLONG due = GetTickCount64() + (ULONGLONG)delay_ms;
        bool wake = false;
        {
            std::lock_guard<std::mutex> lock(g_pumpMutex);
            if (g_pumpDueTick == 0 || due < g_pumpDueTick) { g_pumpDueTick = due; wake = true; }
            g_pumpGen++;
        }
        if (wake && g_pumpWakeEvent) SetEvent(g_pumpWakeEvent);
    }
private:
    IMPLEMENT_REFCOUNTING(PumpApp);
};
static CefRefPtr<PumpApp> g_pumpApp;

static void WakePump() {
    if (g_pumpWakeEvent) SetEvent(g_pumpWakeEvent);
}

static uint32_t PumpGenSnapshot() {
    std::lock_guard<std::mutex> lock(g_pumpMutex);
    return g_pumpGen;
}

static DWORD PumpWaitTimeoutMs() {
    std::lock_guard<std::mutex> lock(g_pumpMutex);
    if (g_pumpDueTick == 0) return INFINITE;
    ULONGLONG now = GetTickCount64();
    return now >= g_pumpDueTick ? 0 : (DWORD)(g_pumpDueTick - now);
}

// ---- URL normalization (all browser logic in C++) ----
static std::string NormalizeUrl(const std::string& input) {
    std::string url = input;
    size_t s = url.find_first_not_of(" \t\r\n");
    if (s == std::string::npos) return {};
    size_t e = url.find_last_not_of(" \t\r\n");
    url = url.substr(s, e - s + 1);
    if (url.empty()) return {};
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
static std::string DisplayUrl(const std::string& url) {
    const char* prefixes[] = {"file:///", "file://", "local://app/", "local://", "about:"};
    for (auto p : prefixes) {
        if (url.find(p) == 0)
            return url.substr(strlen(p));
    }
    return url;
}

static std::string GetHost(const std::string& url) {
    size_t start = url.find("://");
    if (start == std::string::npos) start = 0;
    else start += 3;
    size_t end = url.find('/', start);
    if (end == std::string::npos) end = url.find('?', start);
    if (end == std::string::npos) end = url.find('#', start);
    if (end == std::string::npos) end = url.length();
    size_t port = url.find(':', start);
    if (port != std::string::npos && port < end) end = port;
    return url.substr(start, end - start);
}

// ---- Command queue (pipe thread -> main pump) ----
enum class CmdType { Create, Navigate, Reload, Stop, CloseOne, Resize, EmbedDone, Quit, None };
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
    if (g_cmdQueue.empty()) return {CmdType::None};
    auto c = g_cmdQueue.front();
    g_cmdQueue.pop();
    return c;
}

// ---- Browser instance registry (shared host, N browsers) ----
struct BrowserInstance {
    int id = 0;
    CefRefPtr<BrowserHandler> handler;
    HWND parentHwnd = nullptr;
    // Navigation host filter state (per browser, was global before multi-browser)
    std::string lastNavigateHost;
    ULONGLONG lastNavTick = 0;
};
static std::map<int, BrowserInstance> g_browsers;
static std::mutex g_browsersMutex;

static void SendEv(const std::string& line) {
    if (g_stdioServer) g_stdioServer->SendEvent(line);
}

static bool g_standaloneMode = false;

static bool HasArg(LPCWSTR arg) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return false;
    bool found = false;
    size_t argLen = wcslen(arg);
    for (int i = 1; i < argc; i++) {
        if (_wcsnicmp(argv[i], arg, argLen) == 0) { found = true; break; }
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
        sscanf_s(bc.c_str(), "%lx", &color);
        settings.background_color = (cef_color_t)color;
    }

    setStr(settings, "--cef-accept-language-list", settings.accept_language_list);
    setStr(settings, "--cef-cookieable-schemes-list", settings.cookieable_schemes_list);
    if ((b = setBool("--cef-cookieable-schemes-exclude-defaults")) >= 0) settings.cookieable_schemes_exclude_defaults = b;
}

static void StripTypeFromCommandLine() {
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

    DWORD style = WS_POPUP | WS_CLIPCHILDREN;   // hidden 1x1 parent; Resize moves the child
    HWND parent = CreateWindowExW(0, kHiddenClass, L"", style, 0, 0, 1, 1,
                                  nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!parent) { DiagLog("DIAG: [C++] Create id=%d parent window FAILED", id); SendEv("LoadError|" + std::to_string(id) + "|-100|CreateWindow failed|"); return; }

    auto handler = new BrowserHandler();
    handler->OnBrowserReady = [id](HWND hwnd) {
        char hwndHex[32];
        sprintf_s(hwndHex, "%I64X", (unsigned long long)(LONG_PTR)hwnd);
        SendEv("Ready|" + std::to_string(id) + "|" + hwndHex);
    };
    handler->OnBrowserClosed = [id]() {
        HWND parentToDestroy = nullptr;
        {
            std::lock_guard<std::mutex> lock(g_browsersMutex);
            auto it = g_browsers.find(id);
            if (it != g_browsers.end()) {
                parentToDestroy = it->second.parentHwnd;
                g_browsers.erase(it);
            }
        }
        if (parentToDestroy) DestroyWindow(parentToDestroy);
        // Host lifetime is owned by the C# side: it stays alive until an explicit
        // Quit or stdin EOF (parent process death). Do NOT exit just because the
        // last browser closed — the app may open a new tab afterwards.
    };
    handler->OnAddressChanged = [id](const std::string& u) {
        std::string host = GetHost(u);
        ULONGLONG nowTick = GetTickCount64();
        {
            std::lock_guard<std::mutex> lock(g_browsersMutex);
            auto it = g_browsers.find(id);
            if (it != g_browsers.end()) {
                auto& bi = it->second;
                if (!bi.lastNavigateHost.empty() && nowTick - bi.lastNavTick < 5000) {
                    if (host.empty()) return;
                    bool matches = (host.find(bi.lastNavigateHost) != std::string::npos ||
                                    bi.lastNavigateHost.find(host) != std::string::npos);
                    if (!matches) return;
                }
            }
        }
        SendEv("AddressChanged|" + std::to_string(id) + "|" + DisplayUrl(u));
    };
    handler->OnLoadErrorEvent = [id](const std::string& s) {
        SendEv("LoadError|" + std::to_string(id) + "|" + s);
    };
    handler->OnLoadingStateChanged = [id](bool isLoading, bool canGoBack, bool canGoForward) {
        SendEv("NavState|" + std::to_string(id) + "|" + std::string(isLoading ? "1" : "0") + "|" +
               std::string(canGoBack ? "1" : "0") + "|" + std::string(canGoForward ? "1" : "0"));
    };
    handler->OnTitleChangedCB = [id](const std::string& title) {
        SendEv("TitleChanged|" + std::to_string(id) + "|" + title);
    };
    handler->OnBeforePopupCB = [id](const std::string& popupUrl) {
        SendEv("OpenPopup|" + std::to_string(id) + "|" + popupUrl);
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
        g_browsers[id] = {id, handler, parent};
    }
    CefBrowserHost::CreateBrowserSync(wi, handler, url.empty() ? "about:blank" : url, bs, nullptr, requestCtx);
}

static void ExecuteCmd(const Cmd& c) {
    DiagLog("DIAG: [C++] ExecuteCmd type=%d id=%d arg=%s", (int)c.type, c.id, c.arg.c_str());
    switch (c.type) {
        case CmdType::Create: {
            // "cachePath|url" (either may be empty)
            auto sep = c.arg.find('|');
            std::string cachePath = sep != std::string::npos ? c.arg.substr(0, sep) : "";
            std::string url = sep != std::string::npos ? c.arg.substr(sep + 1) : c.arg;
            DoCreateBrowser(c.id, cachePath, NormalizeUrl(url));
            break;
        }
        case CmdType::Navigate:
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                auto it = g_browsers.find(c.id);
                if (it != g_browsers.end()) {
                    it->second.lastNavigateHost = GetHost(c.arg);
                    it->second.lastNavTick = GetTickCount64();
                }
            }
            DiagLog("DIAG: [C++] PostTask DoNavigate id=%d url=%s", c.id, c.arg.c_str());
            CefPostTask(TID_UI, base::BindOnce(&DoNavigate, c.id, c.arg));
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
            // "w|h" — apply directly to this browser's HWND (latest wins)
            HWND target = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                auto it = g_browsers.find(c.id);
                if (it != g_browsers.end()) {
                    // coalesce via per-instance dirty handled inline; direct move is fine here
                    target = it->second.handler->GetBrowserHwnd();
                }
            }
            if (!target) break;
            auto sep2 = c.arg.find('|');
            if (sep2 == std::string::npos) break;
            int w = atoi(c.arg.substr(0, sep2).c_str());
            int h = atoi(c.arg.substr(sep2 + 1).c_str());
            if (w > 0 && h > 0) MoveWindow(target, 0, 0, w, h, FALSE);
            break;
        }
        case CmdType::EmbedDone: {
            HWND target = nullptr;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                auto it = g_browsers.find(c.id);
                if (it != g_browsers.end()) target = it->second.handler->GetBrowserHwnd();
            }
            if (target) ShowWindow(target, SW_SHOW);
            break;
        }
        case CmdType::Quit:
            SetEvent(g_shutdownEvent);
            break;
        default: break;
    }
}

static ATOM RegisterHiddenClass() {
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = kHiddenClass;
    return RegisterClassExW(&wc);
}

// ========================================================================
//  WinMain
// ========================================================================
int APIENTRY WinMain(HINSTANCE hInstance, HINSTANCE, LPSTR, int) {

    g_diagEnabled = GetEnv("CEF_DIAG") == "1";

    // ---- Step 1: CEF subprocess detection ----
    // If --type= is present (CEF-spawned child process: renderer, GPU, etc.),
    // CefExecuteProcess handles the subprocess message loop and never returns.
    // Otherwise it returns -1 and we continue as the main browser process.
    CefMainArgs mainArgs(hInstance);
    g_pumpApp = new PumpApp();
    int cefRet = CefExecuteProcess(mainArgs, g_pumpApp, nullptr);
    if (cefRet >= 0)
        return cefRet;

    // ---- Main process: strip CEF-injected --type=xxx (shouldn't be present) ----
    StripTypeFromCommandLine();

    // ---- Step 3: Read parameters (command line first, env var fallback) ----
    std::string pipeName;
    std::string url = "about:blank";
    int hostPid = 0;
    bool standalone = HasArg(L"--standalone");

    std::string cliPipe = GetArgValue("--cef-pipe");
    std::string cliUrl  = GetArgValue("--cef-url");
    std::string cliHost = GetArgValue("--cef-host-pid");

    if (!cliPipe.empty()) pipeName = cliPipe;
    if (!cliUrl.empty())  url      = NormalizeUrl(cliUrl);
    if (!cliHost.empty()) { hostPid = atoi(cliHost.c_str()); }

    if (pipeName.empty()) pipeName = GetEnv("CEF_PIPE");
    if (hostPid == 0) { std::string e = GetEnv("CEF_HOST_PID"); if (!e.empty()) { hostPid = atoi(e.c_str()); } }

    // ---- Step 4: Events ----
    g_browserReadyEvent  = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_shutdownEvent      = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_browserClosedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_pumpWakeEvent      = CreateEventW(nullptr, FALSE, FALSE, nullptr);   // auto-reset
    if (!g_browserReadyEvent || !g_shutdownEvent || !g_browserClosedEvent || !g_pumpWakeEvent)
        return 1;

    RegisterHiddenClass();

    // ---- Step 5: CEF Initialize ----

    CefSettings settings;
    settings.multi_threaded_message_loop = false;
    // Event-driven pump: idle main thread blocks on kernel objects instead of
    // polling CefDoMessageLoopWork at 1kHz. Overridable via --cef-external-message-pump=false.
    settings.external_message_pump = true;
    settings.no_sandbox = true;

    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    char* sep = strrchr(exePath, '\\');
    if (sep) strcpy_s(sep + 1, MAX_PATH - (sep - exePath), "cache");
    CefString(&settings.cache_path) = exePath;

    ApplyCefSettingsFromArgs(settings);

    DiagLog("DIAG: [C++] Calling CefInitialize");
    if (!CefInitialize(mainArgs, settings, g_pumpApp, nullptr)) {
        DiagLog("DIAG: [C++] CefInitialize FAILED");
        return 1;
    }
    DiagLog("DIAG: [C++] CefInitialize OK");
    g_standaloneMode = standalone;

    // ---- Step 5b: Standalone mode auto-creates a single visible browser ----
    if (standalone) {
        g_handler = new BrowserHandler();
        g_handler->OnBrowserReady = [](HWND hwnd) {
            g_browserHwnd = hwnd;
            SetEvent(g_browserReadyEvent);
        };
        g_handler->OnAddressChanged = [](const std::string& u) {
            SendEv("AddressChanged|" + DisplayUrl(u));
        };

        DWORD style = WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_VISIBLE;
        g_hiddenParent = CreateWindowExW(0, kHiddenClass, L"CEF Browser Test",
            style, CW_USEDEFAULT, CW_USEDEFAULT, 1280, 800,
            nullptr, nullptr, hInstance, nullptr);

        CefWindowInfo wi;
        wi.SetAsChild(g_hiddenParent, CefRect(0, 0, 1280, 800));
        CefBrowserSettings bs;
        DiagLog("DIAG: [C++] CreateBrowserSync url=%s", url.c_str());
        CefBrowserHost::CreateBrowserSync(wi, g_handler, url, bs, nullptr, nullptr);
        DiagLog("DIAG: [C++] CreateBrowserSync returned");

        // Wait for browser ready: event-driven pump (50ms cap is a safety floor only).
        HANDLE waits[3] = { g_shutdownEvent, g_pumpWakeEvent, g_browserReadyEvent };
        while (WaitForSingleObject(g_browserReadyEvent, 0) != WAIT_OBJECT_0) {
            DWORD waitMs = PumpWaitTimeoutMs();
            if (waitMs > 50) waitMs = 50;
            uint32_t genBefore = PumpGenSnapshot();
            DWORD wr = MsgWaitForMultipleObjectsEx(3, waits, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (wr == WAIT_OBJECT_0) {           // shutdown requested
                CefShutdown();
                return 1;
            }
            if (wr == WAIT_OBJECT_0 + 2) break;  // browser ready

            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            bool timerDue = false;
            {
                std::lock_guard<std::mutex> lock(g_pumpMutex);
                if (g_pumpDueTick != 0 && GetTickCount64() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
            }
            if (timerDue || PumpGenSnapshot() != genBefore || wr == WAIT_OBJECT_0 + 1)
                CefDoMessageLoopWork();
        }
        DiagLog("DIAG: [C++] Browser ready");
    }

    // ---- Step 6: Stdio server (skipped in standalone mode) ----
    // Protocol v2: every command carries a browser id.
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
                    navUrl = NormalizeUrl(navUrl);
                    DiagLog("DIAG: [C++] Normalized url=%s", navUrl.c_str());
                    if (navUrl.empty()) return;
                    PushCmd(CmdType::Navigate, id, navUrl);
                } else if (cmd == "Reload" || cmd == "Stop" || cmd == "EmbedDone") {
                    int id = atoi(arg.c_str());
                    if (id <= 0) return;
                    CmdType t = cmd == "Reload" ? CmdType::Reload
                              : cmd == "Stop"   ? CmdType::Stop
                                                : CmdType::EmbedDone;
                    PushCmd(t, id);
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
                SetEvent(g_shutdownEvent);   // host disconnected → shut down
            },
            [&]() {
                // No handshake needed: Ready events are sent per browser after Create.
            }
        );
    } else {
        if (g_browserHwnd)
            ShowWindow(g_browserHwnd, SW_SHOW);
    }

    // ---- Step 7: Main message pump (event-driven external_message_pump) ----
    // Blocks on {shutdown, pump-wake} + Win32 input. Wakes for: CEF scheduled work,
    // IPC commands/resizes (WakePump), OS messages. Idle = 0% CPU.
    bool running = true;
    while (running) {
        uint32_t genBefore = PumpGenSnapshot();
        DWORD waitMs = PumpWaitTimeoutMs();

        HANDLE waits[2] = { g_shutdownEvent, g_pumpWakeEvent };
        DWORD wr = MsgWaitForMultipleObjectsEx(2, waits, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

        if (wr == WAIT_OBJECT_0)
            break;                                   // shutdown requested

        // Drain pending Win32 messages
        MSG msg;
        bool haveMsgs = false;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            haveMsgs = true;
            if (msg.message == WM_QUIT) { running = false; break; }
            if (standalone && msg.message == WM_CLOSE) {
                SetEvent(g_shutdownEvent);
                running = false;
                break;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        // IPC commands (PushCmd woke the pump)
        for (;;) {
            auto c = PopCmd();
            if (c.type == CmdType::None) break;
            ExecuteCmd(c);
        }

        // CEF work: due timer elapsed, or new work was scheduled, or OS input arrived
        bool timerDue = false;
        {
            std::lock_guard<std::mutex> lock(g_pumpMutex);
            if (g_pumpDueTick != 0 && GetTickCount64() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
        }
        if (timerDue || PumpGenSnapshot() != genBefore || haveMsgs || wr == WAIT_OBJECT_0 + 1)
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
                inst.handler->OnBrowserReady = nullptr;
            }
        }
    }

    // Gracefully close every remaining browser FIRST (server still up for events),
    // then tear down stdio LAST — Stop() unblocks the reader via CancelIoEx.
    RequestCloseAllBrowsers();
    {
        ULONGLONG deadline = GetTickCount64() + 5000;
        for (;;) {
            bool empty = false;
            {
                std::lock_guard<std::mutex> lock(g_browsersMutex);
                empty = g_browsers.empty();
            }
            if (empty) break;
            ULONGLONG now = GetTickCount64();
            if (now >= deadline) break;

            DWORD waitMs = PumpWaitTimeoutMs();
            if (waitMs > (DWORD)(deadline - now)) waitMs = (DWORD)(deadline - now);
            if (waitMs > 50) waitMs = 50;
            uint32_t genBefore = PumpGenSnapshot();

            MSG msg;
            bool haveMsgs = false;
            if (MsgWaitForMultipleObjectsEx(0, nullptr, waitMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
                == WAIT_OBJECT_0) {
                while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                    haveMsgs = true;
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                }
            }

            bool timerDue = false;
            {
                std::lock_guard<std::mutex> lock(g_pumpMutex);
                if (g_pumpDueTick != 0 && GetTickCount64() >= g_pumpDueTick) { g_pumpDueTick = 0; timerDue = true; }
            }
            if (timerDue || PumpGenSnapshot() != genBefore || haveMsgs)
                CefDoMessageLoopWork();
        }
    }

    if (g_hiddenParent) DestroyWindow(g_hiddenParent);

    CefShutdown();
    return 0;
}
