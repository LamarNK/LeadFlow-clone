namespace LeadFlow.Services.Browser;

public interface IBackgroundWebViewHost : IAsyncDisposable
{
    Task AttachAsync(BrowserAccountSession session, CancellationToken cancellationToken);
}
