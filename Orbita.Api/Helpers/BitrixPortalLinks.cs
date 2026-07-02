using System.Diagnostics.CodeAnalysis;

namespace Orbita.Api.Helpers;

public static class BitrixPortalLinks
{
    public static bool TryGetPortalBase(string? portalOrWebhookUrl, [NotNullWhen(true)] out string? portalBase)
    {
        portalBase = null;
        if (string.IsNullOrWhiteSpace(portalOrWebhookUrl))
        {
            return false;
        }

        var trimmed = portalOrWebhookUrl.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            portalBase = $"{uri.Scheme}://{uri.Authority}";
            return true;
        }

        if (!trimmed.Contains('/', StringComparison.Ordinal))
        {
            portalBase = $"https://{trimmed.TrimEnd('/')}";
            return true;
        }

        return false;
    }

    public static string? TryBuildEntityDetailsUrl(string? portalHost, string? entityType, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        if (!TryGetPortalBase(portalHost, out var baseUrl) || baseUrl is null)
        {
            return null;
        }

        var t = (entityType ?? "Deal").Trim();
        var slug = t.Equals("Lead", StringComparison.OrdinalIgnoreCase) ? "lead"
            : t.Equals("Contact", StringComparison.OrdinalIgnoreCase) ? "contact"
            : "deal";

        return $"{baseUrl.TrimEnd('/')}/crm/{slug}/details/{Uri.EscapeDataString(entityId)}/";
    }
}