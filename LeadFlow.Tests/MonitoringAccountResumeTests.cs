using LeadFlow.Core.Services;
using Xunit;

namespace LeadFlow.Tests;

public sealed class MonitoringAccountResumeTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_PrefersMemoryOverPersistedAndLastPass()
    {
        var memory = Now.AddMinutes(20);
        var next = MonitoringAccountResume.ResolveNextEligibleUtc(
            memory,
            Now.AddMinutes(5),
            Now.AddMinutes(-1));
        Assert.Equal(memory, next);
    }

    [Fact]
    public void Resolve_UsesPersistedWhenMemoryMissing()
    {
        var persisted = Now.AddMinutes(12);
        var next = MonitoringAccountResume.ResolveNextEligibleUtc(
            null,
            persisted,
            Now.AddMinutes(-2));
        Assert.Equal(persisted, next);
    }

    [Fact]
    public void Resolve_WithoutSchedule_WaitsMinDelayAfterLastPass()
    {
        var last = Now.AddMinutes(-1);
        var next = MonitoringAccountResume.ResolveNextEligibleUtc(null, null, last);
        Assert.Equal(last.AddMinutes(MonitoringTiming.CycleDelayMinMinutes), next);
        Assert.True(next > Now);
    }

    [Fact]
    public void Resolve_OldLastPass_IsImmediatelyDue()
    {
        var last = Now.AddHours(-2);
        var next = MonitoringAccountResume.ResolveNextEligibleUtc(null, null, last);
        Assert.True(next <= Now);
    }

    [Fact]
    public void Resolve_NeverMonitored_IsImmediatelyDue() =>
        Assert.Equal(DateTime.MinValue, MonitoringAccountResume.ResolveNextEligibleUtc(null, null, null));
}
