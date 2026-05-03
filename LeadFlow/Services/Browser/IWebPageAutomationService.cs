namespace LeadFlow.Services.Browser;

public interface IWebPageAutomationService
{
    Task NavigateAsync(BrowserAccountSession session, string url, CancellationToken cancellationToken);
    Task<string> ExecuteScriptAsync(BrowserAccountSession session, string script, CancellationToken cancellationToken);
}
