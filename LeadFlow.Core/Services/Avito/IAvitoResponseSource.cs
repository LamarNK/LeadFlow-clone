using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public interface IAvitoResponseSource
{
    Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Парсит JSON откликов, уже полученный из CDP/WebView, без повторного открытия браузера.
    /// </summary>
    Task<IReadOnlyList<CandidateResponse>> ParseCandidatesFromRawAsync(
        AvitoAccount account,
        AppSettings settings,
        string rawExtractionJson,
        CancellationToken cancellationToken,
        AvitoSubProfile? activeSubProfile = null);

    Task<AvitoCandidatesParseResult> ParseCandidatesDetailedFromRawAsync(
        AvitoAccount account,
        AppSettings settings,
        string rawExtractionJson,
        CancellationToken cancellationToken,
        AvitoSubProfile? activeSubProfile = null);

    Task<HashSet<string>> ResolveExistingPhonesAsync(
        Guid accountId,
        DuplicateScope duplicateScope,
        IEnumerable<string> phoneCandidates,
        CancellationToken cancellationToken,
        string? avitoSubProfileId = null);
}
