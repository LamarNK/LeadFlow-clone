using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Avito.Session;
using LeadFlow.Core.Services.Worker;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoSessionDeadFastFailTests
{
    private static TimeoutException CdpTimeout() =>
        AdsPowerCdpGuard.Timeout("JavaScript-проверка страницы", TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ThreeConsecutiveCdpTimeouts_AbortNextStepImmediately()
    {
        var probe = new ScriptedThrowingProbe();
        await using var orchestrator = new AvitoSessionOrchestrator(
            probe.ProbeAsync,
            _ => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(5));

        var stepCalls = 0;
        for (var i = 1; i <= 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                orchestrator.RunStepAsync(
                    _ =>
                    {
                        stepCalls++;
                        throw CdpTimeout();
                    },
                    CancellationToken.None));
        }

        // Серия достигла потолка: следующий шаг даже не начинает выполняться.
        var dead = await Assert.ThrowsAsync<AvitoSessionDeadException>(() =>
            orchestrator.RunStepAsync(
                _ =>
                {
                    stepCalls++;
                    return Task.FromResult(0);
                },
                CancellationToken.None));

        Assert.Equal(AvitoSessionOrchestrator.MaxConsecutiveCdpFailures, dead.ConsecutiveFailures);
        Assert.Equal(3, stepCalls);
    }

    [Fact]
    public async Task SuccessfulStep_ResetsFailureStreak()
    {
        var probe = new ScriptedThrowingProbe();
        await using var orchestrator = new AvitoSessionOrchestrator(
            probe.ProbeAsync,
            _ => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(5));

        // Две неудачи, успех, ещё две неудачи — потолок не достигнут.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync<int>(_ => throw CdpTimeout(), CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync<int>(_ => throw CdpTimeout(), CancellationToken.None));
        Assert.Equal(7, await orchestrator.RunStepAsync(_ => Task.FromResult(7), CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync<int>(_ => throw CdpTimeout(), CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync<int>(_ => throw CdpTimeout(), CancellationToken.None));

        // 3-я подряд (после сброса — только 2) ещё считается, и только следующая серия добьёт.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync<int>(_ => throw CdpTimeout(), CancellationToken.None));
        await Assert.ThrowsAsync<AvitoSessionDeadException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public async Task HangingProbes_AlsoAccumulateStreak()
    {
        // Проба всегда падает — ProbeUntilKnownOrThrow бросает CDP-таймаут после серии Unknown.
        var probe = new ScriptedThrowingProbe(throwOnProbe: true);
        await using var orchestrator = new AvitoSessionOrchestrator(
            probe.ProbeAsync,
            _ => Task.FromResult<string?>(null),
            TimeSpan.FromMilliseconds(10));

        for (var i = 1; i <= 3; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                orchestrator.RunStepAsync(_ => Task.FromResult(0), CancellationToken.None));
            // Проба перед шагом троттлится (minStepProbeInterval): даём ей «состариться».
            await Task.Delay(50);
        }

        await Assert.ThrowsAsync<AvitoSessionDeadException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(0), CancellationToken.None));
    }

    [Fact]
    public void Retry_IsTransient_WithRegularDelay()
    {
        var ex = new AvitoSessionDeadException(3);

        Assert.Equal(WorkerAdsPowerPassRetry.Delay, WorkerAdsPowerPassRetry.FromException(ex));
        Assert.Equal("Warning", WorkerAdsPowerPassRetry.EventType(ex));
    }

    /// <summary>Probe-заглушка: страница «чиста», но по требованию падает исключением (Unknown).</summary>
    private sealed class ScriptedThrowingProbe(bool throwOnProbe = false)
    {
        public Task<AvitoPageObstacle> ProbeAsync(CancellationToken cancellationToken)
        {
            if (throwOnProbe)
            {
                throw new InvalidOperationException("CDP не отвечает");
            }

            return Task.FromResult(AvitoPageObstacle.None);
        }
    }
}

public sealed class WorkerNetworkCircuitBreakerTests
{
    [Fact]
    public void StormOfDifferentAccounts_OpensBreaker()
    {
        WorkerNetworkCircuitBreaker.ResetForTests();
        var now = DateTime.UtcNow;

        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now);
        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now.AddSeconds(10));
        Assert.False(WorkerNetworkCircuitBreaker.IsOpen(now.AddSeconds(20)));

        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now.AddSeconds(30));
        Assert.True(WorkerNetworkCircuitBreaker.IsOpen(now.AddSeconds(31)));
        Assert.NotNull(WorkerNetworkCircuitBreaker.OpenUntilUtc());
    }

    [Fact]
    public void SameAccountRepeatedly_DoesNotOpenBreaker()
    {
        WorkerNetworkCircuitBreaker.ResetForTests();
        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            WorkerNetworkCircuitBreaker.RegisterNetworkFailure(accountId, now.AddSeconds(i));
        }

        Assert.False(WorkerNetworkCircuitBreaker.IsOpen(now.AddSeconds(10)));
    }

    [Fact]
    public void Breaker_ClosesAfterOpenDuration()
    {
        WorkerNetworkCircuitBreaker.ResetForTests();
        var now = DateTime.UtcNow;

        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now);
        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now);
        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now);
        Assert.True(WorkerNetworkCircuitBreaker.IsOpen(now.AddSeconds(1)));

        var afterOpen = now.Add(WorkerNetworkCircuitBreaker.OpenDuration).Add(TimeSpan.FromSeconds(1));
        Assert.False(WorkerNetworkCircuitBreaker.IsOpen(afterOpen));
        Assert.Null(WorkerNetworkCircuitBreaker.OpenUntilUtc(afterOpen));
    }

    [Fact]
    public void OldFailures_ExpireFromWindow()
    {
        WorkerNetworkCircuitBreaker.ResetForTests();
        var now = DateTime.UtcNow;

        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), now);
        // Через 11 минут первая неудача выпала из 10-минутного окна: разных аккаунтов снова 2.
        var later = now.AddMinutes(11);
        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), later);
        WorkerNetworkCircuitBreaker.RegisterNetworkFailure(Guid.NewGuid(), later);
        Assert.False(WorkerNetworkCircuitBreaker.IsOpen(later.AddSeconds(1)));
    }
}
