using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmShiftRulesTests
{
    private static readonly DateTime BeforeCutoff =
        new(2026, 8, 8, 17, 59, 0, DateTimeKind.Utc); // 22:59 Asia/Yekaterinburg

    [Fact]
    public void IsEffectivelyOnShift_StartedTodayBeforeCutoff_IsTrue()
    {
        var started = new DateTime(2026, 8, 8, 5, 0, 0, DateTimeKind.Utc);

        Assert.True(CrmShiftRules.IsEffectivelyOnShift(true, started, BeforeCutoff));
        Assert.False(CrmShiftRules.ShouldAutoStop(true, started, BeforeCutoff));
    }

    [Fact]
    public void IsEffectivelyOnShift_PreviousBusinessDay_IsFalse()
    {
        var started = new DateTime(2026, 8, 7, 17, 0, 0, DateTimeKind.Utc);

        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, started, BeforeCutoff));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, started, BeforeCutoff));
    }

    [Fact]
    public void IsEffectivelyOnShift_AtDailyCutoff_IsFalse()
    {
        var started = new DateTime(2026, 8, 8, 5, 0, 0, DateTimeKind.Utc);
        var atCutoff = new DateTime(2026, 8, 8, 18, 0, 0, DateTimeKind.Utc);

        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, started, atCutoff));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, started, atCutoff));
    }

    [Fact]
    public void IsEffectivelyOnShift_Inactive_IsFalse()
    {
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(false, BeforeCutoff, BeforeCutoff));
        Assert.False(CrmShiftRules.ShouldAutoStop(false, BeforeCutoff, BeforeCutoff));
    }

    [Fact]
    public void IsEffectivelyOnShift_ActiveWithoutStart_IsFalse_AndShouldAutoStop()
    {
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, null, BeforeCutoff));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, null, BeforeCutoff));
    }

    [Fact]
    public void ResolveAutoEndReason_NullStart_IsLegacy()
    {
        Assert.Equal(CrmShiftEndReasons.LegacyCleanup, CrmShiftRules.ResolveAutoEndReason(null));
        Assert.Equal(CrmShiftEndReasons.AutoDailyCutoff, CrmShiftRules.ResolveAutoEndReason(BeforeCutoff));
    }
}
