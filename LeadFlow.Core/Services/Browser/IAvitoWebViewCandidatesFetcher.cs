using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Browser;

/// <summary>
/// Опциональная реализация в UI-приложении (WebView2) для локальных профилей без AdsPower.
/// Headless-воркер использует только AdsPower/CDP-путь.
/// </summary>
public interface IAvitoWebViewCandidatesFetcher
{
    Task<IReadOnlyList<CandidateResponse>> FetchNewResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken);
}