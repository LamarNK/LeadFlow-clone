namespace LeadFlow.Services;

/// <summary>Параметры мониторинга откликов; не выставляются в UI, задаются в коде.</summary>
public static class MonitoringTiming
{
    public const int CycleDelayMinMinutes = 1;
    public const int CycleDelayMaxMinutes = 10;
    public const int DelayBetweenAccountsSeconds = 10;
    public const int DelayBetweenResponsesSeconds = 3;
    public const int MaxResponsesPerAccountPerCycle = 10;
    public const int ActiveAdsRefreshIntervalMinutes = 45;
}
