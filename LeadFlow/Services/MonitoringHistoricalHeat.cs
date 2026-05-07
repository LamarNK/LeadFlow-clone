namespace LeadFlow.Services;

/// <summary>
/// Оценка «жары» текущего момента по истории первого сохранения откликов в БД (локальный день недели и час).
/// </summary>
public static class MonitoringHistoricalHeat
{
    /// <summary>
    /// Возвращает 0…1: насколько текущий локальный слот (день недели ± соседние часы) богаче историческими приходами относительно самого загруженного слота.
    /// </summary>
    public static double ComputeScore(IReadOnlyList<DateTime> createdAtUtcSamples, DateTime utcNow, TimeZoneInfo localTz)
    {
        if (createdAtUtcSamples.Count < MonitoringTiming.CycleHistoricalHeatMinSamples)
        {
            return 0;
        }

        var buckets = new Dictionary<(int Dow, int Hour), int>();
        foreach (var raw in createdAtUtcSamples)
        {
            var utc = NormalizeUtc(raw);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, localTz);
            var key = ((int)local.DayOfWeek, local.Hour);
            buckets[key] = buckets.GetValueOrDefault(key) + 1;
        }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtc(utcNow), localTz);
        var dow = (int)nowLocal.DayOfWeek;
        var hour = nowLocal.Hour;

        double WeightedWindow(int d, int h)
        {
            var (d0, h0) = AddLocalHours(d, h, -1);
            var (d2, h2) = AddLocalHours(d, h, 1);
            var a = buckets.GetValueOrDefault((d0, h0));
            var c = buckets.GetValueOrDefault((d, h));
            var b = buckets.GetValueOrDefault((d2, h2));
            return 0.25 * a + 0.5 * c + 0.25 * b;
        }

        var maxWindow = 0.0;
        for (var d = 0; d < 7; d++)
        {
            for (var h = 0; h < 24; h++)
            {
                maxWindow = Math.Max(maxWindow, WeightedWindow(d, h));
            }
        }

        if (maxWindow <= 0)
        {
            return 0;
        }

        return Math.Clamp(WeightedWindow(dow, hour) / maxWindow, 0, 1);
    }

    private static (int Dow, int Hour) AddLocalHours(int dow, int hour, int deltaHours)
    {
        var h = hour + deltaHours;
        var d = dow;
        while (h < 0)
        {
            h += 24;
            d = (d + 6) % 7;
        }

        while (h > 23)
        {
            h -= 24;
            d = (d + 1) % 7;
        }

        return (d, h);
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
