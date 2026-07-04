namespace Orbita.Api.Helpers;

public static class LocalCalendarDateRange
{
    public const int MaxCalendarDays = 366;

    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) GetUtcRangeForLocalCalendarDay(
        DateTime localCalendarDate)
    {
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
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, zone);
        return (utcStart, utcEnd);
    }

    public static (DateTime StartLocal, DateTime EndLocal, DateTime UtcStartInclusive, DateTime UtcEndExclusive) Normalize(
        DateTime? from,
        DateTime? to)
    {
        var today = DateTime.Today;
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

        var utcStart = GetUtcRangeForLocalCalendarDay(start).UtcStartInclusive;
        var utcEnd = GetUtcRangeForLocalCalendarDay(end).UtcEndExclusive;
        return (start, end, utcStart, utcEnd);
    }

    public static DateTime ToLocalDateFromStoredUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.ToLocalTime().Date;
    }
}