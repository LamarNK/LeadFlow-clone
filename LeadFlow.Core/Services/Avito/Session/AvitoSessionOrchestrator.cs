using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.AdsPower;

namespace LeadFlow.Core.Services.Avito.Session;

/// <summary>Стадия оркестратора сессии. Отдельна от типа препятствия на странице.</summary>
public enum AvitoSessionStatus
{
    /// <summary>Рабочие шаги разрешены.</summary>
    Running,

    /// <summary>Обнаружено препятствие, ждём завершения текущего короткого шага.</summary>
    PauseRequested,

    /// <summary>Обработчик препятствия владеет вкладкой.</summary>
    Recovering,

    /// <summary>Подтверждаем исчезновение препятствия повторными проверками.</summary>
    Verifying,

    /// <summary>Терминальное состояние: восстановление невозможно, нужно действие оператора.</summary>
    RequiresManualAction,

    Stopping,
    Stopped
}

/// <summary>Обработчик одного типа препятствия. Вызывается с исключительным владением вкладкой.</summary>
public delegate Task<AvitoObstacleRecoveryResult> AvitoObstacleHandler(
    AvitoPageObstacle obstacle,
    CancellationToken cancellationToken);

/// <summary>
/// Оркестратор браузерной сессии Avito: фоновый наблюдатель проверяет страницу на препятствия
/// (капча в модалке, блок IP, потеря входа, ошибка страницы), приостанавливает новые рабочие шаги,
/// запускает один обработчик, подтверждает результат и возобновляет работу. Рабочие сценарии
/// (сбор откликов, обход чатов) обязаны проходить шаги через <see cref="RunStepAsync"/> и
/// контрольные точки <see cref="WaitReadyAsync"/> — тогда защита не зависит от того,
/// какой сценарий сейчас занят вкладкой.
/// </summary>
/// <remarks>
/// Один эпизод препятствия обрабатывается ровно один раз (флаг-состояние вместо гонки сигналов).
/// Успех обработчика не считается восстановлением: нужно две подряд «чистые» проверки.
/// Короткие шаги не прерываются посередине — пауза действует между шагами; уже отправленный
/// в браузер клик отменить нельзя, поэтому после восстановления сценарий обязан перечитать страницу.
/// </remarks>
public sealed class AvitoSessionOrchestrator : IAsyncDisposable
{
    private const int MaxRecoveryAttemptsPerEpisode = 2;
    private const int ClearProbesRequiredToResume = 2;
    private const int MaxVerifyProbes = 4;
    private const int MaxUnknownProbeAttempts = 3;

    /// <summary>
    /// Сколько подряд CDP-таймаутов шагов/проб считается смертью сессии: дальнейшие шаги
    /// будут лишь терять по 30 с каждый — проход прерывается (AvitoSessionDeadException).
    /// </summary>
    public const int MaxConsecutiveCdpFailures = 3;
    private static readonly TimeSpan VerifyProbeInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan UnknownProbeRetryInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DefaultRecoveryAttemptTimeout = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan UnknownLogThrottle = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<AvitoPageObstacle>> probeAsync;
    private readonly Func<CancellationToken, Task<string?>> fetchHtmlAsync;
    private readonly TimeSpan probeInterval;
    private readonly TimeSpan minStepProbeInterval;
    private readonly TimeSpan recoveryAttemptTimeout;
    private readonly Dictionary<AvitoPageObstacleKind, AvitoObstacleHandler> handlers = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim tabOwnership = new(1, 1);
    private readonly SemaphoreSlim probeOwnership = new(1, 1);
    private readonly CancellationTokenSource lifecycleCts = new();

    private AvitoSessionStatus status = AvitoSessionStatus.Running;
    private AvitoPageObstacle? activeObstacle;
    private TaskCompletionSource resumeSignal = CreateSignal();
    private TaskCompletionSource suspicionSignal = CreateSignal();
    private Task? observerTask;
    private long lastProbeTicks;
    private long disposed;
    private long recoveryGeneration;
    private long recoveryEpisodesCompleted;
    private string? lastRecoveredObstacleKind;
    private int consecutiveCdpFailures;
    private DateTime lastUnknownLogUtc = DateTime.MinValue;

    public AvitoSessionOrchestrator(
        Func<CancellationToken, Task<AvitoPageObstacle>> probeAsync,
        Func<CancellationToken, Task<string?>> fetchHtmlAsync,
        TimeSpan? probeInterval = null,
        TimeSpan? recoveryAttemptTimeout = null)
    {
        this.probeAsync = probeAsync;
        this.fetchHtmlAsync = fetchHtmlAsync;
        this.probeInterval = probeInterval ?? TimeSpan.FromSeconds(1);
        this.recoveryAttemptTimeout = recoveryAttemptTimeout ?? DefaultRecoveryAttemptTimeout;
        minStepProbeInterval = TimeSpan.FromMilliseconds(this.probeInterval.TotalMilliseconds * 0.75);
    }

    public AvitoSessionStatus Status
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }
    }

    /// <summary>Препятствие текущего (или последнего терминального) эпизода.</summary>
    public AvitoPageObstacle? ActiveObstacle
    {
        get
        {
            lock (gate)
            {
                return activeObstacle;
            }
        }
    }

    /// <summary>
    /// Увеличивается после каждого подтверждённого восстановления. Рабочие сценарии используют
    /// поколение как инвалидатор DOM-индексов и снимков, полученных до reload/navigation.
    /// </summary>
    public long RecoveryGeneration => Interlocked.Read(ref recoveryGeneration);

    /// <summary>Всего подтверждённых восстановлений страницы за жизнь сессии.</summary>
    public long RecoveryEpisodesCompleted => Interlocked.Read(ref recoveryEpisodesCompleted);

    /// <summary>Последнее препятствие, успешно устранённое оркестратором (диагностика петель восстановления).</summary>
    public string? LastRecoveredObstacleKind
    {
        get
        {
            lock (gate)
            {
                return lastRecoveredObstacleKind;
            }
        }
    }

    public void RegisterHandler(AvitoPageObstacleKind kind, AvitoObstacleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            handlers[kind] = handler;
        }
    }

    /// <summary>Запускает фонового наблюдателя. Повторный вызов — no-op.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (observerTask is not null
                || status is AvitoSessionStatus.Stopping or AvitoSessionStatus.Stopped)
            {
                return;
            }

            observerTask = Task.Run(() => ObserveAsync(lifecycleCts.Token));
        }
    }

    /// <summary>
    /// Просит наблюдателя проверить страницу немедленно (например, чат не открылся после клика —
    /// типичный признак перехвата клика капча-модалкой).
    /// </summary>
    public void ReportSuspicion()
    {
        lock (gate)
        {
            suspicionSignal.TrySetResult();
        }
    }

    /// <summary>
    /// Контрольная точка сценария: ждёт «чистую» страницу. В терминальном состоянии бросает
    /// исключение, понятное мониторингу (капча/блок IP/нужен вход). Используется между
    /// кандидатами, перед важными действиями и в точках возобновления.
    /// </summary>
    public Task WaitReadyAsync(CancellationToken cancellationToken) =>
        WaitReadyAsync(expectedRecoveryGeneration: null, cancellationToken);

    public async Task WaitReadyAsync(long expectedRecoveryGeneration, CancellationToken cancellationToken) =>
        await WaitReadyAsync((long?)expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);

    /// <summary>Идёт ли сейчас эпизод восстановления (внутри обработчика повторный вызов запрещён).</summary>
    public bool IsRecovering
    {
        get
        {
            lock (gate)
            {
                return status is AvitoSessionStatus.PauseRequested
                    or AvitoSessionStatus.Recovering
                    or AvitoSessionStatus.Verifying;
            }
        }
    }

    private async Task WaitReadyAsync(long? expectedRecoveryGeneration, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            AvitoPageObstacle? terminalObstacle = null;
            lock (gate)
            {
                if (consecutiveCdpFailures >= MaxConsecutiveCdpFailures)
                {
                    // Мёртвая сессия: чекпойнт тоже обязан падать быстро, а не отпускать
                    // сценарий в очередной 30-секундный evaluate.
                    throw new AvitoSessionDeadException(consecutiveCdpFailures);
                }

                if (status == AvitoSessionStatus.Running)
                {
                    ThrowIfRecoveryGenerationChanged(expectedRecoveryGeneration);
                    return;
                }

                if (status is AvitoSessionStatus.Stopping or AvitoSessionStatus.Stopped)
                {
                    throw new OperationCanceledException("Оркестратор сессии Avito останавливается.");
                }

                if (status == AvitoSessionStatus.RequiresManualAction)
                {
                    terminalObstacle = activeObstacle;
                    wait = Task.CompletedTask;
                }
                else
                {
                    wait = resumeSignal.Task;
                }
            }

            if (terminalObstacle is not null)
            {
                throw await BuildTerminalExceptionAsync(terminalObstacle, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Сигнал сменился на новый — перечитываем состояние.
            }
        }
    }

    /// <summary>
    /// Выполняет короткий рабочий шаг под контролем оркестратора: проверка страницы перед шагом
    /// (с троттлингом), ожидание «чистой» страницы, затем исполнение с владением вкладкой.
    /// В терминальном состоянии бросает исключение до начала шага.
    /// </summary>
    public async Task<T> RunStepAsync<T>(Func<CancellationToken, Task<T>> step, CancellationToken cancellationToken)
    {
        return await RunStepAsync(RecoveryGeneration, step, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> RunStepAsync<T>(
        long expectedRecoveryGeneration,
        Func<CancellationToken, Task<T>> step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ThrowIfSessionDead();
        try
        {
            await WaitReadyAsync(expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);
            await MaybeProbeBeforeStepAsync(cancellationToken).ConfigureAwait(false);
            await WaitReadyAsync(expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);

            while (true)
            {
                await tabOwnership.WaitAsync(cancellationToken).ConfigureAwait(false);
                var readyToRun = false;
                try
                {
                    // Между WaitReady и захватом семафора observer мог поставить PauseRequested.
                    // Не выполняем действие в этом окне: освобождаем вкладку для recovery и ждём заново.
                    readyToRun = Status == AvitoSessionStatus.Running;
                    if (readyToRun)
                    {
                        ThrowIfRecoveryGenerationChanged(expectedRecoveryGeneration);
                        var result = await step(cancellationToken).ConfigureAwait(false);
                        ResetCdpFailureStreak();
                        return result;
                    }
                }
                finally
                {
                    try
                    {
                        tabOwnership.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Сессия завершается — владение уже не важно.
                    }
                }

                if (!readyToRun)
                {
                    await WaitReadyAsync(expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (AvitoSessionDeadException)
        {
            throw;
        }
        catch (TimeoutException ex) when (AdsPowerCdpGuard.IsCdpTimeout(ex))
        {
            // Шаг или проба упали CDP-таймаутом: серия таких = мёртвая сессия,
            // следующий шаг упадёт сразу, а не после своих 30 секунд.
            RegisterCdpFailure();
            throw;
        }
    }

    public async Task RunStepAsync(Func<CancellationToken, Task> step, CancellationToken cancellationToken)
    {
        await RunStepAsync(RecoveryGeneration, step, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunStepAsync(
        long expectedRecoveryGeneration,
        Func<CancellationToken, Task> step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        await RunStepAsync(
                expectedRecoveryGeneration,
                async ct =>
                {
                    await step(ct).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Немедленная подтверждённая проверка после подозрительного результата действия.</summary>
    public async Task CheckNowAsync(long expectedRecoveryGeneration, CancellationToken cancellationToken)
    {
        await WaitReadyAsync(expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);
        var obstacle = await ProbeUntilKnownOrThrowAsync(cancellationToken).ConfigureAwait(false);
        if (obstacle.Kind != AvitoPageObstacleKind.None && TryBeginEpisode(obstacle))
        {
            await RunEpisodeAsync(obstacle, cancellationToken).ConfigureAwait(false);
        }

        await WaitReadyAsync(expectedRecoveryGeneration, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Проверка без инвалидации поколения (для точечных checkpoint-ов старых сценариев:
    /// объявления, кошелёк, пополнение). Восстановление не требует перезапуска вызванного
    /// сценария — вызывающий код сам решает, повторять ли операцию.
    /// </summary>
    public Task CheckNowAsync(CancellationToken cancellationToken) =>
        CheckNowAsync(RecoveryGeneration, cancellationToken);

    /// <summary>
    /// Контрольная точка перед действием: состояние обязано быть определимым.
    /// Устойчивый Unknown — fail-closed (переходный CDP-сбой для мониторинга), но не терминал сессии:
    /// навигация между страницами законно даёт Unknown, следующий цикл повторит попытку.
    /// </summary>
    private async Task<AvitoPageObstacle> ProbeUntilKnownOrThrowAsync(CancellationToken cancellationToken)
    {
        var obstacle = await ProbeUntilKnownAsync(cancellationToken).ConfigureAwait(false);
        if (obstacle.Kind == AvitoPageObstacleKind.Unknown)
        {
            LogThrottledUnknown();
            throw AdsPowerCdpGuard.Timeout(
                "подтверждение состояния страницы",
                TimeSpan.FromMilliseconds(UnknownProbeRetryInterval.TotalMilliseconds * MaxUnknownProbeAttempts));
        }

        return obstacle;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        lock (gate)
        {
            status = AvitoSessionStatus.Stopping;
        }

        lifecycleCts.Cancel();
        SignalResumeLocked();

        var observer = observerTask;
        if (observer is not null)
        {
            try
            {
                await observer.ConfigureAwait(false);
            }
            catch
            {
                // Наблюдатель останавливается молча.
            }
        }

        lock (gate)
        {
            status = AvitoSessionStatus.Stopped;
        }

        lifecycleCts.Dispose();
    }

    /// <summary>Быстрый fail: серия подряд CDP-таймаутов уже достигла потолка — не ждём
    /// очередных 30-секундных шагов, обрываем проход немедленно.</summary>
    private void ThrowIfSessionDead()
    {
        lock (gate)
        {
            if (consecutiveCdpFailures >= MaxConsecutiveCdpFailures)
            {
                throw new AvitoSessionDeadException(consecutiveCdpFailures);
            }
        }
    }

    private void RegisterCdpFailure()
    {
        lock (gate)
        {
            consecutiveCdpFailures++;
        }
    }

    private void ResetCdpFailureStreak()
    {
        lock (gate)
        {
            consecutiveCdpFailures = 0;
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task suspicion;
                lock (gate)
                {
                    suspicion = suspicionSignal.Task;
                }

                var interval = Task.Delay(probeInterval, cancellationToken);
                var completed = await Task.WhenAny(suspicion, interval).ConfigureAwait(false);
                _ = completed;
                lock (gate)
                {
                    // Поглощаем сигнал подозрения; новый вызов ReportSuspicion увидит свежий TCS.
                    suspicionSignal = CreateSignal();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (Status != AvitoSessionStatus.Running)
                {
                    // Эпизод уже идёт (например, стартовал с контрольной точки шага).
                    continue;
                }

                var obstacle = await ProbeUntilKnownAsync(cancellationToken).ConfigureAwait(false);
                if (obstacle.Kind == AvitoPageObstacleKind.None)
                {
                    continue;
                }

                // Unknown в фоне — не эпизод: навигация/перерисовка страницы законны.
                // Fail-closed гарантируется проверкой перед действием (RunStepAsync/CheckNow).
                if (obstacle.Kind == AvitoPageObstacleKind.Unknown)
                {
                    LogThrottledUnknown();
                    continue;
                }

                if (!TryBeginEpisode(obstacle))
                {
                    continue;
                }

                await RunEpisodeAsync(obstacle, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito session orchestrator: ошибка наблюдателя — {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "session.orchestrator",
                    memberName: nameof(ObserveAsync),
                    filePath: nameof(AvitoSessionOrchestrator) + ".cs");
            }
        }
    }

    /// <summary>Проверка перед шагом: дешёвый probe, если последний давно (действие «по факту»).</summary>
    private async Task MaybeProbeBeforeStepAsync(CancellationToken cancellationToken)
    {
        var lastProbe = new DateTime(Interlocked.Read(ref lastProbeTicks), DateTimeKind.Utc);
        if (DateTime.UtcNow - lastProbe < minStepProbeInterval)
        {
            return;
        }

        var obstacle = await ProbeUntilKnownOrThrowAsync(cancellationToken).ConfigureAwait(false);
        if (obstacle.Kind != AvitoPageObstacleKind.None && TryBeginEpisode(obstacle))
        {
            await RunEpisodeAsync(obstacle, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AvitoPageObstacle> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        await probeOwnership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AvitoPageObstacle obstacle;
            try
            {
                obstacle = await probeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                obstacle = AvitoPageObstacle.Unknown;
            }

            Interlocked.Exchange(ref lastProbeTicks, DateTime.UtcNow.Ticks);
            return obstacle;
        }
        finally
        {
            probeOwnership.Release();
        }
    }

    private async Task<AvitoPageObstacle> ProbeUntilKnownAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxUnknownProbeAttempts; attempt++)
        {
            var obstacle = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            if (obstacle.Kind != AvitoPageObstacleKind.Unknown)
            {
                return obstacle;
            }

            LogThrottledUnknown();
            if (attempt < MaxUnknownProbeAttempts)
            {
                await Task.Delay(UnknownProbeRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        return AvitoPageObstacle.Unknown;
    }

    private bool TryBeginEpisode(AvitoPageObstacle obstacle)
    {
        lock (gate)
        {
            if (status != AvitoSessionStatus.Running)
            {
                return false;
            }

            status = AvitoSessionStatus.PauseRequested;
            activeObstacle = obstacle;
            return true;
        }
    }

    private async Task RunEpisodeAsync(AvitoPageObstacle obstacle, CancellationToken cancellationToken)
    {
        LogEpisodeStarted(obstacle);
        try
        {
            await tabOwnership.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AbortEpisode();
            return;
        }

        try
        {
            for (var attempt = 1; attempt <= MaxRecoveryAttemptsPerEpisode; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AvitoObstacleHandler? handler;
                lock (gate)
                {
                    status = AvitoSessionStatus.Recovering;
                    handlers.TryGetValue(obstacle.Kind, out handler);
                }

                if (handler is null)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Avito session orchestrator: для препятствия {obstacle.Kind} нет обработчика — требуется действие оператора.",
                        DeskLinkAuditLogLevel.Warning,
                        errorKey: "session.orchestrator",
                        memberName: nameof(RunEpisodeAsync),
                        filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                        properties: BuildObstacleProperties(obstacle, "episode_no_handler"));
                    break;
                }

                AvitoObstacleRecoveryResult result;
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(recoveryAttemptTimeout);
                try
                {
                    result = await handler(obstacle, attemptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
                {
                    result = AvitoObstacleRecoveryResult.Failure(
                        $"обработчик не завершился за {recoveryAttemptTimeout.TotalSeconds:0} с");
                }
                catch (Exception ex)
                {
                    result = AvitoObstacleRecoveryResult.Failure(ex.Message);
                }

                if (!result.Recovered)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Avito session orchestrator: попытка {attempt}/{MaxRecoveryAttemptsPerEpisode} устранить {obstacle.Kind} не удалась{(string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $" — {result.Message}")}.",
                        DeskLinkAuditLogLevel.Warning,
                        errorKey: "session.orchestrator",
                        memberName: nameof(RunEpisodeAsync),
                        filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                        properties: BuildObstacleProperties(obstacle, "episode_attempt_failed"));
                    continue;
                }

                lock (gate)
                {
                    status = AvitoSessionStatus.Verifying;
                }

                if (await VerifyClearAsync(cancellationToken).ConfigureAwait(false))
                {
                    lock (gate)
                    {
                        Interlocked.Increment(ref recoveryGeneration);
                        Interlocked.Increment(ref recoveryEpisodesCompleted);
                        lastRecoveredObstacleKind = obstacle.Kind.ToString();
                        status = AvitoSessionStatus.Running;
                        activeObstacle = null;
                    }

                    SignalResumeLocked();
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Avito session orchestrator: препятствие {obstacle.Kind} устранено, работа возобновлена.",
                        DeskLinkAuditLogLevel.Info,
                        errorKey: "session.orchestrator",
                        memberName: nameof(RunEpisodeAsync),
                        filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                        properties: BuildObstacleProperties(obstacle, "episode_recovered"));
                    return;
                }

                _ = GlobalLogger.Instance.LogAsync(
                    $"Avito session orchestrator: проверка не подтвердила исчезновение {obstacle.Kind} после попытки {attempt}.",
                    DeskLinkAuditLogLevel.Warning,
                    errorKey: "session.orchestrator",
                    memberName: nameof(RunEpisodeAsync),
                    filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                    properties: BuildObstacleProperties(obstacle, "episode_verify_failed"));
            }

            lock (gate)
            {
                status = AvitoSessionStatus.RequiresManualAction;
            }

            SignalResumeLocked();
            _ = GlobalLogger.Instance.LogAsync(
                $"Avito session orchestrator: {obstacle.Kind} не устранено — сессия требует действия оператора.",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "session.orchestrator",
                memberName: nameof(RunEpisodeAsync),
                filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                properties: BuildObstacleProperties(obstacle, "episode_terminal"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortEpisode();
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                activeObstacle = AvitoPageObstacle.Unknown;
                status = AvitoSessionStatus.RequiresManualAction;
            }

            SignalResumeLocked();
            _ = GlobalLogger.Instance.LogAsync(
                $"Avito session orchestrator: непредвиденная ошибка восстановления — {ex.Message}",
                DeskLinkAuditLogLevel.Error,
                errorKey: "session.orchestrator",
                memberName: nameof(RunEpisodeAsync),
                filePath: nameof(AvitoSessionOrchestrator) + ".cs",
                properties: BuildObstacleProperties(obstacle, "episode_error"));
        }
        finally
        {
            try
            {
                tabOwnership.Release();
            }
            catch (ObjectDisposedException)
            {
                // Сессия завершается.
            }
            catch (SemaphoreFullException)
            {
                // Отмена до захвата владения.
            }
        }
    }

    /// <summary>Возобновление после двух подряд «чистых» проверок.</summary>
    private async Task<bool> VerifyClearAsync(CancellationToken cancellationToken)
    {
        var consecutiveClear = 0;
        for (var probe = 0; probe < MaxVerifyProbes; probe++)
        {
            if (probe > 0)
            {
                await Task.Delay(VerifyProbeInterval, cancellationToken).ConfigureAwait(false);
            }

            var obstacle = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
            if (obstacle.Kind == AvitoPageObstacleKind.None)
            {
                consecutiveClear++;
                if (consecutiveClear >= ClearProbesRequiredToResume)
                {
                    return true;
                }
            }
            else
            {
                consecutiveClear = 0;
            }
        }

        return false;
    }

    private void AbortEpisode()
    {
        lock (gate)
        {
            // Сессия отменяется: возвращаем Running, чтобы оставшиеся контрольные точки
            // не зависли — их собственные токены отмены завершат сценарий.
            if (status is AvitoSessionStatus.PauseRequested
                or AvitoSessionStatus.Recovering
                or AvitoSessionStatus.Verifying)
            {
                status = AvitoSessionStatus.Running;
                activeObstacle = null;
            }
        }

        SignalResumeLocked();
    }

    private async Task<Exception> BuildTerminalExceptionAsync(
        AvitoPageObstacle? obstacle,
        CancellationToken cancellationToken)
    {
        string? html = null;
        try
        {
            html = await fetchHtmlAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Диагностика по возможности.
        }

        return obstacle?.Kind switch
        {
            AvitoPageObstacleKind.Captcha => new AvitoCaptchaDetectedException(
                string.IsNullOrWhiteSpace(obstacle.CaptchaKind) ? "captcha" : obstacle.CaptchaKind!,
                obstacle.Url,
                html),
            AvitoPageObstacleKind.IpBlocked => new AvitoCaptchaDetectedException(
                "firewall",
                obstacle.Url,
                html),
            AvitoPageObstacleKind.Unknown => AdsPowerCdpGuard.Timeout(
                "проверка препятствий страницы",
                TimeSpan.FromTicks(UnknownProbeRetryInterval.Ticks * MaxUnknownProbeAttempts)),
            _ => new AvitoLoginRequiredException(obstacle?.Url, obstacle?.Title)
        };
    }

    private void ThrowIfRecoveryGenerationChanged(long? expectedRecoveryGeneration)
    {
        if (expectedRecoveryGeneration is not null
            && RecoveryGeneration != expectedRecoveryGeneration.Value)
        {
            throw new AvitoSessionRestartRequiredException(RecoveryGeneration);
        }
    }

    private void SignalResumeLocked()
    {
        lock (gate)
        {
            var old = resumeSignal;
            resumeSignal = CreateSignal();
            old.TrySetResult();
        }
    }

    private void LogEpisodeStarted(AvitoPageObstacle obstacle)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"Avito session orchestrator: обнаружено препятствие {obstacle.Kind} ({obstacle.CaptchaKind ?? "n/a"}) — рабочие шаги приостанавливаются.",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "session.orchestrator",
            memberName: nameof(RunEpisodeAsync),
            filePath: nameof(AvitoSessionOrchestrator) + ".cs",
            properties: BuildObstacleProperties(obstacle, "episode_detected"));
    }

    private void LogThrottledUnknown()
    {
        if (DateTime.UtcNow - lastUnknownLogUtc < UnknownLogThrottle)
        {
            return;
        }

        lastUnknownLogUtc = DateTime.UtcNow;
        _ = GlobalLogger.Instance.LogAsync(
            "Avito session orchestrator: состояние страницы не удалось определить (проверка пропущена).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: "session.orchestrator",
            memberName: nameof(ObserveAsync),
            filePath: nameof(AvitoSessionOrchestrator) + ".cs",
            properties: new Dictionary<string, object?> { ["step"] = "probe_unknown" });
    }

    private static Dictionary<string, object?> BuildObstacleProperties(AvitoPageObstacle obstacle, string step) =>
        new()
        {
            ["step"] = step,
            ["obstacle.kind"] = obstacle.Kind.ToString(),
            ["obstacle.captchaKind"] = obstacle.CaptchaKind,
            ["obstacle.url"] = obstacle.Url,
            ["obstacle.signals"] = obstacle.Signals is { Count: > 0 }
                ? string.Join(",", obstacle.Signals)
                : null
        };
}
