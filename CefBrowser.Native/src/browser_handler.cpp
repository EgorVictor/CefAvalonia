#include "browser_handler.h"
#include "include/cef_browser.h"
#include "include/cef_frame.h"
#include "include/cef_download_item.h"
#include <shlobj.h>    // SHGetFolderPathW / CSIDL_DOWNLOADS
#include <shobjidl.h>  // IFileDialog (Vista+ common file dialogs)
#include <objbase.h>   // CoInitializeEx / CoCreateInstance
#include <commdlg.h>   // GetSaveFileNameW (Save As dialog)
#include <algorithm>

BrowserHandler::BrowserHandler()
{
}

void BrowserHandler::OnAfterCreated(CefRefPtr<CefBrowser> browser)
{
    browser_ = browser;
    browser_hwnd_ = browser->GetHost()->GetWindowHandle();

    if (OnBrowserReady)
        OnBrowserReady(browser_hwnd_);
}

void BrowserHandler::OnBeforeClose(CefRefPtr<CefBrowser> browser)
{
    browser_ = nullptr;
    browser_hwnd_ = nullptr;
    if (OnBrowserClosed)
        OnBrowserClosed();
}

void BrowserHandler::OnAddressChange(CefRefPtr<CefBrowser> browser,
                                     CefRefPtr<CefFrame> frame,
                                     const CefString& url)
{
    if (OnAddressChanged)
        OnAddressChanged(url.ToString());
}

void BrowserHandler::OnTitleChange(CefRefPtr<CefBrowser> browser,
                                   const CefString& title)
{
    if (OnTitleChangedCB)
        OnTitleChangedCB(title.ToString());
}

void BrowserHandler::OnLoadingStateChange(CefRefPtr<CefBrowser> browser,
                                           bool isLoading,
                                           bool canGoBack,
                                           bool canGoForward)
{
    if (OnLoadingStateChanged)
        OnLoadingStateChanged(isLoading, canGoBack, canGoForward);
}

bool BrowserHandler::OnBeforePopup(CefRefPtr<CefBrowser> browser,
                                    CefRefPtr<CefFrame> frame,
                                    const CefString& target_url,
                                    const CefString& target_frame_name,
                                    WindowOpenDisposition target_disposition,
                                    bool user_gesture,
                                    const CefPopupFeatures& popupFeatures,
                                    CefWindowInfo& windowInfo,
                                    CefRefPtr<CefClient>& client,
                                    CefBrowserSettings& settings,
                                    CefRefPtr<CefDictionaryValue>& extra_info,
                                    bool* no_javascript_access)
{
    if (OnBeforePopupCB && !target_url.empty())
        OnBeforePopupCB(target_url.ToString());
    return true; // cancel popup, we handle it in C#
}

void BrowserHandler::OnLoadError(CefRefPtr<CefBrowser> browser,
                                 CefRefPtr<CefFrame> frame,
                                 ErrorCode errorCode,
                                 const CefString& errorText,
                                 const CefString& failedUrl)
{
    if (!frame->IsMain()) return;
    if (OnLoadErrorEvent)
        OnLoadErrorEvent(std::to_string((int)errorCode) + "|" +
                         errorText.ToString() + "|" +
                         failedUrl.ToString());
}

// Shows the OS "Save As" dialog so the user can choose where to store the file.
// Returns the chosen path, or an empty string if the user cancels.
static std::wstring ShowSaveDialog(HWND owner, const std::wstring& suggestedName)
{
    wchar_t buf[4096] = { 0 };
    if (suggestedName.size() < 4095)
        wcscpy_s(buf, suggestedName.c_str());

    // Double-null terminated filter list: "All Files\0*.*\0".
    wchar_t filter[] = L"All Files\0*.*\0";
    OPENFILENAMEW ofn = { 0 };
    ofn.lStructSize = sizeof(ofn);
    // Owner must be the real top-level window; otherwise the modal dialog is
    // parented to the desktop and pops up BEHIND the Avalonia main window,
    // where the user can't see it and OnBeforeDownload blocks forever.
    ofn.hwndOwner = owner;
    ofn.lpstrFile = buf;
    ofn.nMaxFile = static_cast<DWORD>(sizeof(buf) / sizeof(wchar_t));
    ofn.lpstrFilter = filter;
    ofn.nFilterIndex = 1;
    ofn.Flags = OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR;
    ofn.lpstrDefExt = L"";

    if (GetSaveFileNameW(&ofn))
        return std::wstring(buf);
    return std::wstring();
}

void BrowserHandler::OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                                      CefRefPtr<CefDownloadItem> download_item,
                                      const CefString& suggested_name,
                                      CefRefPtr<CefBeforeDownloadCallback> callback)
{
    std::wstring suggested = suggested_name.ToWString();
    if (suggested.empty())
        suggested = L"download.bin";

    // Let the user pick the destination via the native Save As dialog. This is
    // the expected UX for an embedded browser, which has no built-in download
    // bar / path picker of its own.
    HWND owner = browser_hwnd_ ? GetAncestor(browser_hwnd_, GA_ROOT) : nullptr;
    std::wstring chosen = ShowSaveDialog(owner, suggested);
    if (chosen.empty()) {
        // User cancelled: not calling callback->Continue() cancels the download.
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadCanceled|" + std::to_string(browser_id_));
        return;
    }

    if (OnDownloadEvent) {
        OnDownloadEvent("DownloadStart|" + std::to_string(browser_id_) + "|" +
                        CefString(chosen).ToString());
    }

    // show_dialog=false: we already asked the user via our own dialog, so CEF
    // should just save to the chosen path without its own confirmation.
    callback->Continue(chosen, false);
}

void BrowserHandler::OnDownloadUpdated(CefRefPtr<CefBrowser> browser,
                                       CefRefPtr<CefDownloadItem> download_item,
                                       CefRefPtr<CefDownloadItemCallback> callback)
{
    if (!download_item.get())
        return;

    if (download_item->IsComplete()) {
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadComplete|" + std::to_string(browser_id_) + "|" +
                            download_item->GetFullPath().ToString());
    } else if (download_item->IsCanceled()) {
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadCanceled|" + std::to_string(browser_id_));
    } else {
        // Anything that is neither complete nor canceled is "in flight"
        // (including the initial pending state, where IsInProgress() may still
        // be false). Report byte counts so the host UI can show progress.
        int64 received = download_item->GetReceivedBytes();
        int64 total = download_item->GetTotalBytes();
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadProgress|" + std::to_string(browser_id_) + "|" +
                            std::to_string(received) + "|" + std::to_string(total));
    }
}

// ========================================================================
//  JavaScript dialogs (alert / confirm / prompt)
// ========================================================================

// State for the dynamically-created prompt dialog.
struct PromptDialogState {
    std::wstring message;
    std::wstring result;
    HWND edit = nullptr;
    bool ok = false;
    bool done = false;
};

static LRESULT CALLBACK PromptWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    auto* st = reinterpret_cast<PromptDialogState*>(GetWindowLongPtr(hwnd, GWLP_USERDATA));

    switch (msg) {
    case WM_CREATE: {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(lp);
        st = reinterpret_cast<PromptDialogState*>(cs->lpCreateParams);
        SetWindowLongPtr(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(st));

        HINSTANCE hInst = GetModuleHandleW(nullptr);
        CreateWindowExW(0, L"STATIC", st->message.c_str(),
                        WS_CHILD | WS_VISIBLE, 12, 12, 460, 40,
                        hwnd, nullptr, hInst, nullptr);
        st->edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", st->result.c_str(),
                                   WS_CHILD | WS_VISIBLE | ES_AUTOHSCROLL,
                                   12, 58, 460, 24, hwnd, nullptr, hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"OK",
                        WS_CHILD | WS_VISIBLE | BS_DEFPUSHBUTTON,
                        292, 96, 88, 28, hwnd, reinterpret_cast<HMENU>(IDOK), hInst, nullptr);
        CreateWindowExW(0, L"BUTTON", L"Cancel",
                        WS_CHILD | WS_VISIBLE,
                        384, 96, 88, 28, hwnd, reinterpret_cast<HMENU>(IDCANCEL), hInst, nullptr);
        SetFocus(st->edit);
        return 0;
    }
    case WM_COMMAND: {
        WORD id = LOWORD(wp);
        if (id == IDOK || id == IDCANCEL) {
            st->ok = (id == IDOK);
            if (st->ok) {
                wchar_t buf[2048] = { 0 };
                GetWindowTextW(st->edit, buf, 2048);
                st->result = buf;
            }
            st->done = true;
            DestroyWindow(hwnd);
            return 0;
        }
        break;
    }
    case WM_DESTROY:
        if (st) st->done = true;
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

// Shows a modal prompt() dialog built entirely in code (no .rc resources).
// Returns true if the user pressed OK.
static bool ShowPromptDialog(HWND parent, const std::wstring& message,
                             const std::wstring& defaultValue,
                             std::wstring& outValue)
{
    static const wchar_t kCls[] = L"CefPromptDlg_{7F3A91C4}";
    static bool registered = false;
    if (!registered) {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = PromptWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = kCls;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        registered = RegisterClassExW(&wc) != 0;
    }

    PromptDialogState st;
    st.message = message;
    st.result = defaultValue;

    const int w = 500, h = 150;
    int x = CW_USEDEFAULT, y = CW_USEDEFAULT;
    RECT pr = {};
    if (parent && GetWindowRect(parent, &pr)) {
        x = pr.left + ((pr.right - pr.left) - w) / 2;
        y = pr.top + ((pr.bottom - pr.top) - h) / 2;
    }

    HWND hwnd = CreateWindowExW(WS_EX_DLGMODALFRAME, kCls, L"JavaScript Prompt",
        WS_POPUPWINDOW | WS_CAPTION | WS_VISIBLE, x, y, w, h,
        parent, nullptr, GetModuleHandleW(nullptr), &st);
    if (!hwnd)
        return false;

    if (parent) EnableWindow(parent, FALSE);

    // Local modal pump (same pattern MessageBox uses; GetMessage blocks here).
    while (!st.done) {
        MSG msg;
        if (!GetMessageW(&msg, nullptr, 0, 0))
            break;
        if (msg.message == WM_QUIT) {
            PostQuitMessage(static_cast<int>(msg.wParam));
            break;
        }
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    if (parent) EnableWindow(parent, TRUE);
    if (st.ok)
        outValue = st.result;
    return st.ok;
}

// Parent window used to keep dialogs on top of the browser window.
static HWND GetDialogOwner(CefRefPtr<CefBrowser> browser)
{
    if (browser.get() && browser->GetHost())
        return browser->GetHost()->GetWindowHandle();
    return nullptr;
}

bool BrowserHandler::OnJSDialog(CefRefPtr<CefBrowser> browser,
                                const CefString& origin_url,
                                JSDialogType dialog_type,
                                const CefString& message_text,
                                const CefString& default_prompt_text,
                                CefRefPtr<CefJSDialogCallback> callback,
                                bool& suppress_message)
{
    HWND owner = GetDialogOwner(browser);
    std::wstring msg = message_text.ToWString();

    switch (dialog_type) {
    case JSDIALOGTYPE_ALERT:
        MessageBoxW(owner, msg.c_str(), L"Alert", MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND);
        callback->Continue(true, CefString());
        return true;

    case JSDIALOGTYPE_CONFIRM: {
        int r = MessageBoxW(owner, msg.c_str(), L"Confirm",
                            MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND);
        callback->Continue(r == IDYES, CefString());
        return true;
    }

    case JSDIALOGTYPE_PROMPT: {
        std::wstring value;
        bool ok = ShowPromptDialog(owner, msg, default_prompt_text.ToWString(), value);
        callback->Continue(ok, CefString(value));
        return true;
    }
    default:
        break;
    }

    return false;   // use CEF's default implementation
}

bool BrowserHandler::OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                                          const CefString& message_text,
                                          bool is_reload,
                                          CefRefPtr<CefJSDialogCallback> callback)
{
    // Returning false uses CEF's default handling (allow the unload). We never
    // block navigation with a modal here: a stuck beforeunload dialog would
    // prevent tabs from closing.
    return false;
}

// ========================================================================
//  File dialogs (<input type="file">)
// ========================================================================

// Shows the Vista+ common item dialog. |filterSpecs| entries are
// {description, pattern}. Returns the selected paths (empty if cancelled).
static std::vector<std::wstring> ShowCommonFileDialog(
    HWND owner,
    bool isSave,
    bool multiSelect,
    bool folderPick,
    const std::wstring& title,
    const std::wstring& defaultPath,
    const std::vector<std::pair<std::wstring, std::wstring>>& filterSpecs)
{
    std::vector<std::wstring> result;

    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    bool coInitialized = SUCCEEDED(hrCo);

    IFileDialog* dlg = nullptr;
    HRESULT hr = CoCreateInstance(isSave ? CLSID_FileSaveDialog : CLSID_FileOpenDialog,
                                  nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dlg));
    if (SUCCEEDED(hr) && dlg) {
        DWORD opts = 0;
        dlg->GetOptions(&opts);
        opts |= FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
        if (!isSave)   opts |= FOS_FILEMUSTEXIST;
        if (isSave)    opts |= FOS_OVERWRITEPROMPT;
        if (multiSelect) opts |= FOS_ALLOWMULTISELECT;
        if (folderPick)  opts |= FOS_PICKFOLDERS;
        dlg->SetOptions(opts);

        if (!title.empty())
            dlg->SetTitle(title.c_str());

        // Seed the dialog with the suggested directory + file name.
        if (!defaultPath.empty()) {
            DWORD attr = GetFileAttributesW(defaultPath.c_str());
            std::wstring dir, name;
            if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) {
                dir = defaultPath;
            } else {
                size_t slash = defaultPath.find_last_of(L"\\/");
                if (slash != std::wstring::npos) {
                    dir = defaultPath.substr(0, slash);
                    name = defaultPath.substr(slash + 1);
                } else {
                    name = defaultPath;
                }
            }
            if (!dir.empty()) {
                IShellItem* si = nullptr;
                if (SUCCEEDED(SHCreateItemFromParsingName(dir.c_str(), nullptr, IID_PPV_ARGS(&si)))) {
                    dlg->SetFolder(si);
                    si->Release();
                }
            }
            if (!name.empty())
                dlg->SetFileName(name.c_str());
        }

        if (!filterSpecs.empty()) {
            std::vector<COMDLG_FILTERSPEC> specs;
            specs.reserve(filterSpecs.size());
            for (const auto& fs : filterSpecs)
                specs.push_back({ fs.first.c_str(), fs.second.c_str() });
            dlg->SetFileTypes(static_cast<UINT>(specs.size()), specs.data());
            dlg->SetFileTypeIndex(1);
        }

        if (SUCCEEDED(dlg->Show(owner))) {
            if (multiSelect && !folderPick && !isSave) {
                IFileOpenDialog* fod = nullptr;
                if (SUCCEEDED(dlg->QueryInterface(IID_PPV_ARGS(&fod)))) {
                    IShellItemArray* arr = nullptr;
                    if (SUCCEEDED(fod->GetResults(&arr))) {
                        DWORD count = 0;
                        arr->GetCount(&count);
                        for (DWORD i = 0; i < count; ++i) {
                            IShellItem* item = nullptr;
                            if (SUCCEEDED(arr->GetItemAt(i, &item))) {
                                PWSTR psz = nullptr;
                                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &psz))) {
                                    result.emplace_back(psz);
                                    CoTaskMemFree(psz);
                                }
                                item->Release();
                            }
                        }
                        arr->Release();
                    }
                    fod->Release();
                }
            } else {
                IShellItem* item = nullptr;
                if (SUCCEEDED(dlg->GetResult(&item))) {
                    PWSTR psz = nullptr;
                    if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &psz))) {
                        result.emplace_back(psz);
                        CoTaskMemFree(psz);
                    }
                    item->Release();
                }
            }
        }
        dlg->Release();
    }

    if (coInitialized)
        CoUninitialize();

    return result;
}

bool BrowserHandler::OnFileDialog(CefRefPtr<CefBrowser> browser,
                                  FileDialogMode mode,
                                  const CefString& title,
                                  const CefString& default_file_path,
                                  const std::vector<CefString>& accept_filters,
                                  CefRefPtr<CefFileDialogCallback> callback)
{
    // In CEF 109 cef_file_dialog_mode_t is a plain 4-value enum (no flag bits),
    // so the mode can be compared directly.
    bool isSave      = (mode == FILE_DIALOG_SAVE);
    bool folderPick  = (mode == FILE_DIALOG_OPEN_FOLDER);
    bool multiSelect = (mode == FILE_DIALOG_OPEN_MULTIPLE);

    // Translate CEF accept filters ("Image Types|.png;.jpg", ".txt", "image/*")
    // into COMDLG_FILTERSPEC entries.
    std::vector<std::pair<std::wstring, std::wstring>> specs;
    for (const auto& f : accept_filters) {
        std::wstring s = f.ToWString();
        if (s.empty())
            continue;
        size_t bar = s.find(L'|');
        if (bar != std::wstring::npos) {
            std::wstring desc = s.substr(0, bar);
            std::wstring pat  = s.substr(bar + 1);
            if (!desc.empty() && !pat.empty())
                specs.push_back({ desc, pat });
        } else if (s[0] == L'.') {
            specs.push_back({ s + L" Files", L"*" + s });
        }
        // MIME types like "image/*" carry no usable extension, so they are
        // skipped; the "All Files" fallback below keeps the dialog usable.
    }
    specs.push_back({ L"All Files", L"*.*" });

    std::wstring titleW = title.ToWString();
    if (titleW.empty())
        titleW = isSave ? L"Save As" : (folderPick ? L"Select Folder" : L"Open");

    std::vector<std::wstring> paths = ShowCommonFileDialog(
        GetDialogOwner(browser), isSave, multiSelect, folderPick,
        titleW, default_file_path.ToWString(), specs);

    if (paths.empty()) {
        callback->Cancel();
    } else {
        std::vector<CefString> cefPaths;
        cefPaths.reserve(paths.size());
        for (const auto& p : paths)
            cefPaths.push_back(CefString(p));
        callback->Continue(cefPaths);
    }

    return true;   // custom dialog used
}
