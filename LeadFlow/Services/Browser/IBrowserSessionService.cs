using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public interface IBrowserSessionService
{
    Task<BrowserAccountSession> CreateSessionAsync(AvitoAccount account, CancellationToken cancellationToken);
}
