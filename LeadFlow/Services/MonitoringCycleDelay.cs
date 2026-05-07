namespace LeadFlow.Services;

/// <summary>Случайная пауза между полными циклами мониторинга.</summary>
public static class MonitoringCycleDelay
{
    public static TimeSpan GetRandomDelay(Random? random = null)
    {
        random ??= Random.Shared;
        var minSec = MonitoringTiming.CycleDelayMinMinutes * 60;
        var maxSec = MonitoringTiming.CycleDelayMaxMinutes * 60;
        return TimeSpan.FromSeconds(random.Next(minSec, maxSec + 1));
    }
}
