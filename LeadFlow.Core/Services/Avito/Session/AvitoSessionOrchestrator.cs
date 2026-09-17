using LeadFlow.Core.Logging.Audit;

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
    private static readonly TimeSpan VerifyProbeInterval = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan UnknownLogThrottle = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<AvitoPageObstacle>> probeAsync;
    private readonly Func<CancellationToken, Task<string?>> fetchHtmlAsync;
    private readonly TimeSpan probeInterval;
    private readonly TimeSpan minStepProbeInterval;
    private readonly Dictionary<AvitoPageObstacleKind, AvitoObstacleHandler> handlers = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim tabOwnership = new(1, 1);
    private readonly CancellationTokenSource lifecycleCts = new();

    private AvitoSessionStatus status = AvitoSessionStatus.Running;
    private AvitoPageObstacle? activeObstacle;
    private TaskCompletionSource resumeSignal = CreateSignal();
    private TaskCompletionSource suspicionSignal = CreateSignal();
    private Task? observerTask;
    private long lastProbeTicks;
    private long disposed;
    private DateTime lastUnknownLogUtc = DateTime.MinValue;

    public AvitoSessionOrchestrator(
        Func<CancellationToken, Task<AvitoPageObstacle>> probeAsync,
        Func<CancellationToken, Task<string?>> fetchHtmlAsync,
        TimeSpan? probeInterval = null)
    {
        this.probeAsync = probeAsync;
        this.fetchHtmlAsync = fetchHtmlAsync;
        this.probeInterval = probeInterval ?? TimeSpan.FromSeconds(1);
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
    public async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (status == AvitoSessionStatus.Running)
                {
                    return;
                }

                if (status is AvitoSessionStatus.Stopping or AvitoSessionStatus.Stopped)
                {
                    throw new OperationCanceledException("Оркестратор сессии Avito останавливается.");
                }

                if (status == AvitoSessionStatus.RequiresManualAction)
                {
                    wait = Task.CompletedTask;
                }
                else
                {
                    wait = resumeSignal.Task;
                }
            }

            if (wait.IsCompleted)
            {
                var obstacle = ActiveObstacle;
                throw await BuildTerminalExceptionAsync(obstacle, cancellationToken).ConfigureAwait(false);
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
        ArgumentNullException.ThrowIfNull(step);
        await WaitReadyAsync(cancellationToken).ConfigureAwait(false);
        await MaybeProbeBeforeStepAsync(cancellationToken).ConfigureAwait(false);
        await WaitReadyAsync(cancellationToken).ConfigureAwait(false);

        await tabOwnership.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await step(cancellationToken).ConfigureAwait(false);
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

                var obstacle = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
                if (obstacle.Kind == AvitoPageObstacleKind.None)
                {
                    continue;
                }

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

        var obstacle = await ProbeCoreAsync(cancellationToken).ConfigureAwait(false);
        if (obstacle.Kind is not (AvitoPageObstacleKind.None or AvitoPageObstacleKind.Unknown)
            && TryBeginEpisode(obstacle))
        {
            await RunEpisodeAsync(obstacle, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AvitoPageObstacle> ProbeCoreAsync(CancellationToken cancellationToken)
    {
        var obstacle = await probeAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref lastProbeTicks, DateTime.UtcNow.Ticks);
        return obstacle;
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
                try
                {
                    result = await handler(obstacle, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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
            _ => new AvitoLoginRequiredException(obstacle.Url, obstacle.Title)
        };
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
