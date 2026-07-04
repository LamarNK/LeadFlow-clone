using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringCycleDelayTests
{
    [Fact]
    public void GetDelayAfterCycle_NoNewResponses_UsesMaxMinutes()
    {
        var d = MonitoringCycleDelay.GetDelayAfterCycle(0, 2);
        Assert.Equal(MonitoringTiming.CycleDelayMaxMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_FullCapacity_UsesMinMinutes()
    {
        var capacity = 3 * MonitoringTiming.TypicalResponsesPerAccountPerCycle;
        var d = MonitoringCycleDelay.GetDelayAfterCycle(capacity, 3);
        Assert.Equal(MonitoringTiming.CycleDelayMinMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_PerAccountHotness_PushesHighActivityTowardMin()
    {
        // 1 аккаунт, 5 новых: «сырая» доля 0.5, но perAccount 5/1 → activity 1 → как полный цикл
        var d = MonitoringCycleDelay.GetDelayAfterCycle(5, 1);
        Assert.Equal(MonitoringTiming.CycleDelayMinMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_SparseNewsAcrossAccounts_DoesNotLookAlmostIdle()
    {
        // 2 аккаунта опрошено, всего 1 новый: сырая 1/20, perAccount min(1, 1/2)=0.5 → середина диапазона [min..max] минут
        var d = MonitoringCycleDelay.GetDelayAfterCycle(1, 2);
        var min = (double)MonitoringTiming.CycleDelayMinMinutes;
        var max = (double)MonitoringTiming.CycleDelayMaxMinutes;
        var expectedMinutes = max - 0.5 * (max - min);
        Assert.Equal(expectedMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_NoAccountsPolled_StillWithinRange()
    {
        var d = MonitoringCycleDelay.GetDelayAfterCycle(0, 0);
        Assert.Equal(MonitoringTiming.CycleDelayMaxMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_QuietStreak_AddsCappedExtra()
    {
        var d = MonitoringCycleDelay.GetDelayAfterCycle(0, 1, consecutiveQuietCycles: 3);
        Assert.Equal(
            MonitoringTiming.CycleDelayMaxMinutes + 2 * MonitoringTiming.CycleQuietBackoffExtraMinutesPerStep,
            d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_Backlog_ForcesMinDelay()
    {
        var d = MonitoringCycleDelay.GetDelayAfterCycle(1, 2, consecutiveQuietCycles: 5, hasUndischargedBacklog: true);
        Assert.Equal(MonitoringTiming.CycleDelayMinMinutes, d.TotalMinutes);
    }

    [Fact]
    public void GetDelayAfterCycle_HistoricalHeat_ShortensVersusColdSlot()
    {
        var cold = MonitoringCycleDelay.GetDelayAfterCycle(0, 2, consecutiveQuietCycles: 1, hasUndischargedBacklog: false, historicalHeatScore: 0);
        var hot = MonitoringCycleDelay.GetDelayAfterCycle(0, 2, consecutiveQuietCycles: 1, hasUndischargedBacklog: false, historicalHeatScore: 1);
        Assert.True(hot.TotalMinutes < cold.TotalMinutes);
    }
}
