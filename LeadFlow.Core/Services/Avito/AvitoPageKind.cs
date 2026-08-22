namespace LeadFlow.Core.Services.Avito;

/// <summary>Классификация текущего экрана Avito Pro в CDP-сессии (URL + DOM, не только URL).</summary>
public enum AvitoPageKind
{
    Unknown = 0,
    Candidates,
    Dashboard,
    ProfileItems,
    ProfileSwitchModal,
    Login,
    Captcha,
    /// <summary>Заглушка Avito «Ошибка / обновите страницу» — часто из-за зависшего прокси.</summary>
    TransientError
}