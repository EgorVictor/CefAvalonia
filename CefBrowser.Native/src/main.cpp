// CefBrowser.Native — CEF subprocess + browser host
// Architecture:
//   WinMain → CefExecuteProcess (subprocess) | CefInitialize (main process)
//   Main process: creates hidden CEF browser, hosts named pipe server,
//   pumps messages, dispatches commands/events between C# (pipe client) and CEF.
// IPC protocol (pipe messages):
//   C#→Native: Navigate|url, Reload, Stop, Close, EmbedDone, Resize|w|h
//   Native→C#: Ready|HWND_HEX, AddressChanged|url, LoadError|code|text|url,
//              NavState|isLoading|canGoBack|canGoForward, TitleChanged|title

#include "browser_handler.h"
#include "pipe_server.h"
#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_frame.h"
#include "include/internal/cef_types_wrappers.h"
#include "include/cef_task.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/base/cef_bind.h"
#include "include/base/cef_callback_helpers.h"
#include <Windows.h>
#include <shellapi.h>
#include <cstdlib>
#include <string>
#include <queue>
#include <mutex>


// ---- Forward declarations ----
static bool HasArg(LPCWSTR arg);
static std::string GetArgValue(const std::string& key);
static std::string GetEnv(const char* name);
static void StripTypeFromCommandLine();

// ---- Globals ----
static CefRefPtr<BrowserHandler> g_handler;
static PipeServer* g_pipeServer = nullptr;
static HANDLE g_browserReadyEvent = nullptr;
static HANDLE g_shutdownEvent = nullptr;
static HANDLE g_browserClosedEvent = nullptr;
static HWND g_browserHwnd = nullptr;
static HWND g_hiddenParent = nullptr;

static const wchar_t kHiddenClass[] = L"CefHidden_{B3A0B1C2}";

// ---- Navigation host tracking (filter stale OnAddressChange) ----
static std::string g_lastNavigateHost;
static ULONGLONG g_lastNavTick = 0;  // GetTickCount64, Win7+

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
enum class CmdType { Navigate, Reload, Stop, Close, None };
struct Cmd { CmdType type; std::string arg; };
static std::queue<Cmd> g_cmdQueue;
static std::mutex g_cmdMutex;

static void PushCmd(CmdType type, const std::string& arg = "") {
    std::lock_guard<std::mutex> lock(g_cmdMutex);
    g_cmdQueue.push({type, arg});
}

static Cmd PopCmd() {
    std::lock_guard<std::mutex> lock(g_cmdMutex);
    if (g_cmdQueue.empty()) return {CmdType::None};
    auto c = g_cmdQueue.front();
    g_cmdQueue.pop();
    return c;
}

// ---- Resize overwrite semantics ----
static std::mutex g_resizeMutex;
static bool g_resizeDirty = false;
static int g_resizeW = 0, g_resizeH = 0;

static void PushResize(int w, int h) {
    if (w <= 0 || h <= 0) return;
    std::lock_guard<std::mutex> lock(g_resizeMutex);
    g_resizeW = w;
    g_resizeH = h;
    g_resizeDirty = true;
}

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

static void DoNavigate(const std::string& url) {
    auto b = g_handler ? g_handler->GetBrowser() : nullptr;
    if (b) b->GetMainFrame()->LoadURL(url);
}

static void DoReload() {
    auto b = g_handler ? g_handler->GetBrowser() : nullptr;
    if (b) b->Reload();
}

static void DoStop() {
    auto b = g_handler ? g_handler->GetBrowser() : nullptr;
    if (b) b->StopLoad();
}

static void DoCloseBrowser() {
    auto b = g_handler ? g_handler->GetBrowser() : nullptr;
    if (b) b->GetHost()->CloseBrowser(true);
}

static void ExecuteCmd(const Cmd& c) {
    switch (c.type) {
        case CmdType::Navigate:
            g_lastNavigateHost = GetHost(c.arg);
            g_lastNavTick = GetTickCount64();
            CefPostTask(TID_UI, base::BindOnce(&DoNavigate, c.arg));
            break;
        case CmdType::Reload:
            CefPostTask(TID_UI, base::BindOnce(&DoReload));
            break;
        case CmdType::Stop:
            CefPostTask(TID_UI, base::BindOnce(&DoStop));
            break;
        case CmdType::Close:
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

    // ---- Step 1: CEF subprocess detection ----
    // If --type= is present (CEF-spawned child process: renderer, GPU, etc.),
    // CefExecuteProcess handles the subprocess message loop and never returns.
    // Otherwise it returns -1 and we continue as the main browser process.
    CefMainArgs mainArgs(hInstance);
    int cefRet = CefExecuteProcess(mainArgs, nullptr, nullptr);
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
    if (!g_browserReadyEvent || !g_shutdownEvent || !g_browserClosedEvent)
        return 1;

    RegisterHiddenClass();

    // ---- Step 5: CEF Initialize ----

    CefSettings settings;
    settings.multi_threaded_message_loop = false;
    settings.no_sandbox = true;

    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    char* sep = strrchr(exePath, '\\');
    if (sep) strcpy_s(sep + 1, MAX_PATH - (sep - exePath), "cache");
    CefString(&settings.cache_path) = exePath;

    ApplyCefSettingsFromArgs(settings);

    if (!CefInitialize(mainArgs, settings, nullptr, nullptr))
        return 1;

    // ---- Step 5: Create Browser ----
    g_handler = new BrowserHandler();

    g_handler->OnBrowserReady = [](HWND hwnd) {
        g_browserHwnd = hwnd;
        SetEvent(g_browserReadyEvent);
    };
    g_handler->OnBrowserClosed = []() {
        SetEvent(g_browserClosedEvent);
    };
    g_handler->OnAddressChanged = [](const std::string& u) {
        std::string host = GetHost(u);
        if (!g_lastNavigateHost.empty()) {
            if (GetTickCount64() - g_lastNavTick < 5000) {
                if (host.empty())
                    return;
                bool matches = (host.find(g_lastNavigateHost) != std::string::npos ||
                                g_lastNavigateHost.find(host) != std::string::npos);
                if (!matches)
                    return;
            }
        }
        if (g_pipeServer)
            g_pipeServer->SendEvent("AddressChanged|" + DisplayUrl(u));
    };
    g_handler->OnLoadErrorEvent = [](const std::string& s) {
        if (g_pipeServer)
            g_pipeServer->SendEvent("LoadError|" + s);
    };
    g_handler->OnLoadingStateChanged = [](bool isLoading, bool canGoBack, bool canGoForward) {
        if (g_pipeServer)
            g_pipeServer->SendEvent("NavState|" + std::string(isLoading ? "1" : "0") + "|" +
                                    std::string(canGoBack ? "1" : "0") + "|" +
                                    std::string(canGoForward ? "1" : "0"));
    };
    g_handler->OnTitleChangedCB = [](const std::string& title) {
        if (g_pipeServer)
            g_pipeServer->SendEvent("TitleChanged|" + title);
    };

    {
        DWORD style = standalone ? (WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN | WS_VISIBLE)
                                 : (WS_POPUP | WS_CLIPCHILDREN);
        int w = standalone ? 1280 : 1;
        int h = standalone ? 800 : 1;
        g_hiddenParent = CreateWindowExW(0, kHiddenClass,
            standalone ? L"CEF Browser Test" : L"",
            style, CW_USEDEFAULT, CW_USEDEFAULT, w, h,
            nullptr, nullptr, hInstance, nullptr);
    }

    CefWindowInfo wi;
    wi.SetAsChild(g_hiddenParent, CefRect(0, 0, 1280, 800));
    CefBrowserSettings bs;
    CefBrowserHost::CreateBrowserSync(wi, g_handler, url, bs, nullptr, nullptr);

    MSG msg;
    while (WaitForSingleObject(g_browserReadyEvent, 0) != WAIT_OBJECT_0) {
        if (WaitForSingleObject(g_shutdownEvent, 0) == WAIT_OBJECT_0) {
            CefShutdown();
            return 1;
        }
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        CefDoMessageLoopWork();
        Sleep(1);
    }

    // ---- Step 6: Pipe server (skipped in standalone mode) ----
    PipeServer* ps = nullptr;
    if (!pipeName.empty() && !standalone) {

        ps = new PipeServer(pipeName, hostPid);
        g_pipeServer = ps;

        ps->Start(
            [&](const std::string& cmd, const std::string& arg) {
                if (cmd == "Navigate") {
                    std::string navUrl = NormalizeUrl(arg);
                    if (navUrl.empty()) return;
                    PushCmd(CmdType::Navigate, navUrl);
                } else if (cmd == "Reload") {
                    PushCmd(CmdType::Reload);
                } else if (cmd == "Stop") {
                    PushCmd(CmdType::Stop);
                } else if (cmd == "Close") {
                    PushCmd(CmdType::Close);
                } else if (cmd == "EmbedDone") {
                    if (g_browserHwnd) {
                        ShowWindow(g_browserHwnd, SW_SHOW);
                        {
                            std::lock_guard<std::mutex> lock(g_resizeMutex);
                            if (g_resizeDirty && g_resizeW > 0 && g_resizeH > 0) {
                                MoveWindow(g_browserHwnd, 0, 0, g_resizeW, g_resizeH, TRUE);
                                g_resizeDirty = false;
                            }
                        }
                    }
                }
            },
            [](int w, int h) {
                PushResize(w, h);
            },
            [&]() {
                SetEvent(g_shutdownEvent);
            },
            [&]() {
                char hwndHex[32];
                sprintf_s(hwndHex, "%I64X", (unsigned long long)(LONG_PTR)g_browserHwnd);
                ps->SendEvent("Ready|" + std::string(hwndHex));
            }
        );
    } else {
        if (standalone && g_browserHwnd)
            ShowWindow(g_browserHwnd, SW_SHOW);
    }

    // ---- Step 7: Main message pump ----
    while (true) {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) break;
            if (standalone && msg.message == WM_CLOSE) {
                SetEvent(g_shutdownEvent);
                break;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        if (WaitForSingleObject(g_shutdownEvent, 0) == WAIT_OBJECT_0)
            break;

        {
            std::lock_guard<std::mutex> lock(g_resizeMutex);
            if (g_resizeDirty && g_browserHwnd) {
                MoveWindow(g_browserHwnd, 0, 0, g_resizeW, g_resizeH, FALSE);
                g_resizeDirty = false;
            }
        }

        for (;;) {
            auto c = PopCmd();
            if (c.type == CmdType::None) break;
            ExecuteCmd(c);
        }

        CefDoMessageLoopWork();
        Sleep(1);
    }

    // ---- Step 8: Shutdown ----
    if (g_handler) {
        g_handler->OnAddressChanged = nullptr;
        g_handler->OnLoadErrorEvent = nullptr;
        g_handler->OnLoadingStateChanged = nullptr;
        g_handler->OnTitleChangedCB = nullptr;
    }

    if (g_pipeServer) {
        g_pipeServer->Stop();
        delete g_pipeServer;
        g_pipeServer = nullptr;
    }

    CefPostTask(TID_UI, base::BindOnce(&DoCloseBrowser));
    {
        int pumps = 0;
        while (WaitForSingleObject(g_browserClosedEvent, 10) == WAIT_TIMEOUT && pumps < 500) {
            CefDoMessageLoopWork();
            pumps++;
        }
    }

    g_handler = nullptr;
    CefShutdown();

    if (g_hiddenParent) DestroyWindow(g_hiddenParent);

    return 0;
}
