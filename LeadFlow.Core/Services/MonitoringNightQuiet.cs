namespace LeadFlow.Core.Services;

/// <summary>
/// Ночная пауза мониторинга по московскому времени: не отменяем проход,
/// но не даём крутить кабинет каждые 3–20 минут.
/// </summary>
public static class MonitoringNightQuiet
{
    public static TimeZoneInfo MoscowTimeZone { get; } = ResolveMoscow();

    public static bool IsActive(DateTime utcNow, TimeZoneInfo? timeZone = null)
    {
        var utc = NormalizeUtc(utcNow);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone ?? MoscowTimeZone);
        var hour = local.Hour;
        return hour >= MonitoringTiming.NightQuietStartHourInclusive
               || hour < MonitoringTiming.NightQuietEndHourExclusive;
    }

    public static TimeSpan ApplyFloor(
        TimeSpan delay,
        DateTime utcNow,
        TimeZoneInfo? timeZone = null,
        int? floorMinutes = null)
    {
        if (!IsActive(utcNow, timeZone))
        {
            return delay;
        }

        var floor = floorMinutes
            ?? Random.Shared.Next(
                MonitoringTiming.NightQuietDelayMinMinutes,
                MonitoringTiming.NightQuietDelayMaxMinutes + 1);
        return TimeSpan.FromMinutes(Math.Max(delay.TotalMinutes, floor));
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static TimeZoneInfo ResolveMoscow()
    {
        foreach (var id in new[] { "Europe/Moscow", "Russian Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "MSK", "MSK");
    }
}
