using System.IO;
using LeadFlow.Models;
using LeadFlow.Services;

namespace LeadFlow.Services.Browser;

public sealed class BrowserProfileService : IBrowserProfileService
{
    public BrowserProfileInfo GetProfile(AvitoAccount account)
    {
        var path = Path.Combine(
            JsonSettingsService.GetDataDirectoryPath(),
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

    public void DeleteProfile(string profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
        {
            return;
        }

        Directory.Delete(profilePath, recursive: true);
    }
}
