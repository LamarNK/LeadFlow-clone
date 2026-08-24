using System.Text.Json;

namespace LeadFlow.Core.Logging.Audit;

/// <summary>
/// Достаёт <c>context</c> из JSON-конверта локального журнала. Allowlist — не здесь:
/// фильтрация секретов живёт в <c>WorkerLogPropertyAllowlist</c>.
/// </summary>
public static class LogEnvelopeParser
{
    public static Dictionary<string, object?> ParseContext(string? propertiesJson)
    {
        if (string.IsNullOrWhiteSpace(propertiesJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(propertiesJson);
            var root = doc.RootElement;
            var context = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("context", out var ctx)
                ? ctx
                : root;
            if (context.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var raw = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in context.EnumerateObject())
            {
                raw[property.Name] = property.Value.Clone();
            }

            return raw;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
