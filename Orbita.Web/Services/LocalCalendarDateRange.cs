namespace Orbita.Web.Services;

/// <summary>
/// Converts UI calendar dates to the same half-open UTC range used by the API's
/// LocalCalendarDateRange. Calendar dates are deliberately treated as unspecified
/// before applying the panel's local time zone.
/// </summary>
internal static class LocalCalendarDateRange
{
    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) ToUtcRange(
        DashboardPeriod period,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(period);
        var zone = timeZone ?? TimeZoneInfo.Local;
        var localStart = DateTime.SpecifyKind(period.From.Date, DateTimeKind.Unspecified);
        var localEnd = DateTime.SpecifyKind(period.To.Date.AddDays(1), DateTimeKind.Unspecified);
        return (
            TimeZoneInfo.ConvertTimeToUtc(localStart, zone),
            TimeZoneInfo.ConvertTimeToUtc(localEnd, zone));
    }
}
