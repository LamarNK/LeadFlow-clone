namespace LeadFlow.Services.Browser;

public interface IBackgroundWebViewHostFactory
{
    Task<IBackgroundWebViewHost> CreateAsync(CancellationToken cancellationToken);
}
