using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class SubProfileEnabledFilter
{
    public static IReadOnlyList<AvitoSubProfile> GetEnabled(
        IReadOnlyList<AvitoSubProfile> subProfiles,
        IReadOnlySet<string>? disabledIds)
    {
        if (subProfiles.Count == 0)
        {
            return subProfiles;
        }

        var valid = AvitoSubProfileRules.FilterValid(subProfiles);
        if (disabledIds is null || disabledIds.Count == 0)
        {
            return valid;
        }

        return valid
            .Where(sp => !disabledIds.Contains(sp.Id))
            .ToList();
    }
}