using System.Diagnostics.CodeAnalysis;

namespace LeadFlow.Services.Bitrix;

public static class BitrixPortalLinks
{
    public static bool TryGetPortalBase(string? webhookUrl, [NotNullWhen(true)] out string? portalBase)
    {
        portalBase = null;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        portalBase = $"{uri.Scheme}://{uri.Authority}";
        return true;
    }

    /// <summary>Веб-URL карточки CRM (deal/lead/contact) по данным отклика и webhook.</summary>
    public static string? TryBuildEntityDetailsUrl(string? webhookUrl, string? entityType, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        if (!TryGetPortalBase(webhookUrl, out var baseUrl) || baseUrl is null)
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
