namespace LeadFlow.Services.AdsPower;

public interface IAdsPowerAvitoAuthService
{
    /// <summary>
    /// Запускает браузер AdsPower, переходит на <c>https://www.avito.ru/profile/basic</c>
    /// и определяет: авторизован ли пользователь и какое имя показано в шапке.
    /// При успешной авторизации в той же сессии читает суб-профили Avito Pro.
    /// Браузер остаётся открытым, если нужен вход или капча; иначе закрывается через Local API.
    /// </summary>
    Task<AdsPowerAvitoAuthResult> CheckAuthorizationAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default);
}
