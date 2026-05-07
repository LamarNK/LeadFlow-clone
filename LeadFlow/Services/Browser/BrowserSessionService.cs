using LeadFlow.Data;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public sealed class BrowserSessionService(
    IBrowserProfileService profileService,
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
            PersistAccountAsync = ct => repository.SaveAccountAsync(account, ct)
        });
    }
}
