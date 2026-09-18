using LeadFlow.Core.Services.Avito.Session;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoSessionOrchestratorTests
{
    private static readonly AvitoPageObstacle GeeTestCaptcha = new(
        AvitoPageObstacleKind.Captcha,
        "geetest",
        "https://www.avito.ru/profile/candidates",
        Signals: ["captcha-dialog"]);

    private static readonly AvitoPageObstacle IpBlock = new(
        AvitoPageObstacleKind.IpBlocked,
        Url: "https://www.avito.ru/");

    /// <summary>Probe-заглушка: выдаёт препятствия по номеру вызова, дальше — «чисто».</summary>
    private sealed class ScriptedProbe
    {
        private readonly object gate = new();
        private readonly Queue<AvitoPageObstacle> script;
        private readonly AvitoPageObstacle tail;
        public int ProbeCount;

        public ScriptedProbe(IEnumerable<AvitoPageObstacle> script, AvitoPageObstacle? tail = null)
        {
            this.script = new Queue<AvitoPageObstacle>(script);
            this.tail = tail ?? AvitoPageObstacle.None;
        }

        public Task<AvitoPageObstacle> ProbeAsync(CancellationToken cancellationToken)
        {
            AvitoPageObstacle obstacle;
            lock (gate)
            {
                ProbeCount++;
                obstacle = script.Count > 0 ? script.Dequeue() : tail;
            }

            return Task.FromResult(obstacle);
        }
    }

    private static AvitoSessionOrchestrator Create(
        ScriptedProbe probe,
        TimeSpan? interval = null,
        Action<AvitoSessionOrchestrator>? configure = null)
    {
        var orchestrator = new AvitoSessionOrchestrator(
            probe.ProbeAsync,
            _ => Task.FromResult<string?>(null),
            interval ?? TimeSpan.FromMilliseconds(10));
        configure?.Invoke(orchestrator);
        return orchestrator;
    }

    [Fact]
    public async Task RunStepAsync_ClearPage_ExecutesStep()
    {
        var probe = new ScriptedProbe([]);
        await using var orchestrator = Create(probe);

        var result = await orchestrator.RunStepAsync(_ => Task.FromResult(42), CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_CaptchaBeforeStep_RecoversThenRequiresPassRestart()
    {
        // До шага: captcha → verify(чисто) → verify(чисто), затем сам шаг выполняется.
        var probe = new ScriptedProbe([GeeTestCaptcha]);
        var handlerCalls = 0;
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.Captcha, (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(AvitoObstacleRecoveryResult.Success());
            }));

        var stepCalls = 0;
        var ex = await Assert.ThrowsAsync<AvitoSessionRestartRequiredException>(() =>
            orchestrator.RunStepAsync(
                _ =>
                {
                    stepCalls++;
                    return Task.FromResult("step-done");
                },
                CancellationToken.None));

        Assert.Equal(1, ex.RecoveryGeneration);
        Assert.Equal(0, stepCalls);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
        Assert.Null(orchestrator.ActiveObstacle);

        // Даже когда страница уже снова чистая, старый DOM-снимок не может делать клики.
        await Assert.ThrowsAsync<AvitoSessionRestartRequiredException>(() =>
            orchestrator.WaitReadyAsync(expectedRecoveryGeneration: 0, CancellationToken.None));
        await Assert.ThrowsAsync<AvitoSessionRestartRequiredException>(() =>
            orchestrator.RunStepAsync(0, _ => Task.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task RunStepAsync_UnsolvableCaptcha_ThrowsCaptchaExceptionAfterAttempts()
    {
        // Препятствие никогда не исчезает: обработчик отработает MaxRecoveryAttemptsPerEpisode раз,
        // затем терминальное состояние и исключение для мониторинга.
        var probe = new ScriptedProbe([], tail: GeeTestCaptcha);
        var handlerCalls = 0;
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.Captcha, (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(AvitoObstacleRecoveryResult.Success());
            }));

        var ex = await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        Assert.Equal("geetest", ex.Kind);
        Assert.Equal(2, handlerCalls);
        Assert.Equal(AvitoSessionStatus.RequiresManualAction, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_HandlerFailsTwice_ThrowsCaptchaException()
    {
        var probe = new ScriptedProbe([GeeTestCaptcha]);
        var handlerCalls = 0;
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.Captcha, (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(AvitoObstacleRecoveryResult.Failure("RuCaptcha timeout"));
            }));

        var ex = await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        Assert.Equal("geetest", ex.Kind);
        Assert.Equal(2, handlerCalls);
        Assert.Equal(AvitoSessionStatus.RequiresManualAction, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_TimedOutHandler_IsCancelledAndDoesNotOverlapRetry()
    {
        var activeHandlers = 0;
        var maxConcurrentHandlers = 0;
        var attempts = 0;
        var probe = new ScriptedProbe([GeeTestCaptcha]);
        await using var orchestrator = new AvitoSessionOrchestrator(
            probe.ProbeAsync,
            _ => Task.FromResult<string?>(null),
            recoveryAttemptTimeout: TimeSpan.FromMilliseconds(50));
        orchestrator.RegisterHandler(AvitoPageObstacleKind.Captcha, async (_, ct) =>
        {
            Interlocked.Increment(ref attempts);
            var active = Interlocked.Increment(ref activeHandlers);
            maxConcurrentHandlers = Math.Max(maxConcurrentHandlers, active);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                Interlocked.Decrement(ref activeHandlers);
            }

            return AvitoObstacleRecoveryResult.Success();
        });

        await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        Assert.Equal(2, attempts);
        Assert.Equal(1, maxConcurrentHandlers);
        Assert.Equal(0, activeHandlers);
    }

    [Fact]
    public async Task RunStepAsync_IpBlockWithoutHandler_ThrowsFirewallException()
    {
        var probe = new ScriptedProbe([IpBlock]);
        await using var orchestrator = Create(probe);

        var ex = await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        Assert.Equal("firewall", ex.Kind);
        Assert.Equal(AvitoSessionStatus.RequiresManualAction, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_IpBlockWithHandler_RecoversAndResumesWork()
    {
        // Блок IP теперь решаем: зарегистрированный обработчик (перезапрос + капча)
        // обязан получить эпизод, а не терминал «нет обработчика».
        var probe = new ScriptedProbe([IpBlock]);
        var handlerCalls = 0;
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.IpBlocked, (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(AvitoObstacleRecoveryResult.Success());
            }));

        var ex = await Assert.ThrowsAsync<AvitoSessionRestartRequiredException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult("step-done"), CancellationToken.None));

        Assert.Equal(1, ex.RecoveryGeneration);
        Assert.Equal(1, handlerCalls);
        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
        Assert.Null(orchestrator.ActiveObstacle);
    }

    [Fact]
    public async Task RunStepAsync_IpBlockHandlerFailsTwice_ThrowsFirewallException()
    {
        var probe = new ScriptedProbe([], tail: IpBlock);
        var handlerCalls = 0;
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.IpBlocked, (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult(AvitoObstacleRecoveryResult.Failure("капча не пройдена"));
            }));

        var ex = await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        Assert.Equal("firewall", ex.Kind);
        Assert.Equal(2, handlerCalls);
        Assert.Equal(AvitoSessionStatus.RequiresManualAction, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_LoginRequiredWithoutHandler_ThrowsLoginException()
    {
        var probe = new ScriptedProbe([
            new AvitoPageObstacle(AvitoPageObstacleKind.LoginRequired, Url: "https://www.avito.ru/profile/login")
        ]);
        await using var orchestrator = Create(probe);

        await Assert.ThrowsAsync<AvitoLoginRequiredException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task RunStepAsync_UnknownProbeFailsClosedWithoutExecutingStep()
    {
        var probe = new ScriptedProbe([], tail: AvitoPageObstacle.Unknown);
        await using var orchestrator = Create(probe);
        var stepCalls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync(
                _ =>
                {
                    stepCalls++;
                    return Task.FromResult(1);
                },
                CancellationToken.None));

        Assert.Equal(0, stepCalls);
        Assert.Equal(3, probe.ProbeCount);
        // Fail-closed на действии, но не терминал сессии: навигация между страницами
        // законно даёт Unknown, следующий цикл повторит попытку.
        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
    }

    [Fact]
    public async Task RunStepAsync_ProbeThrows_AlsoFailsClosed()
    {
        await using var orchestrator = new AvitoSessionOrchestrator(
            _ => throw new InvalidOperationException("CDP disconnected"),
            _ => Task.FromResult<string?>(null));
        var stepCalls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            orchestrator.RunStepAsync(
                _ =>
                {
                    stepCalls++;
                    return Task.FromResult(1);
                },
                CancellationToken.None));

        Assert.Equal(0, stepCalls);
        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
    }

    [Fact]
    public async Task Observer_PersistentUnknown_DoesNotTerminateSession()
    {
        var probe = new ScriptedProbe([], tail: AvitoPageObstacle.Unknown);
        await using var orchestrator = Create(probe, interval: TimeSpan.FromMilliseconds(10));

        orchestrator.Start();
        await WaitUntilAsync(() => probe.ProbeCount >= 3, TimeSpan.FromSeconds(5));

        Assert.Equal(AvitoSessionStatus.Running, orchestrator.Status);
        Assert.Null(orchestrator.ActiveObstacle);
    }

    [Fact]
    public async Task Observer_DoesNotEnterRecoveryUntilRunningStepReleasesTab()
    {
        var probe = new ScriptedProbe([
            AvitoPageObstacle.None,
            GeeTestCaptcha,
            AvitoPageObstacle.None,
            AvitoPageObstacle.None
        ]);
        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = Create(
            probe,
            interval: TimeSpan.FromSeconds(30),
            configure: o => o.RegisterHandler(AvitoPageObstacleKind.Captcha, (_, _) =>
            {
                handlerEntered.TrySetResult();
                return Task.FromResult(AvitoObstacleRecoveryResult.Success());
            }));

        var generation = orchestrator.RecoveryGeneration;
        var stepTask = orchestrator.RunStepAsync(
            generation,
            async _ =>
            {
                stepEntered.TrySetResult();
                await releaseStep.Task;
                return true;
            },
            CancellationToken.None);
        await stepEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        orchestrator.Start();
        orchestrator.ReportSuspicion();
        await Task.Delay(150);
        Assert.False(handlerEntered.Task.IsCompleted);

        releaseStep.SetResult();
        await stepTask.WaitAsync(TimeSpan.FromSeconds(5));
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => orchestrator.Status == AvitoSessionStatus.Running, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Observer_BackgroundCaptcha_PausesAndResumesWithoutSteps()
    {
        // Наблюдатель сам находит препятствие по тику и восстанавливает страницу.
        var probe = new ScriptedProbe([AvitoPageObstacle.None, GeeTestCaptcha]);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.Captcha, async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await handlerReleased.Task;
                return AvitoObstacleRecoveryResult.Success();
            }));

        orchestrator.Start();

        // Обработчик получил управление — значит, эпизод стартовал без единого рабочего шага.
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(AvitoSessionStatus.Running, orchestrator.Status);

        handlerReleased.SetResult();
        await WaitUntilAsync(() => orchestrator.Status == AvitoSessionStatus.Running, TimeSpan.FromSeconds(10));
        Assert.Null(orchestrator.ActiveObstacle);
    }

    [Fact]
    public async Task WaitReadyAsync_BlocksWhileRecovering_AndResumesAfter()
    {
        var probe = new ScriptedProbe([GeeTestCaptcha]);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = Create(probe, configure: o =>
            o.RegisterHandler(AvitoPageObstacleKind.Captcha, async (_, _) =>
            {
                handlerEntered.TrySetResult();
                await handlerRelease.Task;
                return AvitoObstacleRecoveryResult.Success();
            }));

        orchestrator.Start();
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Рабочая контрольная точка обязана ждать восстановления.
        var readyTask = orchestrator.WaitReadyAsync(CancellationToken.None);
        Assert.False(readyTask.IsCompleted);

        handlerRelease.SetResult();
        await readyTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WaitReadyAsync_TerminalState_ThrowsMappedException()
    {
        var probe = new ScriptedProbe([IpBlock]);
        await using var orchestrator = Create(probe);

        // Доводим эпизод до терминала через шаг…
        await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.RunStepAsync(_ => Task.FromResult(1), CancellationToken.None));

        // …и убеждаемся, что контрольная точка сообщает то же состояние.
        await Assert.ThrowsAsync<AvitoCaptchaDetectedException>(() =>
            orchestrator.WaitReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReportSuspicion_TriggersImmediateProbe()
    {
        // Интервал наблюдателя большой: без подозрения эпизод не начнётся, с подозрением — начнётся.
        var probe = new ScriptedProbe([GeeTestCaptcha]);
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var orchestrator = Create(
            probe,
            interval: TimeSpan.FromSeconds(30),
            configure: o => o.RegisterHandler(AvitoPageObstacleKind.Captcha, (_, _) =>
            {
                handlerEntered.TrySetResult();
                return Task.FromResult(AvitoObstacleRecoveryResult.Success());
            }));

        orchestrator.Start();
        orchestrator.ReportSuspicion();

        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_StopsObserver()
    {
        var probe = new ScriptedProbe([]);
        var orchestrator = Create(probe, interval: TimeSpan.FromMilliseconds(5));
        orchestrator.Start();

        await orchestrator.DisposeAsync();
        var countAtDispose = probe.ProbeCount;

        await Task.Delay(80);
        Assert.Equal(countAtDispose, probe.ProbeCount);
        Assert.Equal(AvitoSessionStatus.Stopped, orchestrator.Status);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Условие не выполнено за отведённое время.");
    }
}
