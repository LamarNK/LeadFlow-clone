namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Avito Pro показывает отклики в двух вариантах: legacy <c>/profile/candidates</c> и CRM <c>/profile/job/responses</c>.
/// </summary>
public static class AvitoCandidatesPageUrls
{
    public const string LegacyCandidates = "https://www.avito.ru/profile/candidates";
    public const string JobResponsesCrm = "https://www.avito.ru/profile/job/responses";

    public static readonly string[] NavigationOrder = [LegacyCandidates, JobResponsesCrm];

    public static bool IsCandidatesResponsesUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/profile/job/responses", StringComparison.OrdinalIgnoreCase);
    }
}