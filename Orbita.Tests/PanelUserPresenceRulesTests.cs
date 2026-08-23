using Orbita.Api.Services;

namespace Orbita.Tests;

public sealed class PanelUserPresenceRulesTests
{
    [Fact]
    public void IsOnline_WhenLastActivityIsWithinThreshold()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(PanelUserPresenceRules.IsOnline(now.AddMinutes(-5), now));
    }

    [Fact]
    public void IsOnline_WhenLastActivityIsStaleOrMissing_ReturnsFalse()
    {
        var now = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(PanelUserPresenceRules.IsOnline(now.AddMinutes(-5).AddTicks(-1), now));
        Assert.False(PanelUserPresenceRules.IsOnline(null, now));
        Assert.False(PanelUserPresenceRules.IsOnline(now.AddMinutes(1), now));
    }

    [Fact]
    public void TruncateToUtcHour_DropsMinutesAndSeconds()
    {
        var value = new DateTime(2026, 8, 22, 11, 47, 19, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 8, 22, 11, 0, 0, DateTimeKind.Utc), PanelUserPresenceRules.TruncateToUtcHour(value));
    }

    [Fact]
    public void BuildHourSeries_PeaksAtMoscowHourAndAveragesAcrossDays()
    {
        var zone = PanelUserPresenceRules.MoscowTimeZone;
        var nowUtc = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        var todayEleven = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 8, 22, 11, 0, 0), zone);
        var yesterdayEleven = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 8, 21, 11, 0, 0), zone);
        var yesterdayNine = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 8, 21, 9, 0, 0), zone);

        var series = PanelUserPresenceRules.BuildHourSeries(
            [todayEleven, todayEleven, yesterdayEleven, yesterdayNine],
            nowUtc,
            zone);

        Assert.Equal(11, series.TypicalPeakHour);
        Assert.Equal(2, series.TypicalPeakValue);
        Assert.Equal(11, series.TodayPeakHour);
        Assert.Equal(2, series.TodayPeakValue);
        Assert.Equal(2, series.TodayByHour[11]);
        Assert.Equal(2, series.SampleDayCount);
        Assert.Equal(2, series.TypicalByHour[11]);
        Assert.Equal(1, series.TypicalByHour[9]);
        Assert.Equal(15, series.CurrentHour);
    }
}
