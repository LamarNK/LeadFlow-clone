namespace NotifyBot.Application.Options;

public sealed class PlusofonOptions
{
    public const string SectionName = "Plusofon";

    /// <summary>Заголовок Client для Plusofon API — всегда 10553 (см. документацию).</summary>
    public const string DefaultClientId = "10553";

    public string ApiBaseUrl { get; set; } = "https://restapi.plusofon.ru";

    public string ClientId { get; set; } = DefaultClientId;

    /// <summary>Ключ доступа из ЛК: Разработчикам → Доступ к API → Ключ доступа.</summary>
    public string ApiToken { get; set; } = string.Empty;

    public int SmsFetchLimit { get; set; } = 20;

    /// <summary>Максимальный возраст 3DS-SMS для /sms (минуты).</summary>
    public int SmsMaxAgeMinutes { get; set; } = 15;

    /// <summary>Сколько минут ждать SMS после /sms, если кода ещё нет.</summary>
    public int SmsWatchDurationMinutes { get; set; } = 5;

    /// <summary>Интервал опроса Plusofon, пока есть активные мониторинги (секунды).</summary>
    public int SmsWatchPollIntervalSeconds { get; set; } = 5;

    /// <summary>Пауза фонового цикла, когда мониторингов нет (секунды).</summary>
    public int SmsWatchIdleIntervalSeconds { get; set; } = 15;

    public string Secret { get; set; } = string.Empty;

    public bool WebhookValidation { get; set; } = true;

    public string ResolvedClientId =>
        string.IsNullOrWhiteSpace(ClientId) ? DefaultClientId : ClientId;

    public bool IsApiConfigured => !string.IsNullOrWhiteSpace(ApiToken);
}