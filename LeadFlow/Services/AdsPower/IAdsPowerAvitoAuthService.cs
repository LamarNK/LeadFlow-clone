namespace LeadFlow.Services.AdsPower;

public interface IAdsPowerAvitoAuthService
{
    /// <summary>
    /// Запускает браузер AdsPower, переходит на <c>https://www.avito.ru/profile</c>
    /// и определяет: авторизован ли пользователь и какое имя показано в шапке.
    /// После проверки Puppeteer отключается, браузер AdsPower закрывается через Local API.
    /// </summary>
    Task<AdsPowerAvitoAuthResult> CheckAuthorizationAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);
}
