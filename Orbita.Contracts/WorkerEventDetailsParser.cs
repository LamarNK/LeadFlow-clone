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
            if (doc.RootElement.TryGetProperty("attachmentId", out var idElement)
                && Guid.TryParse(idElement.GetString(), out var attachmentId))
            {
                return attachmentId;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}