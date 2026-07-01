using LeadFlow.Core.Services.Avito;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Одна CDP-сессия AdsPower на полный проход аккаунта: switch → отклики → объявления без повторных browser/start.
/// </summary>
public interface IAdsPowerAccountSession : IAsyncDisposable
{
    string AdsPowerUserId { get; }

    Task<bool> SwitchSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default);

    Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default);

    Task<string> ExtractCandidatesJsonAsync(
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
        CancellationToken cancellationToken = default);

    Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default);

    Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default);

    /// <summary>Читает «Аванс» из сайдбара Avito Pro на странице кабинета (не на откликах).</summary>
    Task<decimal?> TryReadAdvanceBalanceAsync(CancellationToken cancellationToken = default);

    /// <summary>HTML модалки «Выбор профиля» в текущей CDP-сессии.</summary>
    Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default);

    string? CurrentPageUrl { get; }

    Task<AvitoPageState?> GetPageStateAsync(CancellationToken cancellationToken = default);

    Task<byte[]?> CapturePageScreenshotAsync(CancellationToken cancellationToken = default);
}