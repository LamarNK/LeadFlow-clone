using System.Globalization;

namespace Orbita.Web.Services;

/// <summary>
/// Inclusive local calendar-day range selected in the browser.
/// <see cref="TimeZoneOffsetMinutes"/> is JS <c>getTimezoneOffset()</c>
/// (minutes to add to local wall time to obtain UTC). Storage and filters use
/// <see cref="FromUtc"/> / <see cref="ToUtcExclusive"/> only.
/// </summary>
public sealed record DashboardPeriod(DateTime From, DateTime To, int TimeZoneOffsetMinutes = 0)
{
    public const int MaxDays = 366;

    public static DashboardPeriod Today => CreateToday(0);

    public static DashboardPeriod All => CreateAll(0);

    public static DashboardPeriod CreateToday(int timeZoneOffsetMinutes, DateTime? utcNow = null)
    {
        var today = GetLocalCalendarDate(utcNow ?? DateTime.UtcNow, timeZoneOffsetMinutes);
        return new DashboardPeriod(today, today, timeZoneOffsetMinutes);
    }

    public static DashboardPeriod CreateAll(int timeZoneOffsetMinutes, DateTime? utcNow = null)
    {
        var today = GetLocalCalendarDate(utcNow ?? DateTime.UtcNow, timeZoneOffsetMinutes);
        return new DashboardPeriod(today.AddDays(-(MaxDays - 1)), today, timeZoneOffsetMinutes);
    }

    public static DashboardPeriod CreateLastDays(int inclusiveDays, int timeZoneOffsetMinutes, DateTime? utcNow = null)
    {
        var days = Math.Clamp(inclusiveDays, 1, MaxDays);
        var today = GetLocalCalendarDate(utcNow ?? DateTime.UtcNow, timeZoneOffsetMinutes);
        return new DashboardPeriod(today.AddDays(-(days - 1)), today, timeZoneOffsetMinutes);
    }

    public static DashboardPeriod Parse(string? from, string? to, int timeZoneOffsetMinutes = 0, DateTime? utcNow = null)
    {
        if (!TryParseDate(from, out var parsedFrom) || !TryParseDate(to, out var parsedTo))
        {
            return CreateToday(timeZoneOffsetMinutes, utcNow);
        }

        if (parsedFrom > parsedTo)
        {
            (parsedFrom, parsedTo) = (parsedTo, parsedFrom);
        }

        var today = GetLocalCalendarDate(utcNow ?? DateTime.UtcNow, timeZoneOffsetMinutes);
        if (parsedTo > today)
        {
            parsedTo = today;
        }

        if (parsedFrom > parsedTo)
        {
            parsedFrom = parsedTo;
        }

        var inclusiveDays = (parsedTo - parsedFrom).Days + 1;
        if (inclusiveDays > MaxDays)
        {
            // Keep the start day; shrink the end (same as historical behavior).
            parsedTo = parsedFrom.AddDays(MaxDays - 1);
            var todayAfterClamp = GetLocalCalendarDate(utcNow ?? DateTime.UtcNow, timeZoneOffsetMinutes);
            if (parsedTo > todayAfterClamp)
            {
                parsedTo = todayAfterClamp;
            }
        }

        return new DashboardPeriod(parsedFrom, parsedTo, timeZoneOffsetMinutes);
    }

    /// <summary>
    /// Local calendar date for <paramref name="utcNow"/> given a JS timezone offset.
    /// </summary>
    public static DateTime GetLocalCalendarDate(DateTime utcNow, int timeZoneOffsetMinutes)
    {
        var utc = utcNow.Kind switch
        {
            DateTimeKind.Utc => utcNow,
            DateTimeKind.Local => utcNow.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)
        };

        // JS: local = utc - getTimezoneOffset() minutes
        var local = utc.AddMinutes(-timeZoneOffsetMinutes);
        return DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Instant of local midnight for the given calendar day, as UTC.
    /// </summary>
    public static DateTime LocalDateStartToUtc(DateTime localCalendarDate, int timeZoneOffsetMinutes)
    {
        var localMidnight = new DateTime(
            localCalendarDate.Year,
            localCalendarDate.Month,
            localCalendarDate.Day,
            0,
            0,
            0,
            DateTimeKind.Unspecified);
        // JS: utc = local + getTimezoneOffset() minutes
        return DateTime.SpecifyKind(localMidnight.AddMinutes(timeZoneOffsetMinutes), DateTimeKind.Utc);
    }

    public DateTime FromUtc => LocalDateStartToUtc(From, TimeZoneOffsetMinutes);

    public DateTime ToUtcExclusive => LocalDateStartToUtc(To.AddDays(1), TimeZoneOffsetMinutes);

    public DateTime LocalToday => GetLocalCalendarDate(DateTime.UtcNow, TimeZoneOffsetMinutes);

    public bool IsTodayOnly
    {
        get
        {
            var today = LocalToday;
            return From == today && To == today;
        }
    }

    public bool IsAllTime => ActivePreset == "all";

    public bool IsSingleDay => From == To;

    public string Label => ActivePreset switch
    {
        "all" => "Все",
        _ when IsSingleDay => From.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        _ => $"{From:dd.MM.yyyy} — {To:dd.MM.yyyy}"
    };

    public string? ActivePreset
    {
        get
        {
            var today = LocalToday;
            if (From == today && To == today) return "today";
            if (From == today.AddDays(-1) && To == today.AddDays(-1)) return "yesterday";
            if (From == today.AddDays(-6) && To == today) return "7d";
            if (From == today.AddDays(-13) && To == today) return "14d";
            if (From == today.AddDays(-29) && To == today) return "30d";
            if (From == today.AddDays(-(MaxDays - 1)) && To == today) return "all";
            return null;
        }
    }

    public string FromIso => From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string ToIso => To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
    }
}
