#pragma once
#include "include/cef_client.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_load_handler.h"
#include "include/cef_display_handler.h"
#include "include/cef_download_handler.h"
#include "include/cef_download_item.h"
#include "include/cef_jsdialog_handler.h"
#include "include/cef_dialog_handler.h"
#include "include/cef_render_handler.h"
#include <string>
#include <functional>
#include <map>
#include <mutex>
#include <vector>

class BrowserHandler : public CefClient,
                       public CefLifeSpanHandler,
                       public CefLoadHandler,
                       public CefDisplayHandler,
                       public CefDownloadHandler,
                       public CefJSDialogHandler,
                       public CefDialogHandler,
                       public CefRenderHandler {
public:
    BrowserHandler();

    // CefClient methods
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }
    CefRefPtr<CefJSDialogHandler> GetJSDialogHandler() override { return this; }
    CefRefPtr<CefDialogHandler> GetDialogHandler() override { return this; }

    // CefLifeSpanHandler
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;
    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override;
    bool OnBeforePopup(CefRefPtr<CefBrowser> browser,
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
                       bool* no_javascript_access) override;

    // CefLoadHandler
    void OnLoadError(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     ErrorCode errorCode,
                     const CefString& errorText,
                     const CefString& failedUrl) override;
    void OnLoadingStateChange(CefRefPtr<CefBrowser> browser,
                              bool isLoading,
                              bool canGoBack,
                              bool canGoForward) override;

    // CefDisplayHandler
    void OnAddressChange(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame,
                         const CefString& url) override;

    void OnTitleChange(CefRefPtr<CefBrowser> browser,
                       const CefString& title) override;

    // CefDownloadHandler
    // Called before a download begins. We pick a target path in the user's
    // Downloads folder and continue; without this handler CEF silently cancels
    // all downloads (GetDownloadHandler() returns nullptr by default).
    void OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                          CefRefPtr<CefDownloadItem> download_item,
                          const CefString& suggested_name,
                          CefRefPtr<CefBeforeDownloadCallback> callback) override;

    // Called whenever a download's status/progress changes.
    void OnDownloadUpdated(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefDownloadItem> download_item,
                           CefRefPtr<CefDownloadItemCallback> callback) override;

    // CefJSDialogHandler
    // Handles alert()/confirm()/prompt(). Without this handler CEF silently
    // swallows JS dialogs (confirm() returns false), breaking many pages.
    bool OnJSDialog(CefRefPtr<CefBrowser> browser,
                    const CefString& origin_url,
                    JSDialogType dialog_type,
                    const CefString& message_text,
                    const CefString& default_prompt_text,
                    CefRefPtr<CefJSDialogCallback> callback,
                    bool& suppress_message) override;

    // Return false: never block navigation/tab close with a leave-confirmation
    // prompt (it would otherwise be able to hang the close path).
    bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                              const CefString& message_text,
                              bool is_reload,
                              CefRefPtr<CefJSDialogCallback> callback) override;

    // CefDialogHandler
    // Handles <input type="file">. Without this handler clicking a file input
    // does nothing at all.
    bool OnFileDialog(CefRefPtr<CefBrowser> browser,
                      FileDialogMode mode,
                      const CefString& title,
                      const CefString& default_file_path,
                      const std::vector<CefString>& accept_filters,
                      CefRefPtr<CefFileDialogCallback> callback) override;

    // ---- Off-screen rendering (OSR) ----
    //
    // Used on Linux, where embedding a native child window into Avalonia's X11
    // window is unreliable: Chromium cannot create an EGL surface for a child of
    // Avalonia's window (EGL_BAD_NATIVE_WINDOW), so the window is destroyed
    // right after creation and the control stays blank/transparent.
    //
    // In OSR mode CEF renders into a POSIX shared memory segment
    // (/dev/shm/cef_osr_{id}, BGRA, width*height*4 bytes) that the managed side
    // maps directly and blits into a bitmap — no X11 child window involved.
    void EnableOsr(int id, int width, int height);
    bool IsOsr() const { return osr_enabled_; }
    void SetOsrSize(int width, int height);

    // (w, h, dirtyX, dirtyY, dirtyW, dirtyH) — pixels are already in the shm
    // segment when this fires.
    std::function<void(int, int, int, int, int, int)> OnPaintEvent;
    // (w, h) — the shm segment was (re)created; the managed side must re-map it.
    std::function<void(int, int)> OnOsrBufferEvent;

    // CefRenderHandler (only called in OSR mode)
    CefRefPtr<CefRenderHandler> GetRenderHandler() override;
    void GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect) override;
    void OnPaint(CefRefPtr<CefBrowser> browser,
                 PaintElementType type,
                 const RectList& dirtyRects,
                 const void* buffer,
                 int width,
                 int height) override;

    // Public accessors
    CefWindowHandle GetBrowserWindowHandle() const { return browser_window_; }
    CefRefPtr<CefBrowser> GetBrowser() const { return browser_; }
    void SetBrowserId(int id) { browser_id_ = id; }

    // Resolves a pending JavaScript dialog. Returns false when the id is unknown
    // (already answered, or the dialog was closed with the browser).
    bool ResolveJsDialog(int dialog_id, bool accept, const std::string& text);

    // Answers every outstanding dialog with "dismiss" so shutdown cannot hang.
    void DismissAllDialogs();

    // Callbacks
    std::function<void(CefWindowHandle)> OnBrowserReady;
    std::function<void(const std::string&)> OnAddressChanged;
    std::function<void(const std::string&)> OnLoadErrorEvent;
    std::function<void()> OnBrowserClosed;
    std::function<void(bool, bool, bool)> OnLoadingStateChanged;
    std::function<void(const std::string&)> OnTitleChangedCB;
    std::function<void(const std::string&)> OnBeforePopupCB;
    std::function<void(const std::string&)> OnDownloadEvent;

    // Set by the host: (dialog_id, type, message, default_text). When unset the
    // handler falls back to native OS dialogs (standalone mode).
    std::function<void(int, const std::string&, const std::string&, const std::string&)> OnJsDialog;

private:
    struct PendingDialog {
        CefRefPtr<CefJSDialogCallback> callback;
    };

    static int NextDialogId();

    CefRefPtr<CefBrowser> browser_;
    CefWindowHandle browser_window_ = 0;
    int browser_id_ = 0;

    std::mutex dialog_mutex_;
    std::map<int, PendingDialog> pending_dialogs_;

    // OSR state
    bool osr_enabled_ = false;
    int osr_width_ = 1;
    int osr_height_ = 1;
    std::string osr_shm_name_;
    int osr_fd_ = -1;
    void* osr_buffer_ = nullptr;
    size_t osr_buffer_size_ = 0;
    std::mutex osr_mutex_;

    IMPLEMENT_REFCOUNTING(BrowserHandler);
};
