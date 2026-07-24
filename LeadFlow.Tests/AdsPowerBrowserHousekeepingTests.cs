using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AdsPowerBrowserHousekeepingTests
{
    private static readonly DateOnly Day = new(2026, 7, 21);

    [Fact]
    public void ShouldSweep_FirstCycles_WithoutPriorSweep_OnlyAtEveryN()
    {
        Assert.False(AdsPowerBrowserHousekeeping.ShouldSweep(1, lastSweepLocalDate: null, Day));
        Assert.False(AdsPowerBrowserHousekeeping.ShouldSweep(2, lastSweepLocalDate: null, Day));
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(3, lastSweepLocalDate: null, Day));
    }

    [Fact]
    public void ShouldSweep_SameDay_AfterPriorSweep_OnlyAtEveryN()
    {
        Assert.False(AdsPowerBrowserHousekeeping.ShouldSweep(1, Day, Day));
        Assert.False(AdsPowerBrowserHousekeeping.ShouldSweep(2, Day, Day));
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(3, Day, Day));
    }

    [Fact]
    public void ShouldSweep_NewLocalDay_AfterPriorSweep_TrueRegardlessOfCycleCount()
    {
        var yesterday = Day.AddDays(-1);
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(1, yesterday, Day));
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(0, yesterday, Day));
    }

    [Fact]
    public void ShouldSweep_RespectsCustomEveryNCycles()
    {
        Assert.False(AdsPowerBrowserHousekeeping.ShouldSweep(1, Day, Day, everyNCycles: 2));
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(2, Day, Day, everyNCycles: 2));
    }

    [Fact]
    public void ShouldSweep_EveryNCyclesClampedToAtLeastOne()
    {
        Assert.True(AdsPowerBrowserHousekeeping.ShouldSweep(1, Day, Day, everyNCycles: 0));
    }

    [Fact]
    public void BrowserHousekeepingEveryNCycles_IsThree()
    {
        Assert.Equal(3, MonitoringTiming.BrowserHousekeepingEveryNCycles);
    }
}
