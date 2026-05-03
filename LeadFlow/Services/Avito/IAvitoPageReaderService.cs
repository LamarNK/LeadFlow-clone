using LeadFlow.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public interface IAvitoPageReaderService
{
    Task<AuthCheckResult> CheckAuthorizationAsync(BrowserAccountSession session, AvitoSelectorOptions selectors, CancellationToken cancellationToken);
}
