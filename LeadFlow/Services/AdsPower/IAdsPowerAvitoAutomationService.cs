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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Открывает в AdsPower-браузере страницу <c>/profile/pro/items</c> и возвращает её HTML
    /// в виде, пригодном для <see cref="LeadFlow.Services.Avito.AvitoParserService.ParseProfilePage(string, System.Guid?)"/>.
    /// Браузер не закрывается — после извлечения только отсоединяемся по CDP.
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
    /// Открывает модалку <c>/profile/pro/items#profile/switch?withEntities=true</c> и возвращает HTML
    /// со списком всех суб-профилей Avito Pro (data-marker="component-profile-switch/profile-{id}").
    /// </summary>
    Task<string> LoadProfileSwitchHtmlAsync(
        AdsPowerConnectionOptions options,
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
        CancellationToken cancellationToken = default);
}
