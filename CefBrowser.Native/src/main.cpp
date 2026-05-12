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
#include <cstdio>
#include <string>
#include <queue>
#include <mutex>
#include <chrono>

// ---- Forward declarations ----
static void DebugLog(const char* msg);
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
static char g_tmp[256];

static const wchar_t kHiddenClass[] = L"CefHidden_{B3A0B1C2}";

// ---- Navigation host tracking (filter stale OnAddressChange) ----
// After user-initiated Navigate, OnAddressChange for OLD hosts is dropped
// within a 5-second window.  This prevents initial-page redirects that fire
// after the user navigated elsewhere from corrupting the address bar.
static std::string g_lastNavigateHost;
static std::chrono::steady_clock::time_point g_lastNavTime;

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
// Commands are forwarded to the CEF UI thread via CefPostTask.
// With the non-blocking PeekMessage pump calling CefDoMessageLoopWork
// every ~1ms, posted tasks are processed reliably.
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

// ---- Resize is NOT queued -- only the latest value is kept (overwrite) ----
static std::mutex g_resizeMutex;
static bool g_resizeDirty = false;
static int g_resizeW = 0, g_resizeH = 0;

static void PushResize(int w, int h) {
    if (w <= 0 || h <= 0) return;  // ignore invalid dimensions
    std::lock_guard<std::mutex> lock(g_resizeMutex);
    g_resizeW = w;
    g_resizeH = h;
    g_resizeDirty = true;
}

// ---- Debug logging (OutputDebugStringA, captured by DebugView) ----
static void DebugLog(const char* msg) {
    OutputDebugStringA("[Native] ");
    OutputDebugStringA(msg);
    OutputDebugStringA("\n");
}

// ---- Helper: check if arg exists in command line (case-insensitive prefix) ----
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

// ---- Helper: extract value from --key=value args ----
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

// ---- Helper: get env var ----
static std::string GetEnv(const char* name) {
    auto val = getenv(name);
    return val ? std::string(val) : std::string();
}

// ---- Strip all --type=xxx from the in-process command line buffer ----
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

// ---- UI-thread helpers for CEF callbacks (called via CefPostTask) ----
// NOTE: These do NOT take CefRefPtr args through BindOnce --
// CEF 109's base::BindOnce does not reliably handle CefRefPtr.
// Instead, access the browser from the global g_handler.
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
            g_lastNavTime = std::chrono::steady_clock::now();
            sprintf_s(g_tmp, "Navigate host='%s'", g_lastNavigateHost.c_str());
            DebugLog(g_tmp);
            CefPostTask(TID_UI, base::BindOnce(&DoNavigate, c.arg));
            break;
        case CmdType::Reload:
            CefPostTask(TID_UI, base::BindOnce(&DoReload));
            break;
        case CmdType::Stop:
            CefPostTask(TID_UI, base::BindOnce(&DoStop));
            break;
        case CmdType::Close:
            DebugLog("Close via command queue");
            SetEvent(g_shutdownEvent);
            break;
        default: break;
    }
}

// ---- Register hidden parent window class ----
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
    DebugLog("=== CefBrowser.Native started ===");

    // ---- Step 1: Are we main process or CEF subprocess? ----
    // Detect by checking for our custom --cef-pipe arg on the command line.
    // CEF constructs subprocess command lines internally, which do NOT
    // include our custom --cef-pipe / --cef-url / --cef-host-pid args.
    bool isMainProcess = HasArg(L"--cef-pipe");
    sprintf_s(g_tmp, "isMainProcess=%d", isMainProcess);
    DebugLog(g_tmp);

    if (!isMainProcess) {
        // CEF subprocess (GPU, Renderer, etc.) - let CefExecuteProcess handle
        DebugLog("CEF subprocess, delegating to CefExecuteProcess");
        CefMainArgs subArgs(hInstance);
        int ret = CefExecuteProcess(subArgs, nullptr, nullptr);
        sprintf_s(g_tmp, "CefExecuteProcess returned %d, exiting", ret);
        DebugLog(g_tmp);
        return ret >= 0 ? ret : 0;
    }

    // ---- Main process: strip CEF-injected --type=xxx ----
    // CEF's chrome_elf.dll may inject --type=gpu-process etc. into the
    // main process command line at DLL load time. Strip it so that
    // CefExecuteProcess correctly returns -1 (not a subprocess).
    StripTypeFromCommandLine();

    // ---- Step 2: Read parameters (command line first, env var fallback) ----
    std::string pipeName;
    std::string url = "https://www.bing.com";
    int hostPid = 0;
    bool standalone = HasArg(L"--standalone");

    // Try command line
    std::string cliPipe = GetArgValue("--cef-pipe");
    std::string cliUrl  = GetArgValue("--cef-url");
    std::string cliHost = GetArgValue("--cef-host-pid");

    if (!cliPipe.empty()) pipeName = cliPipe;
    if (!cliUrl.empty())  url      = cliUrl;
    if (!cliHost.empty()) { try { hostPid = std::stoi(cliHost); } catch (...) {} }

    // Fallback to env vars
    if (pipeName.empty()) pipeName = GetEnv("CEF_PIPE");
    if (url == "https://www.bing.com") { std::string e = GetEnv("CEF_URL"); if (!e.empty()) url = e; }
    if (hostPid == 0) { std::string e = GetEnv("CEF_HOST_PID"); if (!e.empty()) { try { hostPid = std::stoi(e); } catch (...) {} } }

    if (standalone) DebugLog("STANDALONE MODE - visible window, no pipe");

    sprintf_s(g_tmp, "url=%s pipe=%s hostPid=%d", url.c_str(), pipeName.c_str(), hostPid);
    DebugLog(g_tmp);

    // ---- Step 3: Events ----
    g_browserReadyEvent  = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_shutdownEvent      = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    g_browserClosedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_browserReadyEvent || !g_shutdownEvent || !g_browserClosedEvent) {
        DebugLog("CreateEvent failed");
        return 1;
    }

    RegisterHiddenClass();
    DebugLog("Window class registered");

    // ---- Step 4: CEF ExecuteProcess + Initialize ----
    CefMainArgs mainArgs(hInstance);

    int cefRet = CefExecuteProcess(mainArgs, nullptr, nullptr);
    sprintf_s(g_tmp, "CefExecuteProcess returned %d", cefRet);
    DebugLog(g_tmp);
    if (cefRet >= 0) {
        DebugLog("CEF subprocess path (should not happen for main process)");
        return cefRet;
    }

    // CEF settings
    CefSettings settings;
    settings.multi_threaded_message_loop = false;
    settings.no_sandbox = true;

    // Cache path alongside exe
    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    char* sep = strrchr(exePath, '\\');
    if (sep) strcpy_s(sep + 1, MAX_PATH - (sep - exePath), "cache");
    CefString(&settings.cache_path) = exePath;

    DebugLog("CefInitialize...");
    if (!CefInitialize(mainArgs, settings, nullptr, nullptr)) {
        DebugLog("CefInitialize FAILED");
        return 1;
    }
    DebugLog("CefInitialize OK");

    // ---- Step 5: Create Browser ----
    DebugLog("Creating BrowserHandler...");
    g_handler = new BrowserHandler();

    g_handler->OnBrowserReady = [](HWND hwnd) {
        sprintf_s(g_tmp, "OnBrowserReady hwnd=0x%IX", (size_t)hwnd);
        DebugLog(g_tmp);
        g_browserHwnd = hwnd;
        SetEvent(g_browserReadyEvent);
    };
    g_handler->OnBrowserClosed = []() {
        DebugLog("OnBrowserClosed");
        SetEvent(g_browserClosedEvent);
    };
    // Set callbacks BEFORE CreateBrowserSync so initial navigation
    // AddressChange / TitleChange / NavState events are captured.
    // g_pipeServer may be null at this point — callbacks safely no-op.
    g_handler->OnAddressChanged = [](const std::string& u) {
        if (!g_lastNavigateHost.empty()) {
            auto elapsed = std::chrono::steady_clock::now() - g_lastNavTime;
            if (elapsed < std::chrono::seconds(5)) {
                std::string host = GetHost(u);
                bool matches = (host.find(g_lastNavigateHost) != std::string::npos ||
                                g_lastNavigateHost.find(host) != std::string::npos);
                if (!matches) {
                    sprintf_s(g_tmp, "Dropping stale AddressChanged host='%s' (target='%s')",
                              host.c_str(), g_lastNavigateHost.c_str());
                    DebugLog(g_tmp);
                    return;
                }
            }
        }
        if (g_pipeServer)
            g_pipeServer->SendEvent("AddressChanged|" + u);
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
    sprintf_s(g_tmp, "hiddenParent=0x%IX standalone=%d", (size_t)g_hiddenParent, standalone);
    DebugLog(g_tmp);

    DebugLog("CreateBrowserSync...");
    CefWindowInfo wi;
    wi.SetAsChild(g_hiddenParent, CefRect(0, 0, 1280, 800));
    CefBrowserSettings bs;
    CefBrowserHost::CreateBrowserSync(wi, g_handler, url, bs, nullptr, nullptr);
    DebugLog("CreateBrowserSync returned");

    // Wait for OnAfterCreated (already fired during CreateBrowserSync,
    // but pump briefly in case it hasn't)
    DebugLog("Waiting for browser ready...");
    MSG msg;
    while (WaitForSingleObject(g_browserReadyEvent, 0) != WAIT_OBJECT_0) {
        if (WaitForSingleObject(g_shutdownEvent, 0) == WAIT_OBJECT_0) {
            DebugLog("Shutdown during browser init");
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
    DebugLog("Browser ready!");

    // ---- Step 6: Pipe server (skipped in standalone mode) ----
    PipeServer* ps = nullptr;
    if (!pipeName.empty() && !standalone) {
        sprintf_s(g_tmp, "Starting pipe server: %s", pipeName.c_str());
        DebugLog(g_tmp);

        ps = new PipeServer(pipeName, hostPid);
        g_pipeServer = ps;

        ps->Start(
            [&](const std::string& cmd, const std::string& arg) {
                sprintf_s(g_tmp, "Pipe cmd=%s arg=%s", cmd.c_str(), arg.c_str());
                DebugLog(g_tmp);
                if (cmd == "Navigate") {
                    std::string navUrl = arg;
                    if (navUrl.find("http://") != 0 && navUrl.find("https://") != 0)
                        navUrl = "https://" + navUrl;
                    PushCmd(CmdType::Navigate, navUrl);
                } else if (cmd == "Reload") {
                    PushCmd(CmdType::Reload);
                } else if (cmd == "Stop") {
                    PushCmd(CmdType::Stop);
                } else if (cmd == "Close") {
                    DebugLog("Close command");
                    PushCmd(CmdType::Close);
                } else if (cmd == "EmbedDone") {
                    if (g_browserHwnd) ShowWindow(g_browserHwnd, SW_SHOW);
                }
            },
            [](int w, int h) {
                PushResize(w, h);
            },
            [&]() {
                DebugLog("Pipe disconnected");
                SetEvent(g_shutdownEvent);
            },
            [&]() {
                DebugLog("Pipe connected!");
                char hwndHex[32];
                sprintf_s(hwndHex, "%I64X", (unsigned long long)(LONG_PTR)g_browserHwnd);
                ps->SendEvent("Ready|" + std::string(hwndHex));
            }
        );

        // Callbacks already set above (before CreateBrowserSync).
        // They check g_pipeServer internally, so only send when pipe is active.

        DebugLog("Pipe server running, awaiting connection...");
    } else {
        DebugLog("No pipe server (standalone or no pipe name)");
        if (standalone && g_browserHwnd) {
            ShowWindow(g_browserHwnd, SW_SHOW);
        }
    }

    // ---- Step 7: Main message pump ----
    // Non-blocking PeekMessage + Sleep(1) pattern.  Never blocks on
    // MsgWaitForMultipleObjects -- drain Windows messages, apply latest
    // resize (only the newest value is kept), drain command queue, then
    // always call CefDoMessageLoopWork.  Sleep(1) limits CPU when idle.
    DebugLog("Entering message pump");

    while (true) {
        // Drain all pending Windows messages (non-blocking)
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) { DebugLog("WM_QUIT received"); break; }
            if (standalone && msg.message == WM_CLOSE) {
                DebugLog("WM_CLOSE - initiating shutdown");
                SetEvent(g_shutdownEvent);
                break;
            }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        // Check shutdown
        if (WaitForSingleObject(g_shutdownEvent, 0) == WAIT_OBJECT_0) {
            DebugLog("Shutdown event received");
            break;
        }

        // Apply latest resize (overwrite semantics -- intermediate frames skipped)
        {
            std::lock_guard<std::mutex> lock(g_resizeMutex);
            if (g_resizeDirty && g_browserHwnd) {
                MoveWindow(g_browserHwnd, 0, 0, g_resizeW, g_resizeH, FALSE);
                g_resizeDirty = false;
            }
        }

        // Drain command queue
        for (;;) {
            auto c = PopCmd();
            if (c.type == CmdType::None) break;
            ExecuteCmd(c);
        }

        // CEF work
        CefDoMessageLoopWork();

        // Prevent 100 % CPU when idle
        Sleep(1);
    }

    // ---- Step 8: Shutdown ----
    DebugLog("Shutting down...");

    // Clear callbacks to prevent stale lambda captures
    if (g_handler) {
        g_handler->OnAddressChanged = nullptr;
        g_handler->OnLoadErrorEvent = nullptr;
        g_handler->OnLoadingStateChanged = nullptr;
        g_handler->OnTitleChangedCB = nullptr;
    }

    // Stop pipe server (joins pipe thread) and free
    if (g_pipeServer) {
        g_pipeServer->Stop();
        delete g_pipeServer;
        g_pipeServer = nullptr;
    }

    // Close browser on CEF UI thread.
    // Pump CefDoMessageLoopWork while waiting so the posted task is processed.
    CefPostTask(TID_UI, base::BindOnce(&DoCloseBrowser));
    {
        int pumps = 0;
        while (WaitForSingleObject(g_browserClosedEvent, 10) == WAIT_TIMEOUT && pumps < 500) {
            CefDoMessageLoopWork();
            pumps++;
        }
        sprintf_s(g_tmp, "CloseBrowser waited %dms (%d pumps)", pumps * 10, pumps);
        DebugLog(g_tmp);
    }

    g_handler = nullptr;
    CefShutdown();

    if (g_hiddenParent) DestroyWindow(g_hiddenParent);

    DebugLog("=== CefBrowser.Native exiting ===");
    return 0;
}
