namespace LeadFlow.Models;

public sealed class MonitoringSafetyOptions
{
    public int CheckIntervalSeconds { get; set; } = 60;
    public int DelayBetweenAccountsSeconds { get; set; } = 10;
    public int DelayBetweenResponsesSeconds { get; set; } = 3;
    public int MaxResponsesPerCycle { get; set; } = 10;

    /// <summary>
    /// Период повторного парсинга страницы «Активные объявления» (минуты), после первого прогона при старте.
    /// </summary>
    public int ActiveAdsRefreshIntervalMinutes { get; set; } = 45;

    public int PauseOnErrorMinutes { get; set; } = 5;
    public bool StopOnCaptcha { get; set; } = true;
    public bool StopOnAuthRequired { get; set; } = true;
    public bool AutoStartMonitoring { get; set; }
}
