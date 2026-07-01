using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

public static class SubProfileNameResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string? ResolveName(string? subProfilesJson, string? subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(subProfilesJson) || subProfilesJson == "[]")
        {
            return null;
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(subProfilesJson, JsonOptions);
            if (profiles is null || profiles.Count == 0)
            {
                return null;
            }

            var match = profiles.FirstOrDefault(p =>
                string.Equals(p.Id, subProfileId, StringComparison.Ordinal));
            if (match is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(match.Name) ? match.Id : match.Name;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Dictionary<(Guid AccountId, string SubProfileId), string> BuildLookup(
        IEnumerable<(Guid AccountId, string SubProfilesJson)> rows)
    {
        var lookup = new Dictionary<(Guid, string), string>();
        foreach (var (accountId, json) in rows)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[]")
            {
                continue;
            }

            try
            {
                var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(json, JsonOptions);
                if (profiles is null)
                {
                    continue;
                }

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrWhiteSpace(profile.Id))
                    {
                        continue;
                    }

                    var name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name;
                    lookup[(accountId, profile.Id)] = name;
                }
            }
            catch (JsonException)
            {
                // ignore malformed JSON
            }
        }

        return lookup;
    }
}