using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringNightQuietTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData(23, true)]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(12, false)]
    [InlineData(22, false)]
    public void IsActive_UsesInclusiveStartExclusiveEnd(int hour, bool expected)
    {
        var utc = new DateTime(2026, 8, 21, hour, 15, 0, DateTimeKind.Utc);

        Assert.Equal(expected, MonitoringNightQuiet.IsActive(utc, Utc));
    }

    [Fact]
    public void ApplyFloor_Daytime_LeavesDelayUnchanged()
    {
        var day = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromMinutes(8);

        Assert.Equal(delay, MonitoringNightQuiet.ApplyFloor(delay, day, Utc, floorMinutes: 60));
    }

    [Fact]
    public void ApplyFloor_Night_RaisesShortDelayToFloor()
    {
        var night = new DateTime(2026, 8, 21, 23, 30, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromMinutes(8);

        Assert.Equal(TimeSpan.FromMinutes(60), MonitoringNightQuiet.ApplyFloor(delay, night, Utc, floorMinutes: 60));
    }

    [Fact]
    public void ApplyFloor_Night_KeepsLongerQuietBackoff()
    {
        var night = new DateTime(2026, 8, 21, 1, 0, 0, DateTimeKind.Utc);
        var delay = TimeSpan.FromMinutes(95);

        Assert.Equal(delay, MonitoringNightQuiet.ApplyFloor(delay, night, Utc, floorMinutes: 60));
    }

    [Fact]
    public void MoscowMidnight_IsQuiet()
    {
        // 21:00 UTC = 00:00 MSK
        var utc = new DateTime(2026, 8, 21, 21, 0, 0, DateTimeKind.Utc);

        Assert.True(MonitoringNightQuiet.IsActive(utc, MonitoringNightQuiet.MoscowTimeZone));
    }

    [Fact]
    public void NightHourBounds_AreOrderedAcrossMidnight()
    {
        Assert.True(MonitoringTiming.NightQuietStartHourInclusive >= 18);
        Assert.True(MonitoringTiming.NightQuietEndHourExclusive <= 10);
        Assert.True(MonitoringTiming.NightQuietDelayMinMinutes
                    <= MonitoringTiming.NightQuietDelayMaxMinutes);
        Assert.True(MonitoringTiming.NightQuietDelayMinMinutes >= 45);
    }
}
