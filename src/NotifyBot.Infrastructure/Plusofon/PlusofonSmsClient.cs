using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotifyBot.Application.Abstractions;
using NotifyBot.Application.Options;

namespace NotifyBot.Infrastructure.Plusofon;

public sealed class PlusofonSmsClient(
    HttpClient httpClient,
    IOptions<PlusofonOptions> options,
    ILogger<PlusofonSmsClient> logger) : IPlusofonSmsClient
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly PlusofonOptions _options = options.Value;

    public Task<IReadOnlyList<PlusofonSmsMessage>> GetRecentIncomingAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        GetRecentAsync(limit, incomingOnly: true, cancellationToken);

    public async Task<IReadOnlyList<PlusofonSmsMessage>> GetRecentAsync(
        int limit,
        bool? incomingOnly,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsApiConfigured)
        {
            throw new InvalidOperationException("Plusofon API credentials are not configured.");
        }

        var query = incomingOnly switch
        {
            true => $"api/v1/sms?incoming=1&limit={limit}",
            false => $"api/v1/sms?incoming=0&limit={limit}",
            _ => $"api/v1/sms?limit={limit}"
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Add("Client", _options.ResolvedClientId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Plusofon SMS API error {StatusCode}: {Body}",
                (int)response.StatusCode,
                body);
            throw new InvalidOperationException(ParseApiError(body, (int)response.StatusCode));
        }

        var payload = await response.Content.ReadFromJsonAsync<PlusofonSmsListResponse>(
            cancellationToken: cancellationToken);

        if (payload?.Data is null || payload.Data.Count == 0)
        {
            return [];
        }

        return payload.Data
            .Select(MapMessage)
            .OrderByDescending(x => x.ReceivedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private static PlusofonSmsMessage MapMessage(PlusofonSmsItemDto item) =>
        new(
            Text: item.Msg ?? string.Empty,
            ReceivedAtUtc: ParseDateTime(item.SentDatetime ?? item.CreatedDatetime),
            Incoming: item.IsIncoming,
            Sender: item.Sender,
            Receiver: item.Receiver);

    private static string ParseApiError(string body, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var messageElement))
            {
                var message = messageElement.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }
        catch (JsonException)
        {
        }

        return $"Plusofon API вернул {statusCode}.";
    }

    private static DateTimeOffset? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, RuCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private sealed class PlusofonSmsListResponse
    {
        [JsonPropertyName("data")]
        public List<PlusofonSmsItemDto>? Data { get; set; }
    }

    private sealed class PlusofonSmsItemDto
    {
        [JsonPropertyName("msg")]
        public string? Msg { get; set; }

        [JsonPropertyName("sender")]
        public string? Sender { get; set; }

        [JsonPropertyName("receiver")]
        public string? Receiver { get; set; }

        [JsonPropertyName("incoming")]
        public JsonElement Incoming { get; set; }

        public bool IsIncoming => Incoming.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Incoming.TryGetInt32(out var value) && value == 1,
            JsonValueKind.String => Incoming.GetString() is "1" or "true",
            _ => false
        };

        [JsonPropertyName("sent_datetime")]
        public string? SentDatetime { get; set; }

        [JsonPropertyName("created_datetime")]
        public string? CreatedDatetime { get; set; }
    }
}