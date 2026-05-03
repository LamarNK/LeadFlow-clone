using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public interface IBrowserProfileService
{
    BrowserProfileInfo GetProfile(AvitoAccount account);
}
