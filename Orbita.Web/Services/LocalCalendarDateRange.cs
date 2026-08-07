namespace Orbita.Web.Services;

/// <summary>
/// Converts browser-selected local calendar dates to a half-open UTC range.
/// Uses the browser offset on <see cref="DashboardPeriod"/> — never the server OS zone.
/// </summary>
internal static class LocalCalendarDateRange
{
    public static (DateTime UtcStartInclusive, DateTime UtcEndExclusive) ToUtcRange(DashboardPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return (period.FromUtc, period.ToUtcExclusive);
    }
}
