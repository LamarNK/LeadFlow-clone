using Microsoft.Web.WebView2.Core;

namespace LeadFlow.Services.Browser;

public sealed class WebView2PageAutomationService : IWebPageAutomationService
{
    public Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken)
    {
        session.AttachedView?.CoreWebView2?.Navigate(url);
        session.CurrentUrl = url;
        return Task.CompletedTask;
    }

    public async Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken)
    {
        if (session.AttachedView?.CoreWebView2 is not CoreWebView2 core)
        {
            return string.Empty;
        }

        return await core.ExecuteScriptAsync(script);
    }
}
