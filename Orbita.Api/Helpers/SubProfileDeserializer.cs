using System.Text.Json;
using Orbita.Contracts;

using static Orbita.Api.Helpers.SubProfilesDisabledIdsHelper;

namespace Orbita.Api.Helpers;

internal static class SubProfileDeserializer
{
    public static IReadOnlyList<WorkerSubProfileDto>? Deserialize(
        string? json,
        string? disabledIdsJson = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return null;
        }

        List<WorkerSubProfileDto>? profiles = null;
        try
        {
            profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(json, SubProfileJsonOptions.Deserialize);
        }
        catch (JsonException)
        {
            profiles = null;
        }

        if (profiles is null || profiles.Count == 0)
        {
            profiles = TryDeserializeFromJsonElements(json);
        }
        else if (profiles.All(static p => string.IsNullOrWhiteSpace(p.Id) && string.IsNullOrWhiteSpace(p.Name)))
        {
            var fallback = TryDeserializeFromJsonElements(json);
            if (fallback is { Count: > 0 })
            {
                profiles = fallback;
            }
        }

        if (profiles is null || profiles.Count == 0)
        {
            return null;
        }

        var disabled = Parse(disabledIdsJson);
        return profiles
            .Select(p => p with { IsEnabledInPanel = IsEnabled(disabled, p.Id) })
            .ToList();
    }

    private static List<WorkerSubProfileDto>? TryDeserializeFromJsonElements(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var profiles = new List<WorkerSubProfileDto>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = ReadString(element, "id", "Id");
                var name = ReadString(element, "name", "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = id;
                }

                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    id = name!;
                }

                profiles.Add(new WorkerSubProfileDto(
                    id.Trim(),
                    name!.Trim(),
                    ReadString(element, "category", "Category") ?? string.Empty,
                    ReadBool(element, "isCurrent", "IsCurrent"),
                    ReadDecimal(element, "balance", "Balance"),
                    ReadString(element, "lastIssueKind", "LastIssueKind"),
                    ReadString(element, "lastIssueMessage", "LastIssueMessage"),
                    ReadDateTime(element, "lastIssueAt", "LastIssueAt"),
                    IsEnabledInPanel: ReadBool(element, "isEnabledInPanel", "IsEnabledInPanel", defaultValue: true),
                    DiagnosticAttachmentId: ReadGuid(element, "diagnosticAttachmentId", "DiagnosticAttachmentId"),
                    WalletBalance: ReadDecimal(element, "walletBalance", "WalletBalance"),
                    AdvanceDurationText: ReadString(element, "advanceDurationText", "AdvanceDurationText"),
                    Rating: ReadDecimal(element, "rating", "Rating"),
                    ReviewsCount: ReadInt(element, "reviewsCount", "ReviewsCount"),
                    ReviewsText: ReadString(element, "reviewsText", "ReviewsText")));
            }

            return profiles.Count == 0 ? null : profiles;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string camelName, string pascalName)
    {
        if (element.TryGetProperty(camelName, out var camel) && camel.ValueKind == JsonValueKind.String)
        {
            return camel.GetString();
        }

        if (element.TryGetProperty(pascalName, out var pascal) && pascal.ValueKind == JsonValueKind.String)
        {
            return pascal.GetString();
        }

        return null;
    }

    private static bool ReadBool(JsonElement element, string camelName, string pascalName, bool defaultValue = false)
    {
        if (element.TryGetProperty(camelName, out var camel) && camel.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return camel.GetBoolean();
        }

        if (element.TryGetProperty(pascalName, out var pascal) && pascal.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return pascal.GetBoolean();
        }

        return defaultValue;
    }

    private static decimal? ReadDecimal(JsonElement element, string camelName, string pascalName)
    {
        if (TryReadNumber(element, camelName, out var camelValue) || TryReadNumber(element, pascalName, out camelValue))
        {
            return camelValue;
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string camelName, string pascalName)
    {
        if (TryReadNumber(element, camelName, out var camelValue) || TryReadNumber(element, pascalName, out camelValue))
        {
            return (int)camelValue;
        }

        return null;
    }

    private static bool TryReadNumber(JsonElement element, string propertyName, out decimal value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out value))
        {
            return true;
        }

        return prop.ValueKind == JsonValueKind.String
            && decimal.TryParse(prop.GetString(), out value);
    }

    private static DateTime? ReadDateTime(JsonElement element, string camelName, string pascalName)
    {
        var raw = ReadString(element, camelName, pascalName);
        return DateTime.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static Guid? ReadGuid(JsonElement element, string camelName, string pascalName)
    {
        var raw = ReadString(element, camelName, pascalName);
        return Guid.TryParse(raw, out var parsed) ? parsed : null;
    }
}