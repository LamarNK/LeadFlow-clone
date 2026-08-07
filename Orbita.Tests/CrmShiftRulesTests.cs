using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmShiftRulesTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsEffectivelyOnShift_ActiveWithinMax_IsTrue()
    {
        var started = Now - TimeSpan.FromHours(8);
        Assert.True(CrmShiftRules.IsEffectivelyOnShift(true, started, Now));
        Assert.False(CrmShiftRules.ShouldAutoStop(true, started, Now));
    }

    [Fact]
    public void IsEffectivelyOnShift_ActivePastMax_IsFalse()
    {
        var started = Now - CrmShiftRules.MaxDuration - TimeSpan.FromSeconds(1);
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, started, Now));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, started, Now));
    }

    [Fact]
    public void IsEffectivelyOnShift_Inactive_IsFalse()
    {
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(false, Now, Now));
        Assert.False(CrmShiftRules.ShouldAutoStop(false, Now, Now));
    }

    [Fact]
    public void IsEffectivelyOnShift_ActiveWithoutStart_IsFalse_AndShouldAutoStop()
    {
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, null, Now));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, null, Now));
    }

    [Fact]
    public void IsEffectivelyOnShift_ExactlyAtMax_IsFalse()
    {
        var started = Now - CrmShiftRules.MaxDuration;
        Assert.False(CrmShiftRules.IsEffectivelyOnShift(true, started, Now));
        Assert.True(CrmShiftRules.ShouldAutoStop(true, started, Now));
    }

    [Fact]
    public void ResolveAutoEndReason_NullStart_IsLegacy()
    {
        Assert.Equal(CrmShiftEndReasons.LegacyCleanup, CrmShiftRules.ResolveAutoEndReason(null));
        Assert.Equal(CrmShiftEndReasons.AutoMaxDuration, CrmShiftRules.ResolveAutoEndReason(Now));
    }
}
