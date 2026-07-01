using System.Globalization;
using System.Text.Json;

namespace Orbita.Web.Formatting;

public static class ResponseChatDisplay
{
    public static IReadOnlyList<ResponseChatMessageViewModel> ParseMessages(string? chatMessagesJson)
    {
        if (string.IsNullOrWhiteSpace(chatMessagesJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(chatMessagesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<ResponseChatMessageViewModel>();
            foreach (var message in doc.RootElement.EnumerateArray())
            {
                var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var side = message.TryGetProperty("side", out var sideProp) ? sideProp.GetString() : null;
                var isPlatform = message.TryGetProperty("isPlatform", out var platformProp)
                    && platformProp.ValueKind == JsonValueKind.True;
                var at = message.TryGetProperty("at", out var atProp) ? atProp.GetString() : null;

                var tone = string.Equals(side, "right", StringComparison.OrdinalIgnoreCase)
                    ? "outgoing"
                    : isPlatform
                        ? "system"
                        : "incoming";

                results.Add(new ResponseChatMessageViewModel
                {
                    Text = text.Trim(),
                    TimeLabel = FormatMessageTime(at),
                    Tone = tone
                });
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static string FormatMessageTime(string? at)
    {
        if (string.IsNullOrWhiteSpace(at))
        {
            return string.Empty;
        }

        if (DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            var local = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
            return local.ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

        return at;
    }
}

public sealed class ResponseChatMessageViewModel
{
    public string Text { get; init; } = string.Empty;
    public string TimeLabel { get; init; } = string.Empty;
    public string Tone { get; init; } = "incoming";
}