using System.Globalization;
using System.Text.Json;
using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public static class AvitoChatMessagesJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(IReadOnlyList<AvitoChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(messages, SerializerOptions);
    }

    public static IReadOnlyList<AvitoChatMessage> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AvitoChatMessage>>(json, SerializerOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<AvitoChatMessage> ParseFromCandidateJson(JsonElement item)
    {
        if (!item.TryGetProperty("chatMessages", out var chatElement) || chatElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<AvitoChatMessage>();
        foreach (var message in chatElement.EnumerateArray())
        {
            var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            results.Add(new AvitoChatMessage
            {
                Text = text.Trim(),
                At = message.TryGetProperty("at", out var atProp) ? atProp.GetString() : null,
                Side = message.TryGetProperty("side", out var sideProp) ? sideProp.GetString() ?? "left" : "left",
                IsPlatform = message.TryGetProperty("isPlatform", out var platformProp) && platformProp.ValueKind == JsonValueKind.True
            });
        }

        return results;
    }

    /// <summary>
    /// Дата отклика из мини-чата: предпочитаем системное сообщение
    /// «Кандидат откликнулся…», иначе самое раннее сообщение с <c>at</c>.
    /// </summary>
    public static DateTime? TryGetResponseAtUtc(IReadOnlyList<AvitoChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        DateTime? earliestPlatform = null;
        DateTime? earliestAny = null;

        foreach (var message in messages)
        {
            if (!TryParseMessageAtUtc(message.At, out var atUtc))
            {
                continue;
            }

            if (earliestAny is null || atUtc < earliestAny.Value)
            {
                earliestAny = atUtc;
            }

            if (IsCandidateResponsePlatformMessage(message)
                && (earliestPlatform is null || atUtc < earliestPlatform.Value))
            {
                earliestPlatform = atUtc;
            }
        }

        return earliestPlatform ?? earliestAny;
    }

    private static bool IsCandidateResponsePlatformMessage(AvitoChatMessage message) =>
        message.IsPlatform
        && message.Text.Contains("кандидат откликнулся", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseMessageAtUtc(string? at, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(at))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(
                at,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }

        if (DateTime.TryParse(
                at,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return true;
        }

        return false;
    }
}
