#include "browser_handler.h"
#include "protocol.h"
#include "include/cef_browser.h"
#include "include/cef_frame.h"
#include "include/cef_download_item.h"
#include <algorithm>
#include <atomic>
#include <string>
#include <utility>
#include <vector>

#if defined(_WIN32)
#include <shlobj.h>    // SHGetFolderPathW / CSIDL_DOWNLOADS
#include <shobjidl.h>  // IFileDialog (Vista+ common file dialogs)
#include <objbase.h>   // CoInitializeEx / CoCreateInstance
#include <commdlg.h>   // GetSaveFileNameW (Save As dialog)
#elif defined(__linux__)
#include <gtk/gtk.h>
#include <glib.h>
#include <cstring>
extern bool g_gtkInitialized;
#endif

BrowserHandler::BrowserHandler()
{
}

void BrowserHandler::OnAfterCreated(CefRefPtr<CefBrowser> browser)
{
    browser_ = browser;
    browser_window_ = browser->GetHost()->GetWindowHandle();

    if (OnBrowserReady)
        OnBrowserReady(browser_window_);
}

void BrowserHandler::OnBeforeClose(CefRefPtr<CefBrowser> browser)
{
    browser_ = nullptr;
    browser_window_ = 0;
    DismissAllDialogs();
    if (OnBrowserClosed)
        OnBrowserClosed();
}

void BrowserHandler::OnAddressChange(CefRefPtr<CefBrowser> browser,
                                     CefRefPtr<CefFrame> frame,
                                     const CefString& url)
{
    // Only top-level navigation belongs in the address bar. Sub-frame loads
    // (ads, iframes) are ignored instead of trying to guess by host name.
    if (!frame || !frame->IsMain())
        return;

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
    // Report every popup, including window.open() with an empty URL (e.g.
    // about:blank + document.write), so the managed host can open a blank tab.
    if (OnBeforePopupCB)
        OnBeforePopupCB(target_url.empty() ? "about:blank" : target_url.ToString());
    return true; // cancel popup, we handle it in C#
}

void BrowserHandler::OnLoadError(CefRefPtr<CefBrowser> browser,
                                 CefRefPtr<CefFrame> frame,
                                 ErrorCode errorCode,
                                 const CefString& errorText,
                                 const CefString& failedUrl)
{
    if (!frame || !frame->IsMain()) return;
    if (OnLoadErrorEvent)
        OnLoadErrorEvent(std::to_string((int)errorCode) + "|" +
                         proto::Escape(errorText.ToString()) + "|" +
                         proto::Escape(failedUrl.ToString()));
}

#if defined(_WIN32)
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
#elif defined(__linux__)
static std::string ShowSaveDialog(const std::string& suggestedName)
{
    GtkWidget* dialog = gtk_file_chooser_dialog_new(
        "Save As", nullptr, GTK_FILE_CHOOSER_ACTION_SAVE,
        "_Cancel", GTK_RESPONSE_CANCEL,
        "_Save", GTK_RESPONSE_ACCEPT,
        nullptr);
    gtk_file_chooser_set_do_overwrite_confirmation(GTK_FILE_CHOOSER(dialog), TRUE);
    if (!suggestedName.empty())
        gtk_file_chooser_set_current_name(GTK_FILE_CHOOSER(dialog), suggestedName.c_str());

    std::string chosen;
    if (gtk_dialog_run(GTK_DIALOG(dialog)) == GTK_RESPONSE_ACCEPT) {
        char* filename = gtk_file_chooser_get_filename(GTK_FILE_CHOOSER(dialog));
        if (filename) {
            chosen = filename;
            g_free(filename);
        }
    }
    gtk_widget_destroy(dialog);
    return chosen;
}
#endif

void BrowserHandler::OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                                      CefRefPtr<CefDownloadItem> download_item,
                                      const CefString& suggested_name,
                                      CefRefPtr<CefBeforeDownloadCallback> callback)
{
#if defined(_WIN32)
    std::wstring suggested = suggested_name.ToWString();
    if (suggested.empty())
        suggested = L"download.bin";

    // Let the user pick the destination via the native Save As dialog. This is
    // the expected UX for an embedded browser, which has no built-in download
    // bar / path picker of its own.
    HWND owner = browser_window_ ? GetAncestor(browser_window_, GA_ROOT) : nullptr;
    std::wstring chosen = ShowSaveDialog(owner, suggested);
    if (chosen.empty()) {
        // User cancelled: not calling callback->Continue() cancels the download.
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadCanceled|" + std::to_string(browser_id_));
        return;
    }

    if (OnDownloadEvent) {
        OnDownloadEvent("DownloadStart|" + std::to_string(browser_id_) + "|" +
                        proto::Escape(CefString(chosen).ToString()));
    }

    // show_dialog=false: we already asked the user via our own dialog, so CEF
    // should just save to the chosen path without its own confirmation.
    callback->Continue(chosen, false);
#elif defined(__linux__)
    std::string suggested = suggested_name.ToString();
    if (suggested.empty())
        suggested = "download.bin";

    std::string chosen = ShowSaveDialog(suggested);
    if (chosen.empty()) {
        if (OnDownloadEvent)
            OnDownloadEvent("DownloadCanceled|" + std::to_string(browser_id_));
        return;
    }

    if (OnDownloadEvent) {
        OnDownloadEvent("DownloadStart|" + std::to_string(browser_id_) + "|" +
                        proto::Escape(CefString(chosen).ToString()));
    }

    callback->Continue(CefString(chosen), false);
#endif
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
                            proto::Escape(download_item->GetFullPath().ToString()));
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

#if defined(_WIN32)
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
#elif defined(__linux__)
static bool ShowPromptDialog(const std::string& message,
                             const std::string& defaultValue,
                             std::string& outValue)
{
    GtkWidget* dialog = gtk_dialog_new_with_buttons(
        "JavaScript Prompt", nullptr, GTK_DIALOG_MODAL,
        "_Cancel", GTK_RESPONSE_CANCEL,
        "_OK", GTK_RESPONSE_OK,
        nullptr);
    GtkWidget* area = gtk_dialog_get_content_area(GTK_DIALOG(dialog));
    GtkWidget* label = gtk_label_new(message.c_str());
    gtk_label_set_line_wrap(GTK_LABEL(label), TRUE);
    gtk_label_set_max_width_chars(GTK_LABEL(label), 60);
    gtk_widget_set_halign(label, GTK_ALIGN_START);
    gtk_box_pack_start(GTK_BOX(area), label, FALSE, FALSE, 8);
    GtkWidget* entry = gtk_entry_new();
    gtk_entry_set_text(GTK_ENTRY(entry), defaultValue.c_str());
    gtk_entry_set_activates_default(GTK_ENTRY(entry), TRUE);
    gtk_box_pack_start(GTK_BOX(area), entry, FALSE, FALSE, 8);
    gtk_dialog_set_default_response(GTK_DIALOG(dialog), GTK_RESPONSE_OK);
    gtk_widget_show_all(dialog);

    bool ok = gtk_dialog_run(GTK_DIALOG(dialog)) == GTK_RESPONSE_OK;
    if (ok) {
        const char* text = gtk_entry_get_text(GTK_ENTRY(entry));
        outValue = text ? text : "";
    }
    gtk_widget_destroy(dialog);
    return ok;
}
#endif

bool BrowserHandler::OnJSDialog(CefRefPtr<CefBrowser> browser,
                                const CefString& origin_url,
                                JSDialogType dialog_type,
                                const CefString& message_text,
                                const CefString& default_prompt_text,
                                CefRefPtr<CefJSDialogCallback> callback,
                                bool& suppress_message)
{
    if (OnJsDialog) {
        const char* typeName = "confirm";
        switch (dialog_type) {
        case JSDIALOGTYPE_ALERT:  typeName = "alert";  break;
        case JSDIALOGTYPE_CONFIRM: typeName = "confirm"; break;
        case JSDIALOGTYPE_PROMPT: typeName = "prompt"; break;
        default: break;
        }

        int dialogId = NextDialogId();
        {
            std::lock_guard<std::mutex> lock(dialog_mutex_);
            pending_dialogs_[dialogId] = PendingDialog{callback};
        }

        suppress_message = false;
        OnJsDialog(dialogId, typeName, message_text.ToString(),
                   default_prompt_text.ToString());
        return true;   // answered asynchronously by the managed host
    }

    // No managed host attached (standalone mode): use the native dialogs below.
#if defined(_WIN32)
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
#elif defined(__linux__)
    if (!g_gtkInitialized)
        return false;

    std::string msg = message_text.ToString();
    switch (dialog_type) {
    case JSDIALOGTYPE_ALERT: {
        GtkWidget* dialog = gtk_message_dialog_new(
            nullptr, GTK_DIALOG_MODAL, GTK_MESSAGE_INFO, GTK_BUTTONS_OK, "%s", msg.c_str());
        gtk_window_set_title(GTK_WINDOW(dialog), "Alert");
        gtk_dialog_run(GTK_DIALOG(dialog));
        gtk_widget_destroy(dialog);
        callback->Continue(true, CefString());
        return true;
    }
    case JSDIALOGTYPE_CONFIRM: {
        GtkWidget* dialog = gtk_message_dialog_new(
            nullptr, GTK_DIALOG_MODAL, GTK_MESSAGE_QUESTION, GTK_BUTTONS_YES_NO, "%s", msg.c_str());
        gtk_window_set_title(GTK_WINDOW(dialog), "Confirm");
        int r = gtk_dialog_run(GTK_DIALOG(dialog));
        gtk_widget_destroy(dialog);
        callback->Continue(r == GTK_RESPONSE_YES, CefString());
        return true;
    }
    case JSDIALOGTYPE_PROMPT: {
        std::string value;
        bool ok = ShowPromptDialog(msg, default_prompt_text.ToString(), value);
        callback->Continue(ok, CefString(value));
        return true;
    }
    default:
        break;
    }
#endif

    return false;   // use CEF's default implementation
}

bool BrowserHandler::OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                                          const CefString& message_text,
                                          bool is_reload,
                                          CefRefPtr<CefJSDialogCallback> callback)
{
    (void)browser;
    (void)is_reload;

    if (OnJsDialog) {
        int dialogId = NextDialogId();
        {
            std::lock_guard<std::mutex> lock(dialog_mutex_);
            pending_dialogs_[dialogId] = PendingDialog{callback};
        }

        OnJsDialog(dialogId, "beforeunload", message_text.ToString(), "");
        return true;
    }

    // Returning false uses CEF's default handling (allow the unload). We never
    // block navigation with a modal here: a stuck beforeunload dialog would
    // prevent tabs from closing.
    return false;
}

int BrowserHandler::NextDialogId()
{
    static std::atomic<int> next{0};
    return ++next;
}

bool BrowserHandler::ResolveJsDialog(int dialog_id, bool accept, const std::string& text)
{
    PendingDialog pending;
    {
        std::lock_guard<std::mutex> lock(dialog_mutex_);
        auto it = pending_dialogs_.find(dialog_id);
        if (it == pending_dialogs_.end())
            return false;

        pending = it->second;
        pending_dialogs_.erase(it);
    }

    if (pending.callback)
        pending.callback->Continue(accept, CefString(text));
    return true;
}

void BrowserHandler::DismissAllDialogs()
{
    std::map<int, PendingDialog> pending;
    {
        std::lock_guard<std::mutex> lock(dialog_mutex_);
        pending.swap(pending_dialogs_);
    }

    for (auto& entry : pending) {
        if (entry.second.callback)
            entry.second.callback->Continue(false, CefString());
    }
}

// ========================================================================
//  File dialogs (<input type="file">)
// ========================================================================

#if defined(_WIN32)
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
#elif defined(__linux__)
static std::vector<std::string> ShowCommonFileDialog(
    bool isSave,
    bool multiSelect,
    bool folderPick,
    const std::string& title,
    const std::string& defaultPath,
    const std::vector<std::pair<std::string, std::string>>& filterSpecs)
{
    std::vector<std::string> result;
    GtkFileChooserAction action = isSave
        ? GTK_FILE_CHOOSER_ACTION_SAVE
        : (folderPick ? GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER : GTK_FILE_CHOOSER_ACTION_OPEN);

    const char* fallbackTitle = isSave ? "Save As" : (folderPick ? "Select Folder" : "Open");
    GtkWidget* dialog = gtk_file_chooser_dialog_new(
        title.empty() ? fallbackTitle : title.c_str(), nullptr, action,
        "_Cancel", GTK_RESPONSE_CANCEL,
        isSave ? "_Save" : "_Open", GTK_RESPONSE_ACCEPT,
        nullptr);

    GtkFileChooser* chooser = GTK_FILE_CHOOSER(dialog);
    if (multiSelect && !folderPick && !isSave)
        gtk_file_chooser_set_select_multiple(chooser, TRUE);
    if (isSave)
        gtk_file_chooser_set_do_overwrite_confirmation(chooser, TRUE);

    for (const auto& spec : filterSpecs) {
        GtkFileFilter* filter = gtk_file_filter_new();
        gtk_file_filter_set_name(filter, spec.first.c_str());
        size_t start = 0;
        while (start <= spec.second.size()) {
            size_t semi = spec.second.find(';', start);
            std::string pat = spec.second.substr(start,
                semi == std::string::npos ? std::string::npos : semi - start);
            if (pat == "*" || pat == "*.*") {
                pat = "*";
            } else if (!pat.empty() && pat[0] == '.') {
                pat = "*" + pat;
            } else if (!pat.empty() && pat.find('*') == std::string::npos) {
                pat = "*." + pat;
            }
            if (!pat.empty())
                gtk_file_filter_add_pattern(filter, pat.c_str());
            if (semi == std::string::npos)
                break;
            start = semi + 1;
        }
        gtk_file_chooser_add_filter(chooser, filter);
    }
    GtkFileFilter* allFilter = gtk_file_filter_new();
    gtk_file_filter_set_name(allFilter, "All Files");
    gtk_file_filter_add_pattern(allFilter, "*");
    gtk_file_chooser_add_filter(chooser, allFilter);

    if (!defaultPath.empty()) {
        if (g_file_test(defaultPath.c_str(), G_FILE_TEST_IS_DIR)) {
            gtk_file_chooser_set_current_folder(chooser, defaultPath.c_str());
        } else {
            if (isSave) {
                gchar* base = g_path_get_basename(defaultPath.c_str());
                gchar* dir = g_path_get_dirname(defaultPath.c_str());
                if (base)
                    gtk_file_chooser_set_current_name(chooser, base);
                if (dir && std::strcmp(dir, ".") != 0)
                    gtk_file_chooser_set_current_folder(chooser, dir);
                g_free(base);
                g_free(dir);
            } else {
                gtk_file_chooser_set_filename(chooser, defaultPath.c_str());
            }
        }
    }

    if (gtk_dialog_run(GTK_DIALOG(dialog)) == GTK_RESPONSE_ACCEPT) {
        if (multiSelect && !folderPick && !isSave) {
            GSList* files = gtk_file_chooser_get_filenames(chooser);
            for (GSList* it = files; it; it = it->next)
                result.emplace_back(static_cast<char*>(it->data));
            g_slist_free_full(files, g_free);
        } else {
            char* filename = gtk_file_chooser_get_filename(chooser);
            if (filename) {
                result.emplace_back(filename);
                g_free(filename);
            }
        }
    }
    gtk_widget_destroy(dialog);
    return result;
}
#endif

bool BrowserHandler::OnFileDialog(CefRefPtr<CefBrowser> browser,
                                  FileDialogMode mode,
                                  const CefString& title,
                                  const CefString& default_file_path,
                                  const std::vector<CefString>& accept_filters,
                                  CefRefPtr<CefFileDialogCallback> callback)
{
#if defined(_WIN32)
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
#elif defined(__linux__)
    if (!g_gtkInitialized)
        return false;

    bool isSave      = (mode == FILE_DIALOG_SAVE);
    bool folderPick  = (mode == FILE_DIALOG_OPEN_FOLDER);
    bool multiSelect = (mode == FILE_DIALOG_OPEN_MULTIPLE);

    std::vector<std::pair<std::string, std::string>> specs;
    for (const auto& f : accept_filters) {
        std::string s = f.ToString();
        if (s.empty())
            continue;
        size_t bar = s.find('|');
        if (bar != std::string::npos) {
            std::string desc = s.substr(0, bar);
            std::string pat  = s.substr(bar + 1);
            if (!desc.empty() && !pat.empty())
                specs.push_back({ desc, pat });
        } else if (s[0] == '.') {
            specs.push_back({ s + " Files", s });
        }
    }
    specs.push_back({ "All Files", "*" });

    std::string titleS = title.ToString();
    if (titleS.empty())
        titleS = isSave ? "Save As" : (folderPick ? "Select Folder" : "Open");

    std::vector<std::string> paths = ShowCommonFileDialog(
        isSave, multiSelect, folderPick, titleS, default_file_path.ToString(), specs);

    if (paths.empty()) {
        callback->Cancel();
    } else {
        std::vector<CefString> cefPaths;
        cefPaths.reserve(paths.size());
        for (const auto& p : paths)
            cefPaths.push_back(CefString(p));
        callback->Continue(cefPaths);
    }
#endif

    return true;   // custom dialog used
}

// ========================================================================
//  Off-screen rendering (OSR)
// ========================================================================

#if defined(__linux__)
#include <sys/mman.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/stat.h>
#endif
#include <cstring>

void BrowserHandler::EnableOsr(int id, int width, int height)
{
    std::lock_guard<std::mutex> lock(osr_mutex_);
    osr_enabled_ = true;
    osr_width_ = width > 0 ? width : 1;
    osr_height_ = height > 0 ? height : 1;
    osr_shm_name_ = "cef_osr_" + std::to_string(id);
}

void BrowserHandler::SetOsrSize(int width, int height)
{
    std::lock_guard<std::mutex> lock(osr_mutex_);
    if (!osr_enabled_) return;
    if (width > 0)  osr_width_ = width;
    if (height > 0) osr_height_ = height;
}

CefRefPtr<CefRenderHandler> BrowserHandler::GetRenderHandler()
{
    // osr_enabled_ is set on the main thread before the browser is created, so
    // this unsynchronized read is safe.
    return osr_enabled_ ? this : nullptr;
}

void BrowserHandler::GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect)
{
    std::lock_guard<std::mutex> lock(osr_mutex_);
    rect = CefRect(0, 0, osr_width_, osr_height_);
}

void BrowserHandler::OnPaint(CefRefPtr<CefBrowser> browser,
                             PaintElementType type,
                             const RectList& dirtyRects,
                             const void* buffer,
                             int width,
                             int height)
{
    if (type != PET_VIEW)
        return;

    std::lock_guard<std::mutex> lock(osr_mutex_);
    if (!osr_enabled_ || !buffer || width <= 0 || height <= 0)
        return;

    const size_t needed = (size_t)width * (size_t)height * 4;
    osr_width_ = width;
    osr_height_ = height;

    bool recreated = false;
#if defined(__linux__)
    // (Re)create the shared memory segment when it is missing or too small.
    if (!osr_buffer_ || needed > osr_buffer_size_) {
        if (osr_buffer_) { munmap(osr_buffer_, osr_buffer_size_); osr_buffer_ = nullptr; }
        if (osr_fd_ >= 0)   { close(osr_fd_); osr_fd_ = -1; }

        int fd = shm_open(osr_shm_name_.c_str(), O_CREAT | O_RDWR, 0600);
        if (fd >= 0 && ftruncate(fd, (off_t)needed) == 0) {
            void* p = mmap(nullptr, needed, PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
            if (p != MAP_FAILED) {
                osr_fd_ = fd;
                osr_buffer_ = p;
                osr_buffer_size_ = needed;
                recreated = true;
            } else {
                close(fd);
            }
        } else if (fd >= 0) {
            close(fd);
        }
    }
#endif

    if (!osr_buffer_ || needed > osr_buffer_size_)
        return;   // allocation failed or OSR unsupported on this platform

    memcpy(osr_buffer_, buffer, needed);

    // Union of the dirty rects (fall back to the whole frame when empty).
    int dx = 0, dy = 0, dw = width, dh = height;
    if (!dirtyRects.empty()) {
        int x1 = dirtyRects[0].x, y1 = dirtyRects[0].y;
        int x2 = x1 + dirtyRects[0].width, y2 = y1 + dirtyRects[0].height;
        for (size_t i = 1; i < dirtyRects.size(); ++i) {
            const CefRect& r = dirtyRects[i];
            x1 = std::min(x1, r.x); y1 = std::min(y1, r.y);
            x2 = std::max(x2, r.x + r.width); y2 = std::max(y2, r.y + r.height);
        }
        dx = std::max(0, x1); dy = std::max(0, y1);
        dw = std::min(width, x2) - dx;
        dh = std::min(height, y2) - dy;
        if (dw <= 0 || dh <= 0) { dx = 0; dy = 0; dw = width; dh = height; }
    }

    if (recreated && OnOsrBufferEvent)
        OnOsrBufferEvent(width, height);

    if (OnPaintEvent)
        OnPaintEvent(width, height, dx, dy, dw, dh);
}
