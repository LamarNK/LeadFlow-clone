using System.Text.Json;

namespace LeadFlow.Models;

/// <summary>
/// Сериализация списков <see cref="AvitoAdStatus"/> в JSON для колонок SQLite (локальный кэш до следующего парсинга).
/// </summary>
public static class AvitoAdSnapshots
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(IReadOnlyList<AvitoAdStatus> ads) =>
        ads is { Count: > 0 }
            ? JsonSerializer.Serialize(ads.ToList(), JsonOptions)
            : "[]";

    public static IReadOnlyList<AvitoAdStatus> Deserialize(string? json, Guid accountId)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json.Trim(), "[]", StringComparison.Ordinal))
        {
            return Array.Empty<AvitoAdStatus>();
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<AvitoAdStatus>>(json, JsonOptions);
            if (list is null || list.Count == 0)
            {
                return Array.Empty<AvitoAdStatus>();
            }

            foreach (var ad in list)
            {
                ad.AccountId = accountId;
            }

            return list;
        }
        catch
        {
            return Array.Empty<AvitoAdStatus>();
        }
    }
}
