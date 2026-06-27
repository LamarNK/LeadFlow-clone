using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotifyBot.Api.Dto;

public sealed class PlusofonWebhookDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("sms_text")]
    public string? SmsText { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("data")]
    public PlusofonWebhookDataDto? Data { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string? GetMessageText()
    {
        var direct = Text ?? Message ?? Body ?? SmsText ?? Content ?? Data?.Text ?? Data?.Message ?? Data?.Body;
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (ExtensionData is null)
        {
            return null;
        }

        foreach (var key in new[] { "text", "message", "body", "sms_text", "content" })
        {
            if (ExtensionData.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}

public sealed class PlusofonWebhookDataDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}