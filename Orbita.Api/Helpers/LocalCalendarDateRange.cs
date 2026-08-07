namespace Orbita.Api.Helpers;

/// <summary>
/// Calendar-day helpers. Stored timestamps stay UTC.
/// <para>
/// When <c>timeZoneOffsetMinutes</c> is provided it uses the browser convention
/// (JS <c>Date#getTimezoneOffset()</c>: minutes to add to local wall time to get UTC).
/// When omitted (<c>null</c>), falls back to the process OS zone — used by worker-side
/// reports that have no browser context.
/// </para>
/// </summary>
public static class LocalCalendarDateRange
{
    public const int MaxCalendarDays = 366;

    /// <summary>JS-style offset for a zone at the given instant.</summary>
    public static int ToJsOffsetMinutes(TimeZoneInfo zone, DateTime utcInstant)
    {
        var utc = AssumeUtc(utcInstant);
        return (int)Math.Round(-zone.GetUtcOffset(utc).TotalMinutes);
    }

    public static int ResolveOffsetMinutes(int? timeZoneOffsetMinutes, DateTime? utcInstant = null) =>
        timeZoneOffsetMinutes
        ?? ToJsOffsetMinutes(TimeZoneInfo.Local, utcInstant ?? DateTime.UtcNow);

    public static DateTime GetLocalCalendarDate(DateTime utcNow, int? timeZoneOffsetMinutes = null)
    {
        var offset = ResolveOffsetMinutes(timeZoneOffsetMinutes, utcNow);
        var utc = AssumeUtc(utcNow);
        return DateTime.SpecifyKind(utc.AddMinutes(-offset).Date, DateTimeKind.Unspecified);
    }

    public static DateTime LocalDateStartToUtc(DateTime localCalendarDate, int? timeZoneOffsetMinutes = null)
    {
        var offset = ResolveOffsetMinutes(
            timeZoneOffsetMinutes,
            DateTime.SpecifyKind(localCalendarDate.Date, DateTimeKind.Utc));
        var localMidnight = new DateTime(
            localCalendarDate.Year,
            localCalendarDate.Month,
            localCalendarDate.Day,
            0,
            0,
            0,
            DateTimeKind.Unspecified);
        return DateTime.SpecifyKind(localMidnight.AddMinutes(offset), DateTimeKind.Utc);
    }

    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) GetUtcRangeForLocalCalendarDay(
        DateTime localCalendarDate,
        int? timeZoneOffsetMinutes = null)
    {
        if (timeZoneOffsetMinutes is null)
        {
            // Worker/server reports without a browser offset: keep OS-local day boundaries.
            var zone = TimeZoneInfo.Local;
            var localStart = new DateTime(
                localCalendarDate.Year,
                localCalendarDate.Month,
                localCalendarDate.Day,
                0,
                0,
                0,
                DateTimeKind.Unspecified);
            var localEnd = localStart.AddDays(1);
            return (
                TimeZoneInfo.ConvertTimeToUtc(localStart, zone),
                TimeZoneInfo.ConvertTimeToUtc(localEnd, zone));
        }

        var utcStart = LocalDateStartToUtc(localCalendarDate, timeZoneOffsetMinutes);
        var utcEnd = LocalDateStartToUtc(localCalendarDate.AddDays(1), timeZoneOffsetMinutes);
        return (utcStart, utcEnd);
    }

    public static (DateTime StartLocal, DateTime EndLocal, DateTime UtcStartInclusive, DateTime UtcEndExclusive) Normalize(
        DateTime? from,
        DateTime? to,
        int? timeZoneOffsetMinutes = null,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var today = GetLocalCalendarDate(now, timeZoneOffsetMinutes);
        var start = (from ?? today).Date;
        var end = (to ?? today).Date;

        if (end > today)
        {
            end = today;
        }

        if (start > end)
        {
            (start, end) = (end, start);
        }

        if ((end - start).Days + 1 > MaxCalendarDays)
        {
            start = end.AddDays(-(MaxCalendarDays - 1));
        }

        var utcStart = GetUtcRangeForLocalCalendarDay(start, timeZoneOffsetMinutes).UtcStartInclusive;
        var utcEnd = GetUtcRangeForLocalCalendarDay(end, timeZoneOffsetMinutes).UtcEndExclusive;
        return (start, end, utcStart, utcEnd);
    }

    public static DateTime ToLocalDateFromStoredUtc(DateTime value, int? timeZoneOffsetMinutes = null)
    {
        var utc = AssumeUtc(value);
        if (timeZoneOffsetMinutes is null)
        {
            return utc.ToLocalTime().Date;
        }

        return DateTime.SpecifyKind(utc.AddMinutes(-timeZoneOffsetMinutes.Value).Date, DateTimeKind.Unspecified);
    }

    private static DateTime AssumeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
