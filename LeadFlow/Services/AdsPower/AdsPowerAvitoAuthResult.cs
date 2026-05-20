using LeadFlow.Models;

namespace LeadFlow.Services.AdsPower;

/// <summary>
/// Результат проверки авторизации Avito в браузере AdsPower.
/// </summary>
/// <param name="IsAuthorized">Пользователь точно залогинен в Avito (нет формы входа и удалось прочитать имя профиля).</param>
/// <param name="ProfileName">Имя пользователя со страницы Avito (если удалось распарсить).</param>
/// <param name="CurrentUrl">Финальный URL страницы после редиректов (диагностика).</param>
/// <param name="HasLoginForm">На странице обнаружена форма входа.</param>
/// <param name="HasCaptcha">Avito показал капчу/проверку (требуется ручное действие).</param>
/// <param name="ErrorMessage">Текст ошибки, если проверку не удалось выполнить.</param>
/// <param name="KeepBrowserOpen">Не вызывать browser/stop — пользователь должен войти или пройти капчу в открытом окне AdsPower.</param>
/// <param name="SubProfilesParsed">Список суб-профилей успешно прочитан в той же CDP-сессии (даже если список пуст).</param>
/// <param name="SubProfiles">Суб-профили Avito Pro из модалки «Выбор профиля» (если <see cref="SubProfilesParsed"/>).</param>
public sealed record AdsPowerAvitoAuthResult(
    bool IsAuthorized,
    string? ProfileName,
    string? CurrentUrl,
    bool HasLoginForm,
    bool HasCaptcha,
    string? ErrorMessage,
    bool KeepBrowserOpen = false,
    bool SubProfilesParsed = false,
    IReadOnlyList<AvitoSubProfile>? SubProfiles = null);
