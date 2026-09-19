namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Вариант DOM-страницы откликов. URL <c>/profile/candidates</c> больше не означает
/// классический список: Avito отдаёт там Job CRM в компактном или подробном виде.
/// </summary>
public static class AvitoResponsesPageVariant
{
    public const string JobCrmDetailed = "job-crm-detailed";
    public const string JobCrmCompact = "job-crm-compact";
    /// <summary>Старое значение extraction до различения compact/detailed.</summary>
    public const string JobCrm = "job-crm";
    public const string Legacy = "legacy";
    public const string Unknown = "unknown";

    public static bool IsJobCrm(string? pageVariant)
    {
        if (string.IsNullOrWhiteSpace(pageVariant))
        {
            return false;
        }

        return pageVariant.StartsWith("job-crm", StringComparison.OrdinalIgnoreCase);
    }

    public static string Describe(string? pageUrl, string? pageVariant)
    {
        var variant = pageVariant?.Trim().ToLowerInvariant();
        if (IsJobCrm(variant))
        {
            return variant switch
            {
                JobCrmDetailed => "CRM подробный вид",
                JobCrmCompact => "CRM компактный список",
                _ => "CRM"
            };
        }

        if (string.Equals(variant, Legacy, StringComparison.Ordinal))
        {
            return "классическая (/profile/candidates)";
        }

        if (!string.IsNullOrWhiteSpace(pageUrl))
        {
            if (pageUrl.Contains("/profile/job/responses", StringComparison.OrdinalIgnoreCase))
            {
                return "CRM";
            }

            if (pageUrl.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase))
            {
                return "классическая (/profile/candidates)";
            }

            if (string.IsNullOrWhiteSpace(variant) || string.Equals(variant, Unknown, StringComparison.Ordinal))
            {
                return $"неизвестный URL ({pageUrl})";
            }
        }

        return string.IsNullOrWhiteSpace(pageUrl) ? "не определено" : $"неизвестный URL ({pageUrl})";
    }
}
