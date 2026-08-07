using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class DashboardPeriodTests
{
    // UTC+5 → JS getTimezoneOffset = -300
    private const int UtcPlusFiveOffsetMinutes = -300;

    [Fact]
    public void All_ActivePresetAndLabel()
    {
        var period = DashboardPeriod.All;

        Assert.Equal("all", period.ActivePreset);
        Assert.Equal("Все", period.Label);
        Assert.True(period.IsAllTime);
        Assert.False(period.IsTodayOnly);
    }

    [Fact]
    public void Parse_AllRange_MatchesAllPreset()
    {
        var all = DashboardPeriod.All;
        var parsed = DashboardPeriod.Parse(all.FromIso, all.ToIso, all.TimeZoneOffsetMinutes);

        Assert.Equal("all", parsed.ActivePreset);
        Assert.Equal(all.From, parsed.From);
        Assert.Equal(all.To, parsed.To);
    }

    [Fact]
    public void Parse_ClampsRangeToMaxInclusiveCalendarDays()
    {
        var parsed = DashboardPeriod.Parse("2025-01-01", "2026-01-02", timeZoneOffsetMinutes: 0);

        Assert.Equal(new DateTime(2025, 1, 1), parsed.From);
        Assert.Equal(new DateTime(2026, 1, 1), parsed.To);
        Assert.Equal(DashboardPeriod.MaxDays, (parsed.To - parsed.From).Days + 1);
    }

    [Fact]
    public void CreateToday_UsesBrowserLocalDay_NotUtcDay()
    {
        // 2026-08-07 19:49 UTC = 2026-08-08 00:49 in UTC+5
        var utcNow = new DateTime(2026, 8, 7, 19, 49, 0, DateTimeKind.Utc);

        var period = DashboardPeriod.CreateToday(UtcPlusFiveOffsetMinutes, utcNow);

        Assert.Equal(new DateTime(2026, 8, 8), period.From);
        Assert.Equal(new DateTime(2026, 8, 8), period.To);
        Assert.Equal("today", period.ActivePreset);
        Assert.Equal("08.08.2026", period.Label);
    }

    [Fact]
    public void FromUtc_ForUtcPlusFive_StartsPreviousUtcEvening()
    {
        var period = new DashboardPeriod(
            new DateTime(2026, 8, 8),
            new DateTime(2026, 8, 8),
            TimeZoneOffsetMinutes: UtcPlusFiveOffsetMinutes);

        Assert.Equal(new DateTime(2026, 8, 7, 19, 0, 0, DateTimeKind.Utc), period.FromUtc);
        Assert.Equal(new DateTime(2026, 8, 8, 19, 0, 0, DateTimeKind.Utc), period.ToUtcExclusive);
    }

    [Fact]
    public void Parse_MissingDates_DefaultsToLocalToday()
    {
        var utcNow = new DateTime(2026, 8, 7, 19, 49, 0, DateTimeKind.Utc);

        var period = DashboardPeriod.Parse(null, null, UtcPlusFiveOffsetMinutes, utcNow);

        Assert.Equal(new DateTime(2026, 8, 8), period.From);
        Assert.True(period.IsTodayOnly);
    }

    [Fact]
    public void Parse_ClampsFutureLocalDateToLocalToday()
    {
        var utcNow = new DateTime(2026, 8, 7, 19, 49, 0, DateTimeKind.Utc);

        var period = DashboardPeriod.Parse("2026-08-09", "2026-08-09", UtcPlusFiveOffsetMinutes, utcNow);

        Assert.Equal(new DateTime(2026, 8, 8), period.From);
        Assert.Equal(new DateTime(2026, 8, 8), period.To);
    }
}
