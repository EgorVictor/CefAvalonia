#include "browser_handler.h"
#include "include/cef_browser.h"
#include "include/cef_frame.h"

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
