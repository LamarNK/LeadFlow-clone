using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

internal static class AvitoSubProfileRules
{
    public static bool HasValidId(AvitoSubProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Id);

    public static IReadOnlyList<AvitoSubProfile> FilterValid(IEnumerable<AvitoSubProfile> profiles) =>
        profiles.Where(HasValidId).ToList();

    public static Dictionary<string, AvitoSubProfile> IndexById(IEnumerable<AvitoSubProfile> profiles)
    {
        var byId = new Dictionary<string, AvitoSubProfile>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (!HasValidId(profile))
            {
                continue;
            }

            var id = profile.Id.Trim();
            byId.TryAdd(id, profile);
        }

        return byId;
    }
}