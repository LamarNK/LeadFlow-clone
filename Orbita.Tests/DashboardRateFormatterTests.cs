using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class DashboardRateFormatterTests
{
    [Fact]
    public void CalculatePerHour_Today_UsesElapsedLocalDay()
    {
        var period = new DashboardPeriod(
            new DateTime(2026, 9, 12),
            new DateTime(2026, 9, 12),
            TimeZoneOffsetMinutes: -300);
        var aggregatedAtUtc = new DateTime(2026, 9, 12, 7, 0, 0, DateTimeKind.Utc);

        var rate = DashboardRateFormatter.CalculatePerHour(120, period, aggregatedAtUtc);

        Assert.Equal(10, rate);
    }

    [Fact]
    public void CalculatePerHour_PastDay_UsesFullTwentyFourHours()
    {
        var period = new DashboardPeriod(
            new DateTime(2026, 9, 11),
            new DateTime(2026, 9, 11),
            TimeZoneOffsetMinutes: -300);
        var aggregatedAtUtc = new DateTime(2026, 9, 12, 7, 0, 0, DateTimeKind.Utc);

        var rate = DashboardRateFormatter.CalculatePerHour(48, period, aggregatedAtUtc);

        Assert.Equal(2, rate);
    }

    [Fact]
    public void Format_UsesCompactRussianDecimalAndUnit()
    {
        var period = new DashboardPeriod(
            new DateTime(2026, 9, 12),
            new DateTime(2026, 9, 12),
            TimeZoneOffsetMinutes: -300);
        var aggregatedAtUtc = new DateTime(2026, 9, 12, 7, 0, 0, DateTimeKind.Utc);

        var text = DashboardRateFormatter.Format(125, period, aggregatedAtUtc, "откл.");

        Assert.Equal("≈ 10,4 откл./ч", text);
    }
}
