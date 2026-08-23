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

    [Fact]
    public void UnfinishedPass_SkipsAlreadyCompletedSubProfiles()
    {
        DateTime? started = Now;
        DateTime? finished = null;
        var done = new HashSet<string>(StringComparer.Ordinal) { "a", "b" };
        var remaining = MonitoringAccountResume.RemainingSubProfiles(
            ["a", "b", "c", "d"],
            static id => id,
            started,
            finished,
            done);
        Assert.Equal(["c", "d"], remaining);
    }

    [Fact]
    public void FinishedPass_DoesNotSkipSubs()
    {
        DateTime? started = Now.AddMinutes(-30);
        DateTime? finished = Now.AddMinutes(-20);
        var done = new HashSet<string>(StringComparer.Ordinal) { "a" };
        var remaining = MonitoringAccountResume.RemainingSubProfiles(
            ["a", "b"],
            static id => id,
            started,
            finished,
            done);
        Assert.Equal(["a", "b"], remaining);
    }

    [Fact]
    public void BeginOrResume_Unfinished_KeepsCompletedIds()
    {
        DateTime? started = Now.AddMinutes(-5);
        DateTime? finished = null;
        var done = new HashSet<string>(StringComparer.Ordinal) { "a" };
        MonitoringAccountResume.BeginOrResumePass(Now, ref started, ref finished, done);
        Assert.Equal(Now.AddMinutes(-5), started);
        Assert.Null(finished);
        Assert.Contains("a", done);
    }

    [Fact]
    public void BeginOrResume_Finished_StartsFresh()
    {
        DateTime? started = Now.AddMinutes(-40);
        DateTime? finished = Now.AddMinutes(-20);
        var done = new HashSet<string>(StringComparer.Ordinal) { "a" };
        MonitoringAccountResume.BeginOrResumePass(Now, ref started, ref finished, done);
        Assert.Equal(Now, started);
        Assert.Null(finished);
        Assert.Empty(done);
    }

    [Fact]
    public void FinishPass_ClearsCompletedAndSetsNext()
    {
        DateTime? started = Now;
        DateTime? finished = null;
        DateTime? next = null;
        var done = new HashSet<string>(StringComparer.Ordinal) { "a" };
        MonitoringAccountResume.FinishPass(
            Now.AddMinutes(10),
            Now.AddMinutes(25),
            ref started,
            ref finished,
            ref next,
            done);
        Assert.Equal(Now.AddMinutes(10), finished);
        Assert.Equal(Now.AddMinutes(25), next);
        Assert.Empty(done);
    }
}
