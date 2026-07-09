using System.Text.Json;

namespace Orbita.Contracts;

public static class WorkerEventDetailsParser
{
    public static Guid? TryParseAttachmentId(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        var trimmed = details.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("attachmentId", out var idElement))
            {
                if (idElement.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                if (idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out var attachmentId))
                {
                    return attachmentId;
                }

                if (idElement.ValueKind != JsonValueKind.Null
                    && idElement.TryGetGuid(out var guidAttachmentId)
                    && guidAttachmentId != Guid.Empty)
                {
                    return guidAttachmentId;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }

        return null;
    }

    public static string? TryParseDiagnosticText(string? details) =>
        TryParseStringProperty(details, "text");

    public static string? TryParseDiagnosticUrl(string? details) =>
        TryParseStringProperty(details, "url");

    public static string? TryParseDiagnosticKind(string? details) =>
        TryParseStringProperty(details, "kind");

    public static string? TryParseDiagnosticSubProfileName(string? details) =>
        TryParseStringProperty(details, "subProfileName");

    public static string? TryParseDiagnosticSubProfileId(string? details) =>
        TryParseStringProperty(details, "subProfileId");

    public static string FormatForDisplay(string message, string? details)
    {
        message = AdsPowerErrorMessageNormalizer.NormalizeForDisplay(message);
        if (!string.IsNullOrWhiteSpace(details) && !details.TrimStart().StartsWith('{'))
        {
            details = AdsPowerErrorMessageNormalizer.NormalizeForDisplay(details);
        }

        if (string.IsNullOrWhiteSpace(details))
        {
            return message;
        }

        if (!details.TrimStart().StartsWith('{'))
        {
            return message.Contains(details, StringComparison.OrdinalIgnoreCase)
                ? message
                : $"{message} — {details}";
        }

        var text = TryParseDiagnosticText(details);
        var url = TryParseDiagnosticUrl(details);
        var subProfile = TryParseDiagnosticSubProfileName(details);
        var expectedStep = TryParseStringProperty(details, "expectedStep");
        var actualStep = TryParseStringProperty(details, "actualStep");
        var pageStateSummary = TryParseStringProperty(details, "pageStateSummary");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(text.Trim());
        }

        if (!string.IsNullOrWhiteSpace(expectedStep) && !string.IsNullOrWhiteSpace(actualStep))
        {
            parts.Add($"ожидали: {expectedStep.Trim()} · факт: {actualStep.Trim()}");
        }
        else if (!string.IsNullOrWhiteSpace(pageStateSummary))
        {
            parts.Add(pageStateSummary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(subProfile))
        {
            parts.Add($"субпрофиль «{subProfile.Trim()}»");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            parts.Add(url.Trim());
        }

        if (parts.Count == 0)
        {
            return message;
        }

        var suffix = string.Join(" · ", parts);
        return message.Contains(suffix, StringComparison.OrdinalIgnoreCase)
            || parts.Any(part => message.Contains(part, StringComparison.OrdinalIgnoreCase))
            ? message
            : $"{message} — {suffix}";
    }

    private static string? TryParseStringProperty(string? details, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        var trimmed = details.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty(propertyName, out var element)
                && element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}