using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Helpers;

internal static class AdsPowerGroupsJson
{
    private const int GroupIdMaxLength = 64;
    private const int GroupNameMaxLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string? NormalizeGroupId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > GroupIdMaxLength ? trimmed[..GroupIdMaxLength] : trimmed;
    }

    public static string? NormalizeGroupName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > GroupNameMaxLength ? trimmed[..GroupNameMaxLength] : trimmed;
    }

    public static IReadOnlyList<AdsPowerGroupDto> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<AdsPowerGroupDto>>(json, JsonOptions);
            if (parsed is null || parsed.Count == 0)
            {
                return [];
            }

            return parsed
                .Select(x => new AdsPowerGroupDto(
                    NormalizeGroupId(x.GroupId) ?? string.Empty,
                    NormalizeGroupName(x.GroupName) ?? (NormalizeGroupId(x.GroupId) ?? string.Empty)))
                .Where(x => x.GroupId.Length > 0)
                .GroupBy(x => x.GroupId, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<AdsPowerGroupDto> groups)
    {
        var normalized = groups
            .Select(x => new AdsPowerGroupDto(
                NormalizeGroupId(x.GroupId) ?? string.Empty,
                NormalizeGroupName(x.GroupName) ?? (NormalizeGroupId(x.GroupId) ?? string.Empty)))
            .Where(x => x.GroupId.Length > 0)
            .GroupBy(x => x.GroupId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static string? ResolveGroupName(string? groupId, IReadOnlyList<AdsPowerGroupDto> groups)
    {
        var id = NormalizeGroupId(groupId);
        if (id is null)
        {
            return null;
        }

        return groups
            .FirstOrDefault(x => string.Equals(x.GroupId, id, StringComparison.Ordinal))
            ?.GroupName;
    }
}
