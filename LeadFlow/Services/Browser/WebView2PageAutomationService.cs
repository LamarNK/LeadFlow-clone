using Microsoft.Web.WebView2.Core;
using System.Windows;

namespace LeadFlow.Services.Browser;

public sealed class WebView2PageAutomationService : IWebPageAutomationService
{
    public async Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken)
    {
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.AttachedView?.CoreWebView2?.Navigate(url);
        });

        session.CurrentUrl = url;
    }

    public async Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken)
    {
        return await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.AttachedView?.CoreWebView2 is not CoreWebView2 core)
            {
                return string.Empty;
            }

            return await core.ExecuteScriptAsync(script);
        }).Task.Unwrap();
    }
}
