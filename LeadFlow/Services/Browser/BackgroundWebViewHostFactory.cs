namespace LeadFlow.Services.Browser;

public sealed class BackgroundWebViewHostFactory : IBackgroundWebViewHostFactory
{
    public async Task<IBackgroundWebViewHost> CreateAsync(CancellationToken cancellationToken) =>
        await BackgroundWebViewHost.CreateAsync(cancellationToken);
}
