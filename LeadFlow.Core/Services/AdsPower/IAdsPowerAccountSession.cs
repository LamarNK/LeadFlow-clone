using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Одна CDP-сессия AdsPower на полный проход аккаунта: switch → отклики → объявления без повторных browser/start.
/// </summary>
public interface IAdsPowerAccountSession : IAsyncDisposable
{
    string AdsPowerUserId { get; }

    Task<SubProfileSwitchResult> SwitchSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default);

    Task<bool> VerifyActiveSubProfileAsync(string subProfileId, CancellationToken cancellationToken = default);

    Task<string> ExtractCandidatesJsonAsync(
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null,
        CancellationToken cancellationToken = default);

    Task<string> LoadProfileItemsHtmlAsync(CancellationToken cancellationToken = default);

    Task<string> LoadBlockedItemsHtmlAsync(CancellationToken cancellationToken = default);

    /// <summary>Читает «Кошелёк» и «Аванс» из сайдбара Avito Pro на странице кабинета (не на откликах).</summary>
    Task<AvitoMoneySidebar?> TryReadMoneySidebarAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Выполняет ручное пополнение аванса: переход на <c>/account/advance</c>, ввод суммы,
    /// выбор СБП, переход к оплате и снятие QR-изображения. Оплату не выполняет.
    /// <paramref name="beforePayClickAsync"/> вызывается после выбора СБП и непосредственно
    /// перед кликом по оплате; возврат <c>false</c> прерывает сценарий без клика.
    /// <paramref name="reportProgressAsync"/> — необязательный статус для UI оператора.
    /// </summary>
    Task<AvitoAdvanceTopUpResult> RunAdvanceTopUpAsync(
        decimal amount,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? beforePayClickAsync = null,
        Func<string, CancellationToken, Task>? reportProgressAsync = null);

    /// <summary>HTML модалки «Выбор профиля» в текущей CDP-сессии.</summary>
    Task<string> CaptureProfileSwitchHtmlAsync(CancellationToken cancellationToken = default);

    string? CurrentPageUrl { get; }

    Task<AvitoPageState?> GetPageStateAsync(CancellationToken cancellationToken = default);

    Task<byte[]?> CapturePageScreenshotAsync(CancellationToken cancellationToken = default);

    Task<byte[]?> CapturePageJpegScreenshotAsync(CancellationToken cancellationToken = default);

    Task<BrowserMonitorScreencastCapture> CreateMonitorScreencastCaptureAsync(
        CancellationToken cancellationToken = default);
}