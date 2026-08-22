using System.Globalization;
using System.Text.Json;

namespace Orbita.Contracts;

/// <summary>
/// Дата отклика из мини-чата Avito: системное «Кандидат откликнулся…»,
/// иначе самое раннее сообщение с меткой времени.
/// </summary>
public static class AvitoChatResponseAt
{
    public static DateTime? TryGetUtc(string? chatMessagesJson)
    {
        if (string.IsNullOrWhiteSpace(chatMessagesJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(chatMessagesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            DateTime? earliestPlatform = null;
            DateTime? earliestAny = null;
            foreach (var message in doc.RootElement.EnumerateArray())
            {
                var at = message.TryGetProperty("at", out var atProp) ? atProp.GetString() : null;
                if (!TryParseMessageAtUtc(at, out var atUtc))
                {
                    continue;
                }

                Consider(atUtc, IsPlatformMessage(message), ReadText(message), ref earliestPlatform, ref earliestAny);
            }

            return earliestPlatform ?? earliestAny;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DateTime? TryGetUtc(IEnumerable<(string Text, string? At, bool IsPlatform)> messages)
    {
        DateTime? earliestPlatform = null;
        DateTime? earliestAny = null;
        foreach (var message in messages)
        {
            if (!TryParseMessageAtUtc(message.At, out var atUtc))
            {
                continue;
            }

            Consider(atUtc, message.IsPlatform, message.Text, ref earliestPlatform, ref earliestAny);
        }

        return earliestPlatform ?? earliestAny;
    }

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

    private static void Consider(
        DateTime atUtc,
        bool isPlatform,
        string? text,
        ref DateTime? earliestPlatform,
        ref DateTime? earliestAny)
    {
        if (earliestAny is null || atUtc < earliestAny.Value)
        {
            earliestAny = atUtc;
        }

        if (IsCandidateResponsePlatformMessage(isPlatform, text)
            && (earliestPlatform is null || atUtc < earliestPlatform.Value))
        {
            earliestPlatform = atUtc;
        }
    }

    private static bool IsCandidateResponsePlatformMessage(bool isPlatform, string? text) =>
        isPlatform
        && !string.IsNullOrWhiteSpace(text)
        && text.Contains("кандидат откликнулся", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlatformMessage(JsonElement message) =>
        message.TryGetProperty("isPlatform", out var platformProp)
        && platformProp.ValueKind == JsonValueKind.True;

    private static string? ReadText(JsonElement message) =>
        message.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
}
