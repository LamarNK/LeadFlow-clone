using System.Globalization;

namespace Orbita.Web.Services;

internal static class DashboardRateFormatter
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static string Format(
        int count,
        DashboardPeriod period,
        DateTime aggregatedAtUtc,
        string unit)
    {
        var rate = CalculatePerHour(count, period, aggregatedAtUtc);
        return $"≈ {rate.ToString("0.#", DisplayCulture)} {unit}/ч";
    }

    public static double CalculatePerHour(
        int count,
        DashboardPeriod period,
        DateTime aggregatedAtUtc)
    {
        var elapsedHours = CalculateElapsedHours(period, aggregatedAtUtc);
        return count <= 0 ? 0 : count / elapsedHours;
    }

    public static double CalculateElapsedHours(
        DashboardPeriod period,
        DateTime aggregatedAtUtc)
    {
        var aggregatedUtc = aggregatedAtUtc.Kind switch
        {
            DateTimeKind.Utc => aggregatedAtUtc,
            DateTimeKind.Local => aggregatedAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(aggregatedAtUtc, DateTimeKind.Utc)
        };
        var effectiveEndUtc = aggregatedUtc >= period.FromUtc && aggregatedUtc < period.ToUtcExclusive
            ? aggregatedUtc
            : period.ToUtcExclusive;
        return Math.Max(1, (effectiveEndUtc - period.FromUtc).TotalHours);
    }
}
