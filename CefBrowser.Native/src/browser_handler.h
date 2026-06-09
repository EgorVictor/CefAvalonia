#pragma once
#include "include/cef_client.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_load_handler.h"
#include "include/cef_display_handler.h"
#include <string>
#include <functional>

class BrowserHandler : public CefClient,
                       public CefLifeSpanHandler,
                       public CefLoadHandler,
                       public CefDisplayHandler {
public:
    BrowserHandler();

    // CefClient methods
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }

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

    // Public accessors
    HWND GetBrowserHwnd() const { return browser_hwnd_; }
    CefRefPtr<CefBrowser> GetBrowser() const { return browser_; }

    // Callbacks
    std::function<void(HWND)> OnBrowserReady;
    std::function<void(const std::string&)> OnAddressChanged;
    std::function<void(const std::string&)> OnLoadErrorEvent;
    std::function<void()> OnBrowserClosed;
    std::function<void(bool, bool, bool)> OnLoadingStateChanged;
    std::function<void(const std::string&)> OnTitleChangedCB;
    std::function<void(const std::string&)> OnBeforePopupCB;

private:
    CefRefPtr<CefBrowser> browser_;
    HWND browser_hwnd_ = nullptr;

    IMPLEMENT_REFCOUNTING(BrowserHandler);
};
