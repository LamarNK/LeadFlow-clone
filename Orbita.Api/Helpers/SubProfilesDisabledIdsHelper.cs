using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

public static class SubProfilesDisabledIdsHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions)?
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(static id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<string> ids) =>
        JsonSerializer.Serialize(ids ?? [], JsonOptions);

    public static bool IsEnabled(IReadOnlyList<string> disabledIds, string subProfileId) =>
        !disabledIds.Contains(subProfileId, StringComparer.Ordinal);

    public static bool ContainsSubProfile(string? subProfilesJson, string subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId)
            || string.IsNullOrWhiteSpace(subProfilesJson)
            || subProfilesJson == "[]")
        {
            return false;
        }

        try
        {
            var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(subProfilesJson, SubProfileJsonOptions.Deserialize);
            return profiles?.Any(p => string.Equals(p.Id, subProfileId, StringComparison.Ordinal)) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}