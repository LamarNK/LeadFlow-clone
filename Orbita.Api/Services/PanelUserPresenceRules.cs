using Orbita.Contracts;

namespace Orbita.Api.Services;

public static class PanelUserPresenceRules
{
    /// <summary>
    /// Browser heartbeats are sent once per minute. The margin keeps a user online
    /// through a transient failed request without treating a persistent login cookie
    /// as an active session.
    /// </summary>
    public static readonly TimeSpan OnlineThreshold = TimeSpan.FromMinutes(5);

    public const int LookbackDays = 14;
    public const int RetentionDays = 30;

    public static TimeZoneInfo MoscowTimeZone { get; } = ResolveMoscow();

    public static bool IsOnline(DateTime? lastSeenAtUtc, DateTime nowUtc) =>
        lastSeenAtUtc.HasValue
        && lastSeenAtUtc.Value <= nowUtc
        && nowUtc - lastSeenAtUtc.Value <= OnlineThreshold;

    public static DateTime TruncateToUtcHour(DateTime utc)
    {
        var value = AssumeUtc(utc);
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
    }

    public static PanelUserPresenceHourSeriesDto BuildHourSeries(
        IReadOnlyList<DateTime> userHourUtcSamples,
        DateTime nowUtc,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? MoscowTimeZone;
        var now = AssumeUtc(nowUtc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var today = DateOnly.FromDateTime(localNow);
        var lookbackStart = today.AddDays(1 - LookbackDays);

        var typicalSums = new int[24];
        var todayCounts = new int[24];
        var days = new HashSet<DateOnly>();

        foreach (var sample in userHourUtcSamples)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(AssumeUtc(sample), zone);
            var date = DateOnly.FromDateTime(local);
            if (date < lookbackStart || date > today)
            {
                continue;
            }

            typicalSums[local.Hour]++;
            days.Add(date);
            if (date == today)
            {
                todayCounts[local.Hour]++;
            }
        }

        var sampleDays = days.Count;
        var divisor = Math.Max(1, sampleDays);
        var typical = new int[24];
        for (var hour = 0; hour < 24; hour++)
        {
            typical[hour] = (int)Math.Round(typicalSums[hour] / (double)divisor, MidpointRounding.AwayFromZero);
        }

        var typicalPeakHour = IndexOfPeak(typicalSums);
        var todayPeakHour = IndexOfPeak(todayCounts);

        return new PanelUserPresenceHourSeriesDto(
            typical,
            todayCounts,
            typicalPeakHour,
            typical[typicalPeakHour],
            todayPeakHour,
            todayCounts[todayPeakHour],
            sampleDays,
            localNow.Hour,
            now);
    }

    private static int IndexOfPeak(IReadOnlyList<int> values)
    {
        var peakHour = 0;
        var peakValue = values[0];
        for (var hour = 1; hour < values.Count; hour++)
        {
            if (values[hour] > peakValue)
            {
                peakHour = hour;
                peakValue = values[hour];
            }
        }

        return peakHour;
    }

    private static DateTime AssumeUtc(DateTime value) => value.Kind switch
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
