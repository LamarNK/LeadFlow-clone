namespace LeadFlow.Models;

public sealed class MonitoringSafetyOptions
{
    /// <summary>Резерв под будущие сценарии; не используется основным циклом мониторинга.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>Минимальная случайная пауза между полными циклами обхода аккаунтов (минуты).</summary>
    public int CycleDelayMinMinutes { get; set; } = 1;

    /// <summary>Максимальная случайная пауза между полными циклами (минуты).</summary>
    public int CycleDelayMaxMinutes { get; set; } = 10;

    public int DelayBetweenAccountsSeconds { get; set; } = 10;
    public int DelayBetweenResponsesSeconds { get; set; } = 3;
    public int MaxResponsesPerCycle { get; set; } = 10;

    /// <summary>
    /// Период повторного парсинга страницы «Активные объявления» (минуты), после первого прогона при старте.
    /// </summary>
    public int ActiveAdsRefreshIntervalMinutes { get; set; } = 45;

    public bool StopOnCaptcha { get; set; } = true;
    public bool StopOnAuthRequired { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }
}
