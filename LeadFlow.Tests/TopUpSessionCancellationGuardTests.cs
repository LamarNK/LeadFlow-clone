using LeadFlow.Core.Services.Avito;
using Orbita.Contracts;
using Xunit;

namespace LeadFlow.Tests;

public sealed class TopUpSessionCancellationGuardTests
{
    private static TopUpSessionDto ActiveSession(string status = TopUpSessionStatuses.Started) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "W1",
            Guid.NewGuid(),
            "Acc1",
            Guid.NewGuid(),
            "op1",
            "Operator 1",
            status,
            100m,
            300m,
            200m,
            0,
            DateTime.UtcNow,
            DateTime.UtcNow.AddHours(2),
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static TopUpSessionPollResult Active() =>
        new(TopUpSessionPollStatus.Active, ActiveSession());

    private static TopUpSessionPollResult Terminal() =>
        new(TopUpSessionPollStatus.Terminal, ActiveSession(TopUpSessionStatuses.Cancelled));

    [Fact]
    public async Task RunAsync_ActiveSession_ReturnsTrue_AndDoesNotCancel()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(Active()),
            pollInterval: TimeSpan.FromMilliseconds(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        var result = await guard.RunAsync(Guid.NewGuid(), cts.Token);

        Assert.True(result);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_TerminalSession_CancelsToken_AndReturnsFalse()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(Terminal()),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.True(guard.IsCancelled);
        Assert.True(guard.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task RunAsync_MissingSession_CancelsToken()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Missing)),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_PermanentStatus_CancelsToken()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Permanent)),
            pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_BecomesTerminalAfterPoll_CancelsToken()
    {
        var calls = 0;
        var guard = new TopUpSessionCancellationGuard(
            _ =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? Active() : Terminal());
            },
            pollInterval: TimeSpan.FromMilliseconds(5));

        var result = await guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.True(guard.IsCancelled);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task IsSessionActiveAsync_TerminalSession_ReturnsFalse_AndCancels()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(
                TopUpSessionPollStatus.Terminal,
                ActiveSession(TopUpSessionStatuses.Expired))));

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(active);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_ActiveSession_ReturnsTrue()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(Active()));

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(active);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_QueryThrows_ReturnsTrue_DoesNotCancel()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => throw new InvalidOperationException("network"));

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(active);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_FirstTransient_ReturnsTrue_DoesNotCancel()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient)),
            maxConsecutiveFailures: 5);

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(active);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_RepeatedTransient_ReachesThreshold_Cancels()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient)),
            maxConsecutiveFailures: 3);

        // Первые два вызова — транзиентные, не отменяют.
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));

        // Третий подряд — достигает порога и отменяет.
        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(active);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_Missing_ReturnsFalse_AndCancels()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Missing)));

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(active);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_Permanent_ReturnsFalse_AndCancels()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Permanent)));

        var active = await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(active);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task IsSessionActiveAsync_TransientThenActive_ResetsCounter()
    {
        var calls = 0;
        var guard = new TopUpSessionCancellationGuard(
            _ =>
            {
                calls++;
                // transient, transient, active, transient, transient — никогда 3 подряд.
                if (calls == 1 || calls == 2 || calls == 4 || calls == 5)
                {
                    return Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient));
                }

                return Task.FromResult(Active());
            },
            maxConsecutiveFailures: 3);

        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None)); // active — сброс
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await guard.IsSessionActiveAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task Stop_ReturnsRunAsyncPromptly_WithoutCancellingSession()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(Active()),
            pollInterval: TimeSpan.FromSeconds(10));

        var runTask = guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        await Task.Delay(50);
        guard.Stop();

        var result = await runTask;

        Assert.True(result);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_SingleTransientError_DoesNotCancel()
    {
        var calls = 0;
        var guard = new TopUpSessionCancellationGuard(
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    return Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient));
                }

                return Task.FromResult(Active());
            },
            pollInterval: TimeSpan.FromMilliseconds(5),
            maxConsecutiveFailures: 5);

        var runTask = guard.RunAsync(Guid.NewGuid(), CancellationToken.None);
        await Task.Delay(60);
        guard.Stop();

        var result = await runTask;

        Assert.True(result);
        Assert.False(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_RepeatedTransientFailures_CancelsAfterThreshold()
    {
        var guard = new TopUpSessionCancellationGuard(
            _ => Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient)),
            pollInterval: TimeSpan.FromMilliseconds(5),
            maxConsecutiveFailures: 3);

        var result = await guard.RunAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(result);
        Assert.True(guard.IsCancelled);
    }

    [Fact]
    public async Task RunAsync_FailureCountResetsAfterSuccess()
    {
        var calls = 0;
        var guard = new TopUpSessionCancellationGuard(
            _ =>
            {
                calls++;
                // Паттерн: transient, transient, active, transient, transient — никогда 3 подряд.
                if (calls == 1 || calls == 2 || calls == 4 || calls == 5)
                {
                    return Task.FromResult(new TopUpSessionPollResult(TopUpSessionPollStatus.Transient));
                }

                return Task.FromResult(Active());
            },
            pollInterval: TimeSpan.FromMilliseconds(5),
            maxConsecutiveFailures: 3);

        var runTask = guard.RunAsync(Guid.NewGuid(), CancellationToken.None);
        await Task.Delay(80);
        guard.Stop();

        var result = await runTask;

        Assert.True(result);
        Assert.False(guard.IsCancelled);
    }
}
