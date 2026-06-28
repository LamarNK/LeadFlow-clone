


namespace LeadFlow.Services.Browser;

public sealed class BrowserSessionService(
    IBrowserProfileService profileService,
    IProxyCheckService proxyCheckService,
    AppRepository repository) : IBrowserSessionService
{
    public Task<BrowserAccountSession> CreateSessionAsync(AvitoAccount account, CancellationToken cancellationToken)
    {
        var profile = profileService.GetProfile(account);
        account.BrowserProfilePath = profile.ProfilePath;
        return Task.FromResult(new BrowserAccountSession
        {
            Account = account,
            ProfilePath = profile.ProfilePath,
            CurrentUrl = account.AvitoResponsesUrl,
            ProxyCheckService = proxyCheckService,
            PersistAccountAsync = ct => repository.SaveAccountAsync(account, ct)
        });
    }
}
