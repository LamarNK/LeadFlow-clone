using LeadFlow.Models;

namespace LeadFlow.Services;

/// <summary>Расчёт паузы между полными циклами мониторинга по настройкам.</summary>
public static class MonitoringCycleDelay
{
    public const int MinAllowedMinutes = 1;
    public const int MaxAllowedMinutes = 120;

    public static void NormalizeBounds(MonitoringSafetyOptions options)
    {
        options.CycleDelayMinMinutes = Math.Clamp(options.CycleDelayMinMinutes, MinAllowedMinutes, MaxAllowedMinutes);
        options.CycleDelayMaxMinutes = Math.Clamp(options.CycleDelayMaxMinutes, MinAllowedMinutes, MaxAllowedMinutes);
        if (options.CycleDelayMinMinutes > options.CycleDelayMaxMinutes)
        {
            (options.CycleDelayMinMinutes, options.CycleDelayMaxMinutes) =
                (options.CycleDelayMaxMinutes, options.CycleDelayMinMinutes);
        }
    }

    /// <summary>Случайная длительность паузы; границы читаются с clamp без изменения объекта настроек.</summary>
    public static TimeSpan GetRandomDelay(MonitoringSafetyOptions options, Random? random = null)
    {
        random ??= Random.Shared;
        var minM = Math.Clamp(options.CycleDelayMinMinutes, MinAllowedMinutes, MaxAllowedMinutes);
        var maxM = Math.Clamp(options.CycleDelayMaxMinutes, MinAllowedMinutes, MaxAllowedMinutes);
        if (minM > maxM)
        {
            (minM, maxM) = (maxM, minM);
        }

        var minSec = minM * 60;
        var maxSec = maxM * 60;
        return TimeSpan.FromSeconds(random.Next(minSec, maxSec + 1));
    }
}
