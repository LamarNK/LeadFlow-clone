using System.IO;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public sealed class BrowserProfileService : IBrowserProfileService
{
    public BrowserProfileInfo GetProfile(AvitoAccount account)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LeadFlow",
            "Profiles",
            "Avito",
            account.Id.ToString());

        Directory.CreateDirectory(path);
        return new BrowserProfileInfo
        {
            AccountId = account.Id,
            ProfilePath = path,
            Exists = Directory.Exists(path)
        };
    }
}
