namespace LeadFlow.Services.AdsPower;

public interface IAdsPowerAvitoAutomationService
{
    /// <summary>
    /// Открывает в AdsPower-браузере страницу <c>/profile/candidates</c> и возвращает JSON с откликами,
    /// в формате, ожидаемом <see cref="LeadFlow.Services.Avito.AvitoCandidatesJsonParser"/>.
    /// </summary>
    Task<string> ExtractCandidatesJsonAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null);

    /// <summary>
    /// Открывает в AdsPower-браузере страницу <c>/profile/pro/items</c> и возвращает её HTML
    /// в виде, пригодном для <see cref="LeadFlow.Services.Avito.AvitoParserService.ParseProfilePage(string, System.Guid?)"/>.
    /// После извлечения отсоединяемся по CDP и закрываем браузер AdsPower.
    /// </summary>
    Task<string> LoadProfileItemsHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Открывает вкладку «С ошибками» (<c>tabs=rejected</c>) и возвращает её HTML — список
    /// заблокированных/отклонённых вакансий.
    /// </summary>
    Task<string> LoadBlockedItemsHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Открывает модалку <c>/profile/dashboard#profile/switch?withEntities=true</c> и возвращает HTML
    /// со списком всех суб-профилей Avito Pro (data-marker="component-profile-switch/profile-{id}").
    /// </summary>
    Task<string> LoadProfileSwitchHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// В уже подключённой CDP-сессии открывает модалку «Выбор профиля» и возвращает её HTML
    /// (без повторного browser/start).
    /// </summary>
    Task<string> CaptureProfileSwitchHtmlInSessionAsync(
        PuppeteerSharp.IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Переключает активный суб-профиль: открывает модалку, кликает по карточке указанного профиля
    /// и ждёт исчезновения модалки. Возвращает <c>true</c>, если переключение подтверждено.
    /// </summary>
    Task<bool> SwitchActiveProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string subProfileId,
        CancellationToken cancellationToken = default,
        bool closeBrowserAfter = false);

    /// <summary>
    /// Подключается к уже запущенному профилю AdsPower и открывает URL в новой вкладке через CDP.
    /// Не полагается на <c>open_urls</c> в <c>browser/start</c> при повторном вызове — вкладка часто не создаётся.
    /// </summary>
    Task OpenUrlInRunningProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string url,
        CancellationToken cancellationToken = default,
        bool closeBrowserAfter = false);

    /// <summary>
    /// Одна CDP-сессия на полный проход аккаунта: switch → отклики → объявления без повторных browser/start.
    /// Браузер закрывается отдельно через <see cref="CloseBrowserAsync"/> после цикла мониторинга.
    /// </summary>
    Task<IAdsPowerAccountSession> OpenAccountSessionAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Закрывает браузер профиля AdsPower (один раз после полного прохода аккаунта в мониторинге).
    /// </summary>
    Task CloseBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);
}
