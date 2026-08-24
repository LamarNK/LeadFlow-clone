using System.Globalization;
using System.Text.Json;

namespace Orbita.Contracts;

/// <summary>
/// Поля AdsPower startup-диагностики, которые можно доставить worker → Orbita.
/// Не включает raw URL, credentials, ws endpoint, token, cookies, raw JSON/HTML, unbounded messages.
/// </summary>
public static class WorkerLogPropertyAllowlist
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "startup.correlationId",
        "startup.attempt",
        "startup.stage",
        "startup.elapsedMs",
        "startup.event",
        "startup.boundary",
        "startup.lastSuccessfulLocalApi",
        "startup.lastSuccessfulCdp",
        "localApi.operation",
        "localApi.phase",
        "localApi.queueWaitMs",
        "localApi.rateLimitWaitMs",
        "localApi.httpSendMs",
        "localApi.httpResponseMs",
        "localApi.parseMs",
        "localApi.durationMs",
        "localApi.outcome",
        "localApi.httpStatus",
        "localApi.ok",
        "localApi.adsPowerCode",
        "localApi.hasWsPuppeteer",
        "localApi.transportExceptionType",
        "cdp.call",
        "cdp.operation",
        "cdp.durationMs",
        "cdp.ok",
        "cdp.pagesCount",
        "cdp.urlClasses",
        "error.type",
        "adsPower.userId"
    };

    public static bool IsAllowed(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Allowed.Contains(key);

    public static Dictionary<string, object?> Filter(IReadOnlyDictionary<string, object?>? source)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (source is null)
        {
            return result;
        }

        foreach (var kv in source)
        {
            if (!IsAllowed(kv.Key) || kv.Value is null)
            {
                continue;
            }

            result[kv.Key] = kv.Value switch
            {
                string s => s.Length <= 160 ? s : s[..160] + "…",
                bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => kv.Value,
                JsonElement element => ReadElement(element),
                _ => kv.Value.ToString()
            };
        }

        return result;
    }

    public static Dictionary<string, string> ToTransportMap(IReadOnlyDictionary<string, object?>? source)
    {
        var filtered = Filter(source);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in filtered)
        {
            if (kv.Value is null)
            {
                continue;
            }

            result[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return result;
    }

    private static object? ReadElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
}
