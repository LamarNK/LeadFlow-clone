using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class LocalCalendarDateRangeTests
{
    [Fact]
    public void GetLocalCalendarDate_UtcPlusFive_EarlyMorning_IsNextLocalDay()
    {
        var utcNow = new DateTime(2026, 8, 7, 19, 49, 0, DateTimeKind.Utc);

        var local = LocalCalendarDateRange.GetLocalCalendarDate(utcNow, timeZoneOffsetMinutes: -300);

        Assert.Equal(new DateTime(2026, 8, 8), local);
    }

    [Fact]
    public void GetUtcRangeForLocalCalendarDay_UtcPlusFive_MatchesHalfOpenLocalDay()
    {
        var range = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(
            new DateTime(2026, 8, 8),
            timeZoneOffsetMinutes: -300);

        Assert.Equal(new DateTime(2026, 8, 7, 19, 0, 0, DateTimeKind.Utc), range.UtcStartInclusive);
        Assert.Equal(new DateTime(2026, 8, 8, 19, 0, 0, DateTimeKind.Utc), range.UtcEndExclusive);
    }

    [Fact]
    public void Normalize_DoesNotClampToServerToday_WhenOffsetMakesLocalDayAheadOfUtc()
    {
        var utcNow = new DateTime(2026, 8, 7, 19, 49, 0, DateTimeKind.Utc);
        var localToday = new DateTime(2026, 8, 8);

        var range = LocalCalendarDateRange.Normalize(
            localToday,
            localToday,
            timeZoneOffsetMinutes: -300,
            utcNow: utcNow);

        Assert.Equal(localToday, range.StartLocal);
        Assert.Equal(localToday, range.EndLocal);
        Assert.Equal(new DateTime(2026, 8, 7, 19, 0, 0, DateTimeKind.Utc), range.UtcStartInclusive);
        Assert.Equal(new DateTime(2026, 8, 8, 19, 0, 0, DateTimeKind.Utc), range.UtcEndExclusive);
    }

    [Fact]
    public void GetUtcRangeForLocalCalendarDay_WithoutOffset_MatchesServerLocalZone()
    {
        var day = new DateTime(2026, 8, 8);
        var expectedStart = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);
        var expectedEnd = TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.Local);

        var range = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(day);

        Assert.Equal(expectedStart, range.UtcStartInclusive);
        Assert.Equal(expectedEnd, range.UtcEndExclusive);
    }
}
