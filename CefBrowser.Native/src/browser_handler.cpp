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
