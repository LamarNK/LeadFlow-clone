using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public interface IProfileCookiesService
{
    Task<string> ReadCurrentProfileCookiesAsJsonAsync(AvitoAccount account, CancellationToken cancellationToken);
}
