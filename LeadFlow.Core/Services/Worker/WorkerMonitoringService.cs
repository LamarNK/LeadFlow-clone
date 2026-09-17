using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Data;
using LeadFlow.Core.Services;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Browser;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Headless AdsPower/Multilogin monitoring loop extracted from <c>MonitoringService</c> (no WebView2/Browser/Bitrix).
/// New candidates are published via <see cref="INewCandidateSink"/>.
/// </summary>
public sealed class WorkerMonitoringService(
    IWorkerConfigProvider configProvider,
    IMonitoringRepository repository,
    INewCandidateSink candidateSink,
    IWorkerEventSink eventSink,
    IWorkerTelemetrySink telemetrySink,
    IWorkerDiagnosticsUploader diagnosticsUploader,
    IPhoneNormalizer phoneNormalizer,
    ICandidateDuplicateRepository duplicateRepository,
    ICandidateParser candidateParser,
    IAvitoResponseSource avitoResponseSource,
    AvitoDemoResponseSource avitoDemoResponseSource,
    AvitoParserService avitoParser,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService,
    IWorkerActivityReporter activityReporter,
    IWorkerPendingUpdateCoordinator pendingUpdateCoordinator,
    IBrowserMonitorSource browserMonitorSource,
    IResponsePhoneObservationStore? phoneObservationStore = null,
    IMonitoringCycleJournal? monitoringCycleJournal = null,
    IOutboundChatDispatch? outboundChatDispatch = null,
    IMultiloginCdpConnector? multiloginCdpConnector = null,
    WorkerAccountSessionFactory? accountSessionFactory = null,
    LocalChromeAccountLock? localChromeAccountLock = null,
    ILocalChromeBrowserLauncher? localChromeLauncher = null,
    IAvitoGeeTestSolver? geeTestSolver = null,
    IWorkerTopUpHistoryConfirmation? topUpHistoryConfirmation = null) : IWorkerMonitoringService
{
    private readonly IResponsePhoneObservationStore _phoneObservationStore =
        phoneObservationStore ?? new NullResponsePhoneObservationStore();
    private readonly IMonitoringCycleJournal _cycleJournal =
        monitoringCycleJournal ?? NullMonitoringCycleJournal.Instance;
    private readonly IOutboundChatDispatch _outboundChat =
        outboundChatDispatch ?? NullOutboundChatDispatch.Instance;
    private readonly WorkerAccountSessionFactory _accountSessions =
        accountSessionFactory ?? new WorkerAccountSessionFactory(
            adsPowerAvitoAutomationService,
            multiloginCdpConnector);
    private readonly LocalChromeAccountLock _localChromeLock =
        localChromeAccountLock ?? new LocalChromeAccountLock();
    private readonly ILocalChromeBrowserLauncher? _localChromeLauncher = localChromeLauncher;
    private readonly IAvitoGeeTestSolver? _geeTestSolver = geeTestSolver;
    private readonly IWorkerTopUpHistoryConfirmation? _topUpHistoryConfirmation = topUpHistoryConfirmation;

    private const int LoopRecoveryPauseMinutes = 12;
    private const int MaxLoopRecoveryFailuresBeforeStop = 10;

    private readonly DebouncedWorkerTelemetryPusher _telemetryPusher = new(telemetrySink);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _consecutiveMonitoringLoopFailures;
    /// <summary>Сколько account-проходов с последней orphan-уборки (вместо «полного цикла»).</summary>
    private int _completedPassesSinceBrowserHousekeeping;
    private DateOnly? _lastBrowserHousekeepingLocalDate;
    private volatile bool _captchaHold;
    private readonly SemaphoreSlim _monitorScreencastGate = new(1, 1);
    private readonly Dictionary<Guid, BrowserMonitorScreencastCapture> _monitorScreencasts = [];
    /// <summary>Когда аккаунт снова можно брать (своя пауза после прохода + close).</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _accountNextEligibleUtc = new();
    /// <summary>Сколько подряд «тихих» проходов у аккаунта (для quiet backoff delay).</summary>
    private readonly ConcurrentDictionary<Guid, int> _accountQuietStreak = new();
    /// <summary>Один раз за процесс логируем восстановленную паузу аккаунта.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _loggedResumeRestored = new();
    private readonly ConcurrentDictionary<Guid, byte> _busyAccounts = new();
    private readonly SemaphoreSlim _scheduleWake = new(0, 1);
    private int _immediatePassRequested;

    public bool IsActive { get; private set; }
    public bool IsCaptchaHold => _captchaHold;

    public bool IsAccountBusy(Guid accountId) => _busyAccounts.ContainsKey(accountId);

    public void RequestImmediatePass()
    {
        Interlocked.Exchange(ref _immediatePassRequested, 1);
        try
        {
            _scheduleWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already queued; one is sufficient.
        }
    }

    public async Task EnterCaptchaHoldAsync()
    {
        BrowserMonitorScreencastCapture[] captures;
        await _monitorScreencastGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _captchaHold = true;
            captures = [.. _monitorScreencasts.Values];
            _monitorScreencasts.Clear();
        }
        finally
        {
            _monitorScreencastGate.Release();
        }

        foreach (var capture in captures)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }

        await GlobalLogger.Instance.LogAsync(
                "Мониторинг приостановлен для сессии капчи; monitor screencast остановлены (текущие браузеры не закрываются).",
                DeskLinkAuditLogLevel.Info)
            .ConfigureAwait(false);
    }

    public void ExitCaptchaHold()
    {
        _captchaHold = false;
        _ = GlobalLogger.Instance.LogAsync(
            "Мониторинг возобновлён после сессии капчи.",
            DeskLinkAuditLogLevel.Info);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsActive = true;
        _accountQuietStreak.Clear();
        // После stop/start счётчик проходов обнуляется; календарный день учитывается отдельно.
        _completedPassesSinceBrowserHousekeeping = 0;
        _ = GlobalLogger.Instance.LogAsync("Мониторинг воркера запущен.", DeskLinkAuditLogLevel.Info);
        _loopTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cycleJournal.AbortOpenCycles(
            "worker-stopped",
            "Мониторинг остановлен до завершения прохода.");
        try
        {
            await _cycleJournal
                .FlushAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker: не удалось отправить прерванные циклы при остановке: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }

        if (_loopTask is not null)
        {
            await _loopTask.ConfigureAwait(false);
        }

        IsActive = false;
        _cts.Dispose();
        _cts = null;
        _ = GlobalLogger.Instance.LogAsync("Мониторинг воркера остановлен.", DeskLinkAuditLogLevel.Info);
        activityReporter.ReportStopped();

        // При остановке мониторинга закрываем все известные браузеры (в т.ч. открытые оператором).
        try
        {
            var config = await configProvider.GetConfigAsync(CancellationToken.None).ConfigureAwait(false);
            await CloseAllKnownAdsPowerBrowsersAsync(
                    config.Accounts,
                    reason: "остановка мониторинга",
                    cancellationToken: CancellationToken.None,
                    ignoreCancellation: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker: уборка браузеров при остановке не удалась: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }

        await _telemetryPusher.PushNowAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Независимые аккаунты: start → work → browser/stop → своя пауза → снова.
            // Не ждём «хвост» списка: слот сразу уходит следующему due.
            var running = new List<AccountCycleJob>();
            var lastLoggedParallelism = -1;
            var launchSlot = 0;
            WorkerMonitoringLogger.CycleStarted(0);
            activityReporter.ReportCycle(0);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_captchaHold)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    configProvider.InvalidateConfigCache();
                    var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
                    var settings = ToAppSettings(config);
                    var accounts = SelectRunnableAccounts(config);

                    if (accounts.Count == 0)
                    {
                        if (running.Count > 0)
                        {
                            var doneEmpty = await Task.WhenAny(running.Select(static j => j.Task))
                                .ConfigureAwait(false);
                            var finishedEmpty = running.First(j => j.Task == doneEmpty);
                            running.Remove(finishedEmpty);
                            try
                            {
                                await ApplyCompletedPassScheduleAsync(finishedEmpty, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // ignore
                            }

                            continue;
                        }

                        WorkerMonitoringLogger.CycleSkippedNoAccounts();
                        activityReporter.ReportNoEnabledAccounts();
                        await Task.Delay(
                                TimeSpan.FromSeconds(MonitoringTiming.NoAccountsConfigPollSeconds),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    // MSI: нет «общей паузы цикла» — вместо неё drain:
                    // не стартуем новые, ждём пока доработают текущие, фаза Waiting → install.
                    var updatePending = pendingUpdateCoordinator.HasPendingInstall;
                    if (updatePending)
                    {
                        if (running.Count == 0)
                        {
                            activityReporter.ReportWaiting(
                                DateTime.UtcNow,
                                pendingUpdateCoordinator.BuildWaitingMessage(TimeSpan.Zero)
                                ?? "Пауза · установка обновления");
                            if (pendingUpdateCoordinator.TryApplyPendingInstallAtPause())
                            {
                                break;
                            }

                            await Task.Delay(
                                    TimeSpan.FromSeconds(MonitoringTiming.PendingUpdateRetrySeconds),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }

                        activityReporter.ReportWaiting(
                            DateTime.UtcNow,
                            $"Обновление: жду завершения {running.Count} аккаунт(ов)");
                        var drainDone = await Task.WhenAny(running.Select(static j => j.Task))
                            .ConfigureAwait(false);
                        var drained = running.First(j => j.Task == drainDone);
                        running.Remove(drained);
                        try
                        {
                            await ApplyCompletedPassScheduleAsync(drained, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // ignore pass errors during drain for update
                        }

                        continue;
                    }

                    var parallelism = Math.Max(config.MaxConcurrentAccounts, 1);
                    if (parallelism != lastLoggedParallelism)
                    {
                        lastLoggedParallelism = parallelism;
                        WorkerMonitoringLogger.CycleParallelism(parallelism);
                    }

                    var now = DateTime.UtcNow;
                    var runningIds = running.Select(static j => j.Account.Id).ToHashSet();
                    if (Interlocked.Exchange(ref _immediatePassRequested, 0) == 1)
                    {
                        foreach (var account in accounts.Where(a => !runningIds.Contains(a.Id)))
                        {
                            // База хранит обычное расписание. В памяти сдвигаем только
                            // ближайший запуск, чтобы после прохода вернулась стандартная пауза.
                            _accountNextEligibleUtc[account.Id] = now;
                        }
                    }

                    var due = accounts
                        .Where(a => !runningIds.Contains(a.Id))
                        .Where(a => GetNextEligibleUtc(a) <= now)
                        .OrderBy(a => GetNextEligibleUtc(a))
                        .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    while (running.Count < parallelism && due.Count > 0)
                    {
                        var account = due[0];
                        due.RemoveAt(0);
                        var slot = launchSlot++;
                        running.Add(new AccountCycleJob(
                            account,
                            RunAccountInCycleSlotAsync(account, settings, config.WorkerId, slot, cancellationToken)));
                    }

                    activityReporter.ReportCycleProgress(
                        $"Активно {running.Count}/{parallelism} · аккаунтов {accounts.Count}");

                    if (running.Count == 0)
                    {
                        var nextDue = accounts
                            .Select(GetNextEligibleUtc)
                            .DefaultIfEmpty(now.AddSeconds(30))
                            .Min();
                        var wait = nextDue - DateTime.UtcNow;
                        if (wait < TimeSpan.FromSeconds(2))
                        {
                            wait = TimeSpan.FromSeconds(2);
                        }

                        var delay = wait > TimeSpan.FromSeconds(30)
                            ? TimeSpan.FromSeconds(30)
                            : wait;

                        activityReporter.ReportWaiting(nextDue, "Ожидание следующего цикла");
                        await _scheduleWake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var completedTask = await Task.WhenAny(running.Select(static j => j.Task))
                        .ConfigureAwait(false);
                    var job = running.First(j => j.Task == completedTask);
                    running.Remove(job);

                    await ApplyCompletedPassScheduleAsync(job, cancellationToken).ConfigureAwait(false);

                    _completedPassesSinceBrowserHousekeeping++;
                    if (_completedPassesSinceBrowserHousekeeping
                        >= Math.Max(1, MonitoringTiming.BrowserHousekeepingEveryNCycles)
                           * Math.Max(1, accounts.Count))
                    {
                        await MaybeHousekeepBrowsersAfterPassesAsync(config, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Смена календарного дня — тоже уборка.
                        var todayLocal = DateOnly.FromDateTime(DateTime.Now);
                        if (_lastBrowserHousekeepingLocalDate is not null
                            && todayLocal != _lastBrowserHousekeepingLocalDate.Value)
                        {
                            await MaybeHousekeepBrowsersAfterPassesAsync(config, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!await TryRecoverLoopAsync(ex, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync("Worker monitoring cancelled.", DeskLinkAuditLogLevel.Info);
        }
        finally
        {
            if (_cts?.IsCancellationRequested != true)
            {
                IsActive = false;
            }
        }
    }

    private DateTime GetNextEligibleUtc(AvitoAccount account)
    {
        DateTime? memoryNext = _accountNextEligibleUtc.TryGetValue(account.Id, out var at) ? at : null;
        var next = MonitoringAccountResume.ResolveNextEligibleUtc(
            memoryNext,
            account.NextMonitoringAtUtc,
            account.LastMonitoringAt);

        if (memoryNext is null
            && account.NextMonitoringAtUtc is { } persisted
            && persisted > DateTime.UtcNow)
        {
            _accountNextEligibleUtc[account.Id] = persisted;
            if (_loggedResumeRestored.TryAdd(account.Id, 0))
            {
                WorkerMonitoringLogger.AccountResumeRestored(account, persisted);
            }
        }

        return next;
    }

    private async Task ApplyCompletedPassScheduleAsync(
        AccountCycleJob job,
        CancellationToken cancellationToken)
    {
        var collectedCount = 0;
        var publishedCount = 0;
        var polled = false;
        var backlog = false;
        var passCompleted = false;
        TimeSpan? retryAfter = null;
        try
        {
            var outcome = await job.Task.ConfigureAwait(false);
            passCompleted = outcome.PassCompleted;
            retryAfter = outcome.RetryAfter;
            if (outcome.PolledSource)
            {
                polled = true;
                collectedCount = outcome.CollectedCount;
                publishedCount = outcome.PublishedCount;
                backlog = outcome.HasUndischargedBacklog;
            }
            else if (!string.IsNullOrWhiteSpace(outcome.NotPolledReason))
            {
                WorkerMonitoringLogger.AccountSkipped(job.Account, outcome.NotPolledReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WorkerMonitoringLogger.AccountFailed(job.Account, "проход", ex.Message);
        }

        _consecutiveMonitoringLoopFailures = 0;

        var quietStreak = _accountQuietStreak.GetValueOrDefault(job.Account.Id);
        if (polled && (collectedCount > 0 || backlog))
        {
            quietStreak = 0;
        }
        else if (polled)
        {
            quietStreak++;
        }

        _accountQuietStreak[job.Account.Id] = quietStreak;

        // Своя пауза только этому аккаунту (браузер уже закрыт в finally прохода).
        var historicalHeat = await repository
            .GetHistoricalResponseIngestHeatScoreAsync(DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        // CDP hang / Local API queue-HTTP timeout: RetryAfter=1 мин, ночной пол не применяется.
        var personalDelay = WorkerAccountPassDelay.Resolve(
            retryAfter,
            polled,
            collectedCount,
            quietStreak,
            backlog,
            historicalHeat,
            DateTime.UtcNow);

        var nextEligible = DateTime.UtcNow.Add(personalDelay);
        _accountNextEligibleUtc[job.Account.Id] = nextEligible;
        job.Account.NextMonitoringAtUtc = nextEligible;
        if (passCompleted)
        {
            var passStarted = job.Account.MonitoringPassStartedAtUtc;
            var passFinished = job.Account.MonitoringPassFinishedAtUtc;
            var next = job.Account.NextMonitoringAtUtc;
            MonitoringAccountResume.FinishPass(
                DateTime.UtcNow,
                nextEligible,
                ref passStarted,
                ref passFinished,
                ref next,
                job.Account.MonitoringPassCompletedSubIds);
            job.Account.MonitoringPassStartedAtUtc = passStarted;
            job.Account.MonitoringPassFinishedAtUtc = passFinished;
            job.Account.NextMonitoringAtUtc = next;
        }

        try
        {
            await repository.SaveAccountAsync(job.Account, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Пауза уже в памяти процесса; файл/рантайм — best effort.
        }

        var profileBusy = retryAfter is not null
            && WorkerAdsPowerPassRetry.IsLocalChromeProfileBusy(job.Account.LastErrorMessage);
        WorkerMonitoringLogger.AccountPersonalDelay(
            job.Account,
            personalDelay.TotalMinutes,
            collectedCount,
            publishedCount,
            polled,
            browserClosed: polled && !profileBusy,
            shortRetry: profileBusy);
    }

    private async Task<bool> TryRecoverLoopAsync(Exception ex, CancellationToken cancellationToken)
    {
        WorkerMonitoringLogger.CycleFailed(ex.Message);
        _ = GlobalLogger.Instance.LogAsync(
            $"Сбой цикла мониторинга: {ex}",
            DeskLinkAuditLogLevel.Error);
        activityReporter.ReportError($"Ошибка цикла: {ex.Message}");

        _consecutiveMonitoringLoopFailures++;
        if (MaxLoopRecoveryFailuresBeforeStop > 0
            && _consecutiveMonitoringLoopFailures > MaxLoopRecoveryFailuresBeforeStop)
        {
            IsActive = false;
            return false;
        }

        var pause = TimeSpan.FromMinutes(Math.Clamp(LoopRecoveryPauseMinutes, 1, 120));
        try
        {
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return true;
    }

    /// <summary>
    /// Обход аккаунтов с динамическим параллелизмом: лимит перечитывается из конфига перед стартом каждого нового аккаунта.
    /// Уже запущенные браузеры не обрываются при снижении лимита в панели.
    /// </summary>
    private async Task<(int CollectedCount, int AccountsPolled, bool HadBacklog, List<(string DisplayName, string Reason)> NotPolled)>
        ProcessAccountsInCycleAsync(
        IReadOnlyList<AvitoAccount> accounts,
        CancellationToken cancellationToken)
    {
        var collectedThisCycle = 0;
        var accountsPolled = 0;
        var hadUndischargedBacklog = false;
        var notPolled = new List<(string DisplayName, string Reason)>();
        var nextIndex = 0;
        var launchSlot = 0;
        var running = new List<AccountCycleJob>();
        var lastLoggedParallelism = -1;
        var parallelism = 1;

        while (nextIndex < accounts.Count || running.Count > 0)
        {
            var shuttingDown = cancellationToken.IsCancellationRequested;

            if (!shuttingDown)
            {
                configProvider.InvalidateConfigCache();
                var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
                var settings = ToAppSettings(config);
                parallelism = Math.Max(config.MaxConcurrentAccounts, 1);

                if (parallelism != lastLoggedParallelism)
                {
                    lastLoggedParallelism = parallelism;
                    WorkerMonitoringLogger.CycleParallelism(parallelism);
                }

                while (running.Count < parallelism && nextIndex < accounts.Count)
                {
                    var account = accounts[nextIndex++];
                    var slot = launchSlot++;
                    running.Add(new AccountCycleJob(
                        account,
                        RunAccountInCycleSlotAsync(account, settings, config.WorkerId, slot, cancellationToken)));
                }
            }

            if (running.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                break;
            }

            var completed = await Task.WhenAny(running.Select(static job => job.Task)).ConfigureAwait(false);
            var job = running.First(j => j.Task == completed);
            running.Remove(job);
            try
            {
                var outcome = await job.Task.ConfigureAwait(false);
                if (outcome.PolledSource)
                {
                    accountsPolled++;
                    collectedThisCycle += outcome.CollectedCount;
                    hadUndischargedBacklog |= outcome.HasUndischargedBacklog;
                }
                else if (!string.IsNullOrWhiteSpace(outcome.NotPolledReason))
                {
                    notPolled.Add((job.Account.DisplayName, outcome.NotPolledReason));
                }
            }
            catch (OperationCanceledException) when (shuttingDown)
            {
                // Аккаунт прерван при остановке мониторинга; браузер закрывается в finally ProcessAccountAsync.
            }

            if (!shuttingDown && parallelism == 1 && nextIndex < accounts.Count)
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(MonitoringTiming.DelayBetweenAccountsSeconds),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (collectedThisCycle, accountsPolled, hadUndischargedBacklog, notPolled);
    }

    private sealed record AccountCycleJob(AvitoAccount Account, Task<AccountCycleOutcome> Task);

    private sealed record AccountCycleOutcome(
        int CollectedCount,
        bool PolledSource,
        bool HasUndischargedBacklog,
        string? NotPolledReason = null,
        bool PassCompleted = false,
        TimeSpan? RetryAfter = null,
        int PublishedCount = 0);

    private async Task<AccountCycleOutcome> RunAccountInCycleSlotAsync(
        AvitoAccount account,
        AppSettings settings,
        Guid workerId,
        int launchSlot,
        CancellationToken cancellationToken)
    {
        if (launchSlot > 0)
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(launchSlot * MonitoringTiming.ParallelAccountLaunchStaggerMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _busyAccounts[account.Id] = 1;
        try
        {
            return await ProcessAccountAsync(account, settings, workerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busyAccounts.TryRemove(account.Id, out _);
            activityReporter.ReportAccountFinished(account.Id);
            await _telemetryPusher.PushNowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AccountCycleOutcome> ProcessAccountAsync(
        AvitoAccount account,
        AppSettings settings,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        // Персона аккаунта: стабильный множитель таймингов, чтобы аккаунты не были поведенческими близнецами.
        using var personaScope = AvitoPersona.Begin(account.Id.ToString());
        using var captchaRequestContext = CaptchaProviderRequestContext.Use(new CaptchaProviderRequestContextValue(
            WorkerId: workerId,
            AccountId: account.Id,
            CycleRunId: null,
            SubProfileRunId: null,
            SubProfileId: null,
            SubProfileName: null,
            Stage: CaptchaProviderRequestStages.Other,
            Reason: CaptchaProviderRequestReasons.FirewallDetected));
        if (!HasSupportedRuntime(account))
        {
            return new AccountCycleOutcome(0, false, false, "неподдерживаемый runtime профиля");
        }

        if (string.Equals(
                account.AvitoCredentialsError,
                WorkerAccountCredentialErrors.PasswordDecryptionFailed,
                StringComparison.Ordinal))
        {
            const string credentialsError =
                "Пароль Avito есть в БД, но API не смог расшифровать его текущим Data Protection key ring. " +
                "Перенесите volume с ключами со старого сервера или сохраните пароль Avito заново.";
            WorkerMonitoringLogger.AccountSkipped(account, credentialsError);
            activityReporter.ReportSkipped(
                account.Id,
                account.DisplayName,
                "Пропущен: ошибка расшифровки пароля Avito");
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Error",
                Message = "Не удалось расшифровать пароль Avito",
                Details = credentialsError
            }, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, false, false, credentialsError);
        }

        var accountSw = Stopwatch.StartNew();
        var staleStateCleared = AccountIssueTracker.TryClearStaleBlockingState(account);
        var staleErrorCleared = AccountIssueTracker.TryClearStaleAccountErrorMessage(account);
        if (staleStateCleared || staleErrorCleared)
        {
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            _telemetryPusher.RequestDebouncedPush(cancellationToken);
            WorkerMonitoringLogger.StaleStateCleared(account, staleStateCleared, staleErrorCleared);
            if (staleStateCleared)
            {
                await repository.AddLogAsync(new ProcessingLogItem
                {
                    AccountId = account.Id,
                    Level = "Info",
                    Message = "Повторный проход после устаревшей блокировки",
                    Details = $"Статус сброшен, интервал {MonitoringTiming.AccountBlockingIssueRetryAfterHours} ч."
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        if (account.Status is AvitoAccountStatus.RequiresLogin
            or AvitoAccountStatus.RequiresManualAction
            or AvitoAccountStatus.Paused)
        {
            AccountIssueTracker.RefreshAccountIssueMessage(account);
            var skipReason = AccountIssueTracker.FormatStatusHint(account);
            WorkerMonitoringLogger.AccountSkipped(account, skipReason);
            activityReporter.ReportSkipped(
                account.Id,
                account.DisplayName,
                $"Пропущен: {skipReason}");
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Аккаунт пропущен",
                Details = skipReason
            }, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, false, false, skipReason);
        }

        if (WorkerAccountRuntime.IsLocalProvider(account)
            && _localChromeLock.IsHeld(account.Id, LocalChromeAccountLock.Login))
        {
            const string loginSkipReason = "открыт браузер для ручного входа";
            WorkerMonitoringLogger.AccountSkipped(account, loginSkipReason);
            activityReporter.ReportSkipped(
                account.Id,
                account.DisplayName,
                $"Пропущен: {loginSkipReason}");
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Аккаунт пропущен",
                Details = loginSkipReason
            }, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, false, false, loginSkipReason);
        }

        var enabledSubProfiles = SubProfileEnabledFilter
            .GetEnabled(account.SubProfiles, account.DisabledSubProfileIds)
            .Count;
        WorkerMonitoringLogger.AccountStarted(account, enabledSubProfiles, account.SubProfiles.Count);

        activityReporter.ReportAccount(
            account.Id,
            account.DisplayName,
            "Открывает браузер и проверяет отклики");
        account.Status = AvitoAccountStatus.Monitoring;
        account.LastMonitoringAt = DateTime.UtcNow;
        await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);

        var cycleId = _cycleJournal.BeginCycle(account.Id, account.DisplayName);
        CaptchaProviderRequestContext.SetCycle(cycleId);
        var cycleTerminal = false;
        try
        {
            var (publishedCount, collectedCount, backlog, subProfilesProcessed, aborted) =
                await StreamProcessAccountResponsesAsync(
                    account, settings, cycleId, cancellationToken)
                .ConfigureAwait(false);

            if (account.Status == AvitoAccountStatus.Monitoring)
            {
                account.Status = AvitoAccountStatus.Authorized;
            }

            AccountIssueTracker.RefreshAccountIssueMessage(account);
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            WorkerMonitoringLogger.AccountFinished(
                account,
                collectedCount,
                publishedCount,
                accountSw.Elapsed.TotalSeconds,
                subProfilesProcessed);
            if (aborted)
            {
                _cycleJournal.AbortCycle(
                    cycleId,
                    errorType: subProfilesProcessed == 0 ? "cycle-start" : null,
                    errorMessage: subProfilesProcessed == 0
                        ? (string.IsNullOrWhiteSpace(account.LastErrorMessage)
                            ? "цикл прерван до первого субпрофиля"
                            : account.LastErrorMessage)
                        : null);
            }
            else
            {
                _cycleJournal.CompleteCycle(cycleId);
            }

            cycleTerminal = true;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(
                collectedCount,
                true,
                backlog,
                PassCompleted: !aborted,
                PublishedCount: publishedCount);
        }
        catch (AvitoCaptchaDetectedException captchaEx)
        {
            _cycleJournal.AbortCycle(cycleId, "captcha", captchaEx.Message);
            cycleTerminal = true;
            await HandleCaptchaForAccountAsync(account, captchaEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AvitoLoginRequiredException loginEx)
        {
            _cycleJournal.AbortCycle(cycleId, "auth-required", loginEx.Message);
            cycleTerminal = true;
            await HandleLoginRequiredForAccountAsync(account, loginEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerDailyOpenLimitExceededException limitEx)
        {
            _cycleJournal.AbortCycle(cycleId, "ads-power-limit", limitEx.Message);
            cycleTerminal = true;
            await HandleAdsPowerDailyOpenLimitForAccountAsync(account, limitEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerRateLimitExceededException rateEx)
        {
            _cycleJournal.AbortCycle(cycleId, "ads-power-rate", rateEx.Message);
            cycleTerminal = true;
            await HandleAdsPowerRateLimitForAccountAsync(account, rateEx, cancellationToken).ConfigureAwait(false);
            var reason = string.IsNullOrWhiteSpace(account.LastErrorMessage)
                ? AdsPowerStartupLogSanitizer.ExternalDetail(rateEx.ApiMessage, rateEx.Message)
                : account.LastErrorMessage;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, false, false, $"AdsPower rate limit: {reason}");
        }
        catch (AdsPowerProfileInUseException profileInUseEx)
        {
            _cycleJournal.AbortCycle(cycleId, "profile-in-use", profileInUseEx.Message);
            cycleTerminal = true;
            await HandleAdsPowerProfileInUseForAccountAsync(account, profileInUseEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerProxyFailureException proxyEx)
        {
            _cycleJournal.AbortCycle(cycleId, "proxy", proxyEx.UserMessage);
            cycleTerminal = true;
            await HandleAdsPowerProxyFailureForAccountAsync(account, proxyEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (SessionDiagnosticException diagnosticEx)
        {
            _cycleJournal.AbortCycle(cycleId, "session", diagnosticEx.Message);
            cycleTerminal = true;
            await HandleSessionDiagnosticForAccountAsync(account, diagnosticEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(
                0,
                true,
                false,
                RetryAfter: WorkerAdsPowerPassRetry.FromException(diagnosticEx));
        }
        catch (OperationCanceledException)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            try
            {
                await _cycleJournal.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }

            throw;
        }
        catch (Exception ex)
        {
            var retryAfter = WorkerAdsPowerPassRetry.FromException(ex);
            var profileBusy = WorkerAdsPowerPassRetry.IsLocalChromeProfileBusy(ex);
            account.LastErrorMessage = ex.Message;
            account.Status = retryAfter is null
                ? AvitoAccountStatus.Error
                : AvitoAccountStatus.Authorized;
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            if (retryAfter is null)
            {
                WorkerMonitoringLogger.AccountFailed(account, "мониторинг", ex.Message);
            }
            else
            {
                WorkerMonitoringLogger.AccountTransientFailure(account, "мониторинг", ex.Message);
            }

            var eventMessage = retryAfter is null
                ? $"Ошибка аккаунта {account.DisplayName}: {ex.Message}"
                : profileBusy
                    ? $"Профиль обычного браузера занят на аккаунте {account.DisplayName}, повтор через ~1 мин."
                    : $"AdsPower timeout на аккаунте {account.DisplayName}, браузер закрыт, повтор через ~1 мин: {ex.Message}";
            await PublishAccountEventAsync(
                account,
                WorkerAdsPowerPassRetry.EventType(ex),
                eventMessage,
                ex.Message,
                cancellationToken).ConfigureAwait(false);
            _cycleJournal.FailCycle(cycleId, "automation", ex.Message);
            cycleTerminal = true;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(
                0,
                PolledSource: !profileBusy,
                false,
                RetryAfter: retryAfter);
        }
        finally
        {
            if (!cycleTerminal)
            {
                _cycleJournal.AbortCycle(cycleId);
                try
                {
                    await _cycleJournal.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // best effort
                }
            }
        }
    }

    private async Task<(int PublishedCount, int CollectedCount, bool Backlog, int SubProfilesProcessed, bool Aborted)>
        StreamProcessAccountResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        Guid cycleId,
        CancellationToken cancellationToken)
    {
        var publishedTotal = 0;
        var collectedTotal = 0;
        var subProfilesProcessed = 0;
        var aborted = false;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        // Окно наблюдения (часов) после первой отправки — из конфига воркера, default 120 (5 суток).
        var phoneWatchHours = ResponsePhoneWatchRules.DefaultUnchangedHours;
        WorkerMonitoringConfig? liveConfig = null;
        try
        {
            liveConfig = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            phoneWatchHours = liveConfig.PhoneUnchangedHours;
        }
        catch
        {
            // оставляем default
        }

        IReadOnlyList<WorkerPendingChatMessageDto> pendingAll = [];
        try
        {
            pendingAll = await _outboundChat.GetPendingAsync(account.Id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pendingAll = [];
        }

        async Task<CandidateBatchPublishResult> ProcessBatchInlineAsync(
            IReadOnlyList<CandidateResponse> batch,
            IReadOnlyDictionary<string, IReadOnlyList<WorkerPendingChatMessageDto>>? pendingBySource = null)
        {
            if (batch.Count == 0)
            {
                return CandidateBatchPublishResult.Empty;
            }

            var readyCandidates = new List<CandidateResponse>();
            foreach (var response in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Дедуп внутри батча: по субпрофилю+ФИО (не SourceResponseId — он динамический).
                var nameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(response.FullName);
                var subKey = (response.AvitoSubProfileId ?? string.Empty).Trim();
                var key = !string.IsNullOrWhiteSpace(nameKey)
                    ? $"fio:{subKey}|{nameKey}"
                    : (string.IsNullOrWhiteSpace(response.SourceResponseId)
                        ? $"{response.PhoneRaw}|{response.FullName}|{response.Vacancy}"
                        : response.SourceResponseId);

                if (!seenKeys.Add(key))
                {
                    continue;
                }

                readyCandidates.Add(response);
            }

            if (readyCandidates.Count == 0)
            {
                return CandidateBatchPublishResult.Empty;
            }

            // phone-watch уже опубликован в Orbita под стабильным ключом sub+ФИО.
            // Не полагаемся на локальный phone-watch.db: после переустановки воркера
            // восстанавливаем состояние по данным Orbita и берём свежие записи первыми.
            var phoneWatchSourceIds = readyCandidates
                .Select(ResponsePhoneWatchOrbitaState.BuildSourceResponseId)
                .Where(static sourceId => !string.IsNullOrWhiteSpace(sourceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var storedPhoneWatches = await duplicateRepository
                .GetExistingSourceResponsesAsync(account.Id, phoneWatchSourceIds, cancellationToken)
                .ConfigureAwait(false);
            var storedPhoneWatchesBySourceId = storedPhoneWatches
                .Where(static x => x.SourceResponseId.StartsWith("phone-watch:", StringComparison.OrdinalIgnoreCase))
                .GroupBy(static x => x.SourceResponseId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.OrderByDescending(x => x.CollectedAt).First(),
                    StringComparer.OrdinalIgnoreCase);
            readyCandidates = ResponsePhoneWatchOrbitaState
                .OrderByAddedAt(readyCandidates, storedPhoneWatches)
                .ToList();

            // Уже известные в Орбите кандидаты (FIO/телефон) — не слать повторно как новый отклик.
            // Смену номера по phone-watch пропускаем отдельно (см. ниже).
            var profiles = readyCandidates
                .Select(response => new CandidateLookupProfileDto(
                    response.FullName,
                    response.Age,
                    response.City ?? string.Empty,
                    phoneNormalizer.Normalize(response.PhoneRaw) ?? string.Empty,
                    response.CreatedAt == default ? null : response.CreatedAt))
                .ToList();
            var matchedProfiles = await duplicateRepository
                .GetMatchedProfileIndicesAsync(profiles, account.Id, cancellationToken)
                .ConfigureAwait(false);

            var publishedCount = 0;
            var collectedCount = 0;
            var watchRefreshedCount = 0;
            var phoneChangedCount = 0;
            var skippedPersonDuplicates = 0;
            var skippedNoPhone = 0;
            var filteredAge = 0;
            var filteredGender = 0;
            var filteredResponseAge = 0;
            var filterSamples = new List<string>(5);
            var noPhoneSamples = new List<string>(5);
            var utcNow = DateTime.UtcNow;
            for (var i = 0; i < readyCandidates.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var candidate = readyCandidates[i];
                var phoneWatchSourceId = ResponsePhoneWatchOrbitaState.BuildSourceResponseId(candidate);
                storedPhoneWatchesBySourceId.TryGetValue(phoneWatchSourceId, out var storedPhoneWatch);
                if (storedPhoneWatch is not null)
                {
                    // Точный ключ существующей записи гарантирует обновление чата, а не новый отклик.
                    candidate.SourceResponseId = storedPhoneWatch.SourceResponseId;
                }
                var genderResolution = CandidateGenderResolver.Resolve(
                    candidate.FullName,
                    candidate.Gender,
                    candidate.RawText);
                candidate.Gender = CandidateGenderResolver.ToStoredGender(genderResolution);

                var alreadyInOrbitEarly = matchedProfiles.Contains(i);
                var hasPendingOutbound = pendingBySource is not null
                    && !string.IsNullOrWhiteSpace(candidate.SourceResponseId)
                    && pendingBySource.ContainsKey(candidate.SourceResponseId.Trim());
                if (alreadyInOrbitEarly
                    && hasPendingOutbound
                    && !string.IsNullOrWhiteSpace(candidate.ChatMessagesJson))
                {
                    candidate.OperationKind = WorkerCandidateOperationKinds.WatchRefresh;
                    await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                    watchRefreshedCount++;
                    continue;
                }

                var filterResult = ResponseCollectionFilter.Evaluate(
                    candidate.Age,
                    genderResolution.Gender is CandidateGenders.Unknown ? null : genderResolution.Gender,
                    settings.ResponseFilters);
                if (!filterResult.Pass)
                {
                    if (filterResult.RejectReason == ResponseCollectionFilterReasons.AgeAboveMax)
                    {
                        filteredAge++;
                    }
                    else if (filterResult.RejectReason is ResponseCollectionFilterReasons.GenderFemale
                             or ResponseCollectionFilterReasons.GenderMale)
                    {
                        filteredGender++;
                    }

                    if (filterSamples.Count < 5)
                    {
                        filterSamples.Add(
                            $"{candidate.FullName}|age={candidate.Age?.ToString() ?? "-"}|gender={genderResolution.Gender}|src={genderResolution.Source}|{filterResult.RejectReason}");
                    }

                    _ = GlobalLogger.Instance.LogAsync(
                        $"Response collection filter skipped «{candidate.FullName}»: {filterResult.RejectReason} (gender={genderResolution.Gender}, source={genderResolution.Source}, age={candidate.Age?.ToString() ?? "n/a"}).",
                        DeskLinkAuditLogLevel.Info,
                        memberName: nameof(StreamProcessAccountResponsesAsync));
                    continue;
                }

                var responseCreatedAt = candidate.CreatedAt == default
                    ? (candidate.CollectedAt == default ? DateTime.UtcNow : candidate.CollectedAt)
                    : candidate.CreatedAt;

                var phoneNormalized = phoneNormalizer.Normalize(candidate.PhoneRaw) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(phoneNormalized))
                {
                    // Карточка без раскрытого номера (маска/лимит бюджета/неудачный клик) —
                    // это НЕ дубль: считаем отдельно и логируем образцы, чтобы потери
                    // были видимы (остаётся в очереди на следующий проход).
                    skippedNoPhone++;
                    if (noPhoneSamples.Count < 5)
                    {
                        noPhoneSamples.Add($"{candidate.FullName}|created={responseCreatedAt:yyyy-MM-dd}");
                    }

                    continue;
                }

                var fullNameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(candidate.FullName);
                if (string.IsNullOrWhiteSpace(fullNameKey))
                {
                    skippedNoPhone++;
                    if (noPhoneSamples.Count < 5)
                    {
                        noPhoneSamples.Add($"(no-name)|created={responseCreatedAt:yyyy-MM-dd}");
                    }

                    continue;
                }

                var localObservation = await _phoneObservationStore
                    .GetAsync(candidate.AvitoSubProfileId, fullNameKey, cancellationToken)
                    .ConfigureAwait(false);
                var existingObs = ResponsePhoneWatchOrbitaState.RestoreObservation(
                        storedPhoneWatch,
                        candidate.AvitoSubProfileId,
                        fullNameKey,
                        phoneWatchHours,
                        utcNow)
                    ?? localObservation;
                var alreadyInOrbit = matchedProfiles.Contains(i) || storedPhoneWatch is not null;
                var watchingOpen = ResponsePhoneObservationWatch.IsOpen(existingObs);

                // Проверка давности отклика (пропускать старше N дней).
                // Открытое phone-watch — не режем: окно наблюдения (например 5 суток) может быть
                // длиннее фильтра «старше 3 дней», смена номера всё равно должна дойти.
                var ageFilterResult = ResponseCollectionFilter.EvaluateResponseAge(responseCreatedAt, settings.ResponseFilters);
                if (!ageFilterResult.Pass && !watchingOpen)
                {
                    filteredResponseAge++;
                    if (filterSamples.Count < 5)
                    {
                        filterSamples.Add(
                            $"{candidate.FullName}|created={responseCreatedAt:yyyy-MM-dd}|{ageFilterResult.RejectReason}");
                    }

                    _ = GlobalLogger.Instance.LogAsync(
                        $"Response collection filter skipped «{candidate.FullName}»: {ageFilterResult.RejectReason} (created={responseCreatedAt:yyyy-MM-dd}).",
                        DeskLinkAuditLogLevel.Info,
                        memberName: nameof(StreamProcessAccountResponsesAsync));
                    continue;
                }

                // Уже в Орбите и мы сами его не ведём в phone-watch — тихо игнор (без дублей в ленте).
                if (alreadyInOrbit && !watchingOpen)
                {
                    skippedPersonDuplicates++;
                    continue;
                }

                var decision = ResponsePhoneWatchEvaluator.Evaluate(
                    existingObs,
                    candidate.AvitoSubProfileId,
                    fullNameKey,
                    candidate.PhoneRaw,
                    phoneNormalized,
                    phoneWatchHours,
                    utcNow);

                // Skip: тот же номер в окне / окно закрыто / нет данных.
                // Пока phone-watch открыт — досылаем, если есть чат или поля карточки:
                // API обновит чат и незалоченные оператором поля.
                if (decision.Action == ResponsePhoneWatchAction.Skip)
                {
                    await _phoneObservationStore
                        .UpsertAsync(decision.NextObservation, cancellationToken)
                        .ConfigureAwait(false);

                    var watchPayloadChanged = ResponsePhoneWatchChatRefresh.HasPayloadChanged(
                            candidate,
                            storedPhoneWatch)
                        || ResponsePhoneWatchChatRefresh.HasResponseDateRefresh(candidate);
                    if (ResponsePhoneWatchChatRefresh.ShouldPublish(
                            watchingOpen,
                            decision.Action,
                            watchPayloadChanged ? candidate.ChatMessagesJson : null,
                            watchPayloadChanged))
                    {
                        ApplyPhoneWatchDecision(candidate, decision, phoneNormalized);
                        await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                        watchRefreshedCount++;
                        continue;
                    }

                    skippedPersonDuplicates++;
                    continue;
                }

                // Первая отправка: если кандидат уже в Орбите — не плодим duplicate-строки.
                // Не пишем observation как published (иначе «фантомная» публикация).
                if (decision.Action == ResponsePhoneWatchAction.PublishInitial && alreadyInOrbit)
                {
                    skippedPersonDuplicates++;
                    continue;
                }

                // PublishInitial (новый) или PublishPhoneChanged (тот же phone-watch id) — в Орбиту.
                await _phoneObservationStore
                    .UpsertAsync(decision.NextObservation, cancellationToken)
                    .ConfigureAwait(false);

                ApplyPhoneWatchDecision(candidate, decision, phoneNormalized);
                await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (decision.Action == ResponsePhoneWatchAction.PublishInitial)
                {
                    publishedCount++;
                    publishedTotal++;
                    collectedCount++;
                    collectedTotal++;
                }
                else if (decision.Action == ResponsePhoneWatchAction.PublishPhoneChanged)
                {
                    phoneChangedCount++;
                }
            }

            if (filteredAge > 0 || filteredGender > 0 || filteredResponseAge > 0)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Response filters for {account.DisplayName}: filtered_age={filteredAge}, filtered_gender={filteredGender}, filtered_response_age={filteredResponseAge}. Samples: {string.Join("; ", filterSamples)}",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(StreamProcessAccountResponsesAsync));
            }

            if (skippedNoPhone > 0)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Responses without revealed phone for {account.DisplayName}: count={skippedNoPhone} (deferred to next pass, not published). Samples: {string.Join("; ", noPhoneSamples)}",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(StreamProcessAccountResponsesAsync));
            }

            return new CandidateBatchPublishResult(
                readyCandidates.Count,
                publishedCount,
                DeferredByCycleLimit: 0,
                skippedPersonDuplicates,
                collectedCount,
                watchRefreshedCount,
                phoneChangedCount,
                SkippedNoPhoneCount: skippedNoPhone);
        }

        if (settings.DemoModeEnabled)
        {
            var demo = await avitoDemoResponseSource
                .GetBatchAsync(account, int.MaxValue, cancellationToken)
                .ConfigureAwait(false);
            var demoResult = await ProcessBatchInlineAsync(demo).ConfigureAwait(false);
            var demoRunId = _cycleJournal.BeginSubProfile(cycleId, "demo", "demo", 1, 1);
            _cycleJournal.CompleteSubProfile(
                cycleId,
                demoRunId,
                demo.Count,
                publishedTotal,
                collectedCount: demoResult.CollectedCount);
            return (publishedTotal, collectedTotal, false, 1, false);
        }

        var runtimeKind = WorkerAccountRuntime.Resolve(account);
        var useExternalBrowser = runtimeKind is WorkerAccountRuntimeKind.AdsPower
            or WorkerAccountRuntimeKind.Multilogin
            or WorkerAccountRuntimeKind.Local;

        if (!useExternalBrowser)
        {
            var responses = await avitoResponseSource
                .GetNewResponsesAsync(account, settings, cancellationToken)
                .ConfigureAwait(false);
            var legacyResult = await ProcessBatchInlineAsync(responses).ConfigureAwait(false);
            var legacyRunId = _cycleJournal.BeginSubProfile(cycleId, "legacy", "legacy", 1, 1);
            _cycleJournal.CompleteSubProfile(
                cycleId,
                legacyRunId,
                responses.Count,
                publishedTotal,
                collectedCount: legacyResult.CollectedCount);
            return (publishedTotal, collectedTotal, false, 1, false);
        }

        if (WorkerAccountRuntime.IsMultiloginProvider(account) && !WorkerAccountRuntime.IsMultilogin(account))
        {
            throw new InvalidOperationException(
                "Multilogin CDP: не заданы launcher URL, token, folder ID или profile ID.");
        }

        if (WorkerAccountRuntime.IsLocalProvider(account) && !WorkerAccountRuntime.IsLocal(account))
        {
            throw new InvalidOperationException(
                "Обычный браузер: не задан путь к отдельной папке профиля (User Data).");
        }

        var adsOptions = new AdsPowerConnectionOptions(
            string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl) ? string.Empty : account.AdsPowerApiBaseUrl,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        // Логический проход открываем для ЛЮБОГО аккаунта с внешним браузером,
        // включая аккаунты без субпрофилей: бюджет их действий тоже привязан к
        // незавершённому проходу. Иначе потраченные счётчики сохранялись бы вместе
        // с ЗАВЕРШЁННЫМ проходом, и transient-retry получал бы полный бюджет заново.
        BeginOrResumeAccountPass(account);
        if (account.SubProfiles.Count > 0)
        {
            var enabledBeforeBrowser = SubProfileEnabledFilter
                .GetEnabled(account.SubProfiles, account.DisabledSubProfileIds)
                .ToList();
            var remainingBeforeBrowser = MonitoringAccountResume.RemainingSubProfiles(
                enabledBeforeBrowser,
                static sub => sub.Id,
                account.MonitoringPassStartedAtUtc,
                account.MonitoringPassFinishedAtUtc,
                account.MonitoringPassCompletedSubIds);
            if (enabledBeforeBrowser.Count > 0 && remainingBeforeBrowser.Count == 0)
            {
                await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
                return (publishedTotal, collectedTotal, false, 0, false);
            }
        }

        var browserOpened = false;
        BrowserMonitorScreencastCapture? monitorScreencast = null;
        var monitorContext = new BrowserMonitorRuntimeContext();
        var loginCredentials = AvitoLoginCredentials.TryCreate(account.AvitoLogin, account.AvitoPassword);
        using var loginScope = AvitoAutoLoginContext.Use(loginCredentials);
        var loginAttempt = new AvitoAutoLoginAttempt();
        using var loginAttemptScope = AvitoAutoLoginContext.UseAttempt(loginAttempt);
        using var loginCaptchaScope = AvitoAutoLoginContext.UseSolver(
            _geeTestSolver is null
                ? null
                : async (page, ct) =>
                {
                    using (LocalChromeTrafficPolicy.AllowImages(page))
                    {
                        return await _geeTestSolver
                            .TrySolveOnPageAsync(
                                page,
                                html: null,
                                page.Url,
                                AvitoCaptchaTaskContext.Options,
                                ct)
                            .ConfigureAwait(false);
                    }
                });
        var captchaCounters = new AvitoCaptchaPassCounters();
        using var captchaTaskScope = AvitoCaptchaTaskContext.Use(
            GeeTestV4TaskOptions.FromBrowserProfile(
                account.AssignedUserAgent,
                account.ProxyType,
                account.ProxyAddress,
                account.ProxyUsername,
                account.ProxyPassword),
            captchaCounters);
        WorkerOpenedAccountSession? opened = null;
        // Общий бюджет действий на весь проход аккаунта: все субпрофили и повторные
        // попытки после восстановления страницы делят одни счётчики (см. AvitoAccountPassBudget).
        AvitoAccountPassBudget? passBudget = null;
        var localChromeLockHeld = false;
        try
        {
            if (runtimeKind == WorkerAccountRuntimeKind.Local)
            {
                if (!_localChromeLock.TryAcquire(account.Id, LocalChromeAccountLock.Monitoring, out var existing))
                {
                    throw new InvalidOperationException(
                        string.Equals(existing, LocalChromeAccountLock.Login, StringComparison.Ordinal)
                            ? "Обычный браузер уже открыт для ручного входа. Дождитесь закрытия окна."
                            : "Обычный браузер этого аккаунта уже запущен.");
                }

                localChromeLockHeld = true;
            }

            opened = await OpenAccountSessionWithDiagnosticsAsync(
                    account,
                    adsOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            var session = opened.Session;
            browserOpened = true;
            WorkerMonitoringLogger.BrowserOpened(account);

            browserMonitorSource.Register(
                account.Id,
                account.DisplayName,
                WorkerAccountRuntime.MonitorProfileId(account),
                async ct =>
                {
                    try
                    {
                        BrowserMonitorScreencastCapture? activeMonitorScreencast;
                        await _monitorScreencastGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            if (_captchaHold)
                            {
                                return null;
                            }

                            if (!_monitorScreencasts.TryGetValue(account.Id, out activeMonitorScreencast)
                                || !ReferenceEquals(activeMonitorScreencast, monitorScreencast))
                            {
                                monitorScreencast = await session
                                    .CreateMonitorScreencastCaptureAsync(ct)
                                    .ConfigureAwait(false);
                                _monitorScreencasts[account.Id] = monitorScreencast;
                                activeMonitorScreencast = monitorScreencast;
                            }
                        }
                        finally
                        {
                            _monitorScreencastGate.Release();
                        }

                        await activeMonitorScreencast
                            .WaitForFirstFrameAsync(TimeSpan.FromSeconds(4), ct)
                            .ConfigureAwait(false);

                        if (_captchaHold)
                        {
                            return null;
                        }

                        var bytes = activeMonitorScreencast.TryGetLatestJpeg();
                        if (bytes is null || bytes.Length == 0)
                        {
                            bytes = await session.CapturePageJpegScreenshotAsync(ct).ConfigureAwait(false);
                        }

                        if (bytes is null || bytes.Length == 0)
                        {
                            return null;
                        }

                        return new BrowserMonitorCapture(
                            bytes,
                            session.CurrentPageUrl,
                            monitorContext.SubProfileId,
                            monitorContext.SubProfileName);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        return null;
                    }
                });

            AvitoSubProfile? diagnosticSubProfile = null;
            try
            {
            if (ShouldRefreshSubProfiles(account))
            {
                await TryRefreshSubProfilesAsync(account, session, cancellationToken).ConfigureAwait(false);
            }

            var allSubProfiles = account.SubProfiles;
            if (passBudget is null)
            {
                // Бюджет привязан к логическому проходу: при возобновлении незавершённого
                // прохода (новая сессия того же прохода, рестарт воркера) израсходованные
                // счётчики восстанавливаются с аккаунта; новый проход — с чистыми.
                passBudget = AvitoAccountPassBudget.ForAccountPass(account, PersistAccountBestEffort);
            }

            if (allSubProfiles.Count == 0)
            {
                if (MonitoringTiming.CollectActiveAdsInWorkerPass && IsAdsStatsStale(account))
                {
                    var part = await CollectProfileItemsFromSessionAsync(account, session, cancellationToken)
                        .ConfigureAwait(false);
                    if (part.ParseSuccess)
                    {
                        await ApplyStatsSnapshotAsync(account, part, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await TryCaptureAccountBalanceAsync(account, session, cancellationToken).ConfigureAwait(false);
                    await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
                    _telemetryPusher.RequestDebouncedPush(cancellationToken);
                }

                var singleRunId = _cycleJournal.BeginSubProfile(cycleId, string.Empty, "—", 1, 1);
                var singlePending = GroupPendingOutbound(pendingAll, avitoSubProfileId: null);
                var singleOpenPhoneWatches = await duplicateRepository
                    .GetOpenPhoneWatchesAsync(account.Id, string.Empty, phoneWatchHours, cancellationToken)
                    .ConfigureAwait(false);
                var singleProfileHints = new CandidatesMessengerEnrichmentHints(
                    account.Id,
                    settings.DuplicateScope,
                    ResponseFilters: settings.ResponseFilters,
                    MessengerAutoReply: settings.Avito.MessengerAutoReply,
                    IsOpenPhoneWatchAsync: (fullName, ct) => IsOpenPhoneWatchForHintsAsync(
                        avitoSubProfileId: null,
                        fullName,
                        ct),
                    PendingBySourceResponseId: singlePending,
                    ClaimOutboundChatForDeliveryAsync: _outboundChat.ClaimForDeliveryAsync,
                    AckOutboundChatSentAsync: _outboundChat.AckSentAsync,
                    PhoneWatchHours: phoneWatchHours,
                    OpenPhoneWatches: singleOpenPhoneWatches);
                var rawJson = await session
                    .ExtractCandidatesJsonAsync(singleProfileHints, cancellationToken, passBudget)
                    .ConfigureAwait(false);
                var singleParse = await avitoResponseSource
                    .ParseCandidatesDetailedFromRawAsync(account, settings, rawJson, cancellationToken)
                    .ConfigureAwait(false);
                WorkerMonitoringLogger.ExtractionSummary(account, null, singleParse.Summary);
                var singleBatch = singleParse.Candidates;
                var singlePublishResult = await ProcessBatchInlineAsync(singleBatch, singlePending).ConfigureAwait(false);
                WorkerMonitoringLogger.ExtractionPublished(
                    account,
                    null,
                    singlePublishResult.PublishedCount,
                    singlePublishResult.ReadyCount,
                    singlePublishResult.DeferredByCycleLimit,
                    singlePublishResult.SkippedPersonDuplicates);
                var singleCaptcha = TakeCaptchaSnapshot(captchaCounters);
                _cycleJournal.CompleteSubProfile(
                    cycleId,
                    singleRunId,
                    singleParse.Summary.ParsedValidCount,
                    singlePublishResult.PublishedCount,
                    singlePublishResult.DeferredByCycleLimit,
                    singlePublishResult.SkippedPersonDuplicates,
                    singlePublishResult.CollectedCount,
                    singleCaptcha.Seen,
                    singleCaptcha.Solved,
                    loginAttempt.Attempted,
                    loginAttempt.Succeeded,
                    skippedNoPhoneCount: singleParse.Summary.MissingPhoneCount + singlePublishResult.SkippedNoPhoneCount);
                if (account.Status == AvitoAccountStatus.RequiresLogin
                    || account.Status == AvitoAccountStatus.RequiresManualAction)
                {
                    var issueMessage = string.IsNullOrWhiteSpace(account.LastErrorMessage)
                        ? $"Аккаунт «{account.DisplayName}» — требуется действие на странице откликов."
                        : account.LastErrorMessage;
                    var issueKind = account.Status == AvitoAccountStatus.RequiresManualAction
                        ? "subprofile-captcha"
                        : "subprofile-auth-required";
                    await PublishAccountDiagnosticFromSessionAsync(
                        account,
                        session,
                        issueKind,
                        issueMessage,
                        cancellationToken).ConfigureAwait(false);
                }

                return (publishedTotal, collectedTotal, false, 1, false);
            }

            var subProfiles = SubProfileEnabledFilter
                .GetEnabled(allSubProfiles, account.DisabledSubProfileIds)
                .ToList();
            BeginOrResumeAccountPass(account);
            var beforeResumeSkip = subProfiles.Count;
            subProfiles = MonitoringAccountResume.RemainingSubProfiles(
                subProfiles,
                static sub => sub.Id,
                account.MonitoringPassStartedAtUtc,
                account.MonitoringPassFinishedAtUtc,
                account.MonitoringPassCompletedSubIds);
            if (subProfiles.Count < beforeResumeSkip)
            {
                WorkerMonitoringLogger.AccountSkipped(
                    account,
                    $"повторный проход: уже собраны {beforeResumeSkip - subProfiles.Count} субпрофил(ей) в этом проходе, осталось {subProfiles.Count}");
            }

            AvitoHumanVariation.Shuffle(subProfiles);
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            var switchQueue = subProfiles
                .Select(static sub => (Sub: sub, Deferred: false))
                .ToList();
            if (beforeResumeSkip == 0)
            {
                var skipReason = allSubProfiles.Count > 0
                    && allSubProfiles.All(static sp => string.IsNullOrWhiteSpace(sp.Id))
                    ? "субпрофили без id — не удалось обновить список из Avito"
                    : "все субпрофили отключены в панели";
                WorkerMonitoringLogger.AccountSkipped(account, skipReason);
                activityReporter.ReportSkipped(
                    account.Id,
                    account.DisplayName,
                    "Все субпрофили отключены в панели");
                return (publishedTotal, collectedTotal, false, 0, true);
            }

            if (subProfiles.Count == 0)
            {
                return (publishedTotal, collectedTotal, false, 0, false);
            }

            var collectStats = MonitoringTiming.CollectActiveAdsInWorkerPass && IsAdsStatsStale(account);
            ProfileResult? statsAggregate = collectStats
                ? new ProfileResult { ParseSuccess = false, PageLoadedSuccessfully = true, ActiveTabCounterResolved = true }
                : null;
            var consecutiveCaptchaFails = 0;
            var lastStartedIndex = -1;
            string? remainingSkipReason = null;

            for (var i = 0; i < switchQueue.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    remainingSkipReason ??= FormatRemainingSkipReason("cancelled", subName: null);
                    break;
                }

                if (aborted && switchQueue[i].Deferred)
                {
                    break;
                }

                var sub = switchQueue[i].Sub;
                if (i > 0)
                {
                    loginAttempt.Reset();
                }
                var deferredRetry = switchQueue[i].Deferred;
                lastStartedIndex = i;
                diagnosticSubProfile = sub;
                monitorContext.SubProfileId = sub.Id;
                monitorContext.SubProfileName = sub.Name;
                var subRunId = _cycleJournal.BeginSubProfile(
                    cycleId,
                    sub.Id,
                    sub.Name,
                    i + 1,
                    switchQueue.Count);
                CaptchaProviderRequestContext.SetSubProfile(subRunId, sub.Id, sub.Name);
                CaptchaProviderRequestContext.SetStage(CaptchaProviderRequestStages.SubProfileSwitch, CaptchaProviderRequestReasons.AfterSubProfileSwitch);
                var subFoundCount = 0;
                var subPublishedCount = 0;
                var subCollectedCount = 0;
                try
                {
                    WorkerMonitoringLogger.SubProfileStep(
                        account,
                        sub,
                        i + 1,
                        subProfiles.Count,
                        "переключение субпрофиля");
                    activityReporter.ReportSubProfile(
                        account.Id,
                        account.DisplayName,
                        sub.Id,
                        sub.Name,
                        "Переключает субпрофиль");
                    var switched = await session.SwitchSubProfileAsync(sub.Id, cancellationToken).ConfigureAwait(false);
                    if (!switched.Ok)
                    {
                        if (switched.IsCaptcha)
                        {
                            throw new AvitoCaptchaDetectedException(
                                switched.IsIpBlock ? "firewall" : "captcha",
                                session.CurrentPageUrl,
                                html: null,
                                screenshotPng: null,
                                sub.Id,
                                sub.Name);
                        }

                        if (switched.IsLogin)
                        {
                            throw new AvitoLoginRequiredException(
                                session.CurrentPageUrl,
                                title: null,
                                screenshotPng: null,
                                sub.Id,
                                sub.Name);
                        }

                        var blocking = await HandleSubProfileSwitchFailureAsync(
                                account,
                                session,
                                sub,
                                cancellationToken).ConfigureAwait(false);
                        var skipCaptcha = TakeCaptchaSnapshot(captchaCounters);
                        _cycleJournal.FailSubProfile(
                            cycleId,
                            subRunId,
                            switched.JournalErrorType,
                            switched.JournalMessage(deferredRetry),
                            captchaCount: skipCaptcha.Seen,
                            captchaSolvedCount: skipCaptcha.Solved);
                        if (blocking)
                        {
                            WorkerMonitoringLogger.AccountBlockingStop(
                                account,
                                $"не удалось переключить субпрофиль «{sub.Name}»");
                            aborted = true;
                            remainingSkipReason = FormatRemainingSkipReason("switch-failed", sub.Name);
                            break;
                        }

                        if (switched.ShouldDeferRetry && !deferredRetry)
                        {
                            switchQueue.Add((sub, true));
                        }

                        continue;
                    }

                    decimal? historyPageBalance = null;
                    if (_topUpHistoryConfirmation is not null
                        && liveConfig?.PendingTopUpHistoryChecks.Any(x =>
                            x.AccountId == account.Id
                            && string.Equals(x.SubProfileId, sub.Id, StringComparison.Ordinal)) == true)
                    {
                        try
                        {
                            WorkerMonitoringLogger.SubProfileStep(
                                account,
                                sub,
                                i + 1,
                                subProfiles.Count,
                                "проверка истории операций кошелька");
                            var historyHtml = await session
                                .LoadWalletHistoryHtmlAsync(cancellationToken)
                                .ConfigureAwait(false);
                            if (!AvitoAdvanceTopUpHistoryParser.IsHistoryPage(historyHtml))
                            {
                                throw new InvalidOperationException(
                                    "Страница истории операций Avito не загрузилась.");
                            }

                            var operations = AvitoAdvanceTopUpHistoryParser
                                .Parse(historyHtml, DateTime.UtcNow);
                            var advanceBalance = AvitoBalanceParser
                                .ParseMoneySidebar(historyHtml)
                                ?.AdvanceBalance;
                            var confirmed = await _topUpHistoryConfirmation
                                .ConfirmAsync(
                                    liveConfig!.WorkerId,
                                    account.Id,
                                    sub.Id,
                                    operations,
                                    advanceBalance,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (confirmed > 0)
                            {
                                if (advanceBalance is decimal observed
                                    && (sub.Balance is null || observed > sub.Balance))
                                {
                                    sub.Balance = observed;
                                    historyPageBalance = observed;
                                }

                                _ = GlobalLogger.Instance.LogAsync(
                                    $"Аккаунт «{account.DisplayName}» · «{sub.Name}» — пополнение подтверждено историей операций Avito.",
                                    DeskLinkAuditLogLevel.Info);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _ = GlobalLogger.Instance.LogAsync(
                                $"Top-up history check failed for {account.DisplayName}/{sub.Name}: {ex.Message}",
                                DeskLinkAuditLogLevel.Warning);
                        }
                    }

                    if (historyPageBalance is null
                        && !AvitoHumanVariation.RollPermille(MonitoringTiming.SkipBalanceChancePermille))
                    {
                        await TryCaptureSubProfileBalanceAsync(sub, session, cancellationToken).ConfigureAwait(false);
                        await RefreshProfileAlertsAsync(account, session, sub, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    await PersistAccountSubProfilesAsync(account, allSubProfiles, cancellationToken)
                        .ConfigureAwait(false);
                    if (sub.Balance.HasValue || sub.Rating.HasValue || sub.ReviewsCount.HasValue)
                    {
                        WorkerMonitoringLogger.SubProfileTelemetrySaved(account, sub);
                    }

                    WorkerMonitoringLogger.SubProfileStep(
                        account,
                        sub,
                        i + 1,
                        subProfiles.Count,
                        "чтение откликов");
                    activityReporter.ReportSubProfile(
                        account.Id,
                        account.DisplayName,
                        sub.Id,
                        sub.Name,
                        "Читает отклики");
                    var pendingForSub = GroupPendingOutbound(pendingAll, sub.Id);
                    var openPhoneWatches = await duplicateRepository
                        .GetOpenPhoneWatchesAsync(account.Id, sub.Id, phoneWatchHours, cancellationToken)
                        .ConfigureAwait(false);
                    var messengerHints = new CandidatesMessengerEnrichmentHints(
                        account.Id,
                        settings.DuplicateScope,
                        sub.Id,
                        settings.ResponseFilters,
                        settings.Avito.MessengerAutoReply,
                        IsOpenPhoneWatchAsync: (fullName, ct) => IsOpenPhoneWatchForHintsAsync(
                            sub.Id,
                            fullName,
                            ct),
                        PendingBySourceResponseId: pendingForSub,
                        ClaimOutboundChatForDeliveryAsync: _outboundChat.ClaimForDeliveryAsync,
                        AckOutboundChatSentAsync: _outboundChat.AckSentAsync,
                        PhoneWatchHours: phoneWatchHours,
                        OpenPhoneWatches: openPhoneWatches);
                    var rawJson = await session
                        .ExtractCandidatesJsonAsync(messengerHints, cancellationToken, passBudget)
                        .ConfigureAwait(false);
                    var issueAtBefore = sub.LastIssueAt;
                    var parseResult = await avitoResponseSource
                        .ParseCandidatesDetailedFromRawAsync(account, settings, rawJson, cancellationToken, sub)
                        .ConfigureAwait(false);
                    WorkerMonitoringLogger.ExtractionSummary(account, sub, parseResult.Summary);
                    var batch = parseResult.Candidates;
                    if (sub.HasIssue && sub.LastIssueAt != issueAtBefore)
                    {
                        await PublishSubProfileIssueWithDiagnosticAsync(
                            account,
                            session,
                            sub,
                            sub.LastIssueKind,
                            sub.LastIssueMessage,
                            cancellationToken,
                            applyIssue: false).ConfigureAwait(false);
                    }

                    foreach (var r in batch)
                    {
                        r.AvitoSubProfileId = sub.Id;
                        r.AvitoSubProfileName = sub.Name;
                    }

                    var publishResult = await ProcessBatchInlineAsync(batch, pendingForSub).ConfigureAwait(false);
                    subFoundCount = parseResult.Summary.ParsedValidCount;
                    subPublishedCount = publishResult.PublishedCount;
                    subCollectedCount = publishResult.CollectedCount;
                    WorkerMonitoringLogger.ExtractionPublished(
                        account,
                        sub,
                        publishResult.PublishedCount,
                        publishResult.ReadyCount,
                        publishResult.DeferredByCycleLimit,
                        publishResult.SkippedPersonDuplicates);

                    var captcha = TakeCaptchaSnapshot(captchaCounters);
                    _cycleJournal.CompleteSubProfile(
                        cycleId,
                        subRunId,
                        parseResult.Summary.ParsedValidCount,
                        publishResult.PublishedCount,
                        publishResult.DeferredByCycleLimit,
                        publishResult.SkippedPersonDuplicates,
                        publishResult.CollectedCount,
                        captcha.Seen,
                        captcha.Solved,
                        loginAttempt.Attempted,
                        loginAttempt.Succeeded,
                        publishResult.WatchRefreshedCount,
                        publishResult.PhoneChangedCount,
                        parseResult.Summary.MissingPhoneCount + publishResult.SkippedNoPhoneCount);
                    subProfilesProcessed++;
                    consecutiveCaptchaFails = 0;
                    MonitoringAccountResume.MarkSubCompleted(account.MonitoringPassCompletedSubIds, sub.Id);
                    await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);

                    if (collectStats && statsAggregate is not null)
                    {
                        var part = await CollectProfileItemsFromSessionAsync(account, session, cancellationToken)
                            .ConfigureAwait(false);
                        await RefreshProfileAlertsAsync(account, session, sub, cancellationToken)
                            .ConfigureAwait(false);
                        if (part.ParseSuccess)
                        {
                            statsAggregate.ParseSuccess = true;
                            statsAggregate.ActiveAds.AddRange(part.ActiveAds);
                            statsAggregate.BlockedAds.AddRange(part.BlockedAds);
                            statsAggregate.UnpublishedAds.AddRange(part.UnpublishedAds);
                            statsAggregate.ActiveCount += part.ActiveCount;
                            statsAggregate.BlockedCount += part.BlockedCount;
                            statsAggregate.DraftsCount += part.DraftsCount;

                            part.ApplyMoneyTo(sub);
                            await PersistAccountSubProfilesAsync(account, allSubProfiles, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (AvitoCaptchaDetectedException captchaEx)
                {
                    var captcha = TakeCaptchaSnapshot(captchaCounters, unsolvedFallback: true);
                    var issueKind = AvitoSubProfileIssueKind.FromCaptchaKind(captchaEx.Kind);
                    _cycleJournal.FailSubProfile(
                        cycleId,
                        subRunId,
                        issueKind == AvitoSubProfileIssueKind.IpBlock ? "ip-block" : "captcha",
                        issueKind == AvitoSubProfileIssueKind.IpBlock ? "блок IP" : "капча",
                        subFoundCount,
                        subPublishedCount,
                        subCollectedCount,
                        captcha.Seen,
                        captcha.Solved,
                        loginAttempt.Attempted,
                        loginAttempt.Succeeded);
                    consecutiveCaptchaFails++;
                    await HandleCaptchaForAccountAsync(
                            account,
                            new AvitoCaptchaDetectedException(
                                captchaEx.Kind,
                                captchaEx.Url,
                                captchaEx.HtmlPreview,
                                captchaEx.ScreenshotPng,
                                sub.Id,
                                sub.Name),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (issueKind == AvitoSubProfileIssueKind.IpBlock
                        || MonitoringPassFailurePolicy.ShouldStopRemainingAfterCaptcha(consecutiveCaptchaFails))
                    {
                        aborted = true;
                        remainingSkipReason = issueKind == AvitoSubProfileIssueKind.IpBlock
                            ? FormatRemainingSkipReason("ip-block", sub.Name)
                            : consecutiveCaptchaFails >= MonitoringPassFailurePolicy.ConsecutiveCaptchaStopsRemaining
                                ? FormatRemainingSkipReason("captcha-consecutive", sub.Name)
                                : FormatRemainingSkipReason("captcha", sub.Name);
                        break;
                    }
                }
                catch (AvitoLoginRequiredException loginEx)
                {
                    var captcha = TakeCaptchaSnapshot(captchaCounters);
                    _cycleJournal.FailSubProfile(
                        cycleId,
                        subRunId,
                        "auth-required",
                        "нужен вход",
                        subFoundCount,
                        subPublishedCount,
                        subCollectedCount,
                        captcha.Seen,
                        captcha.Solved,
                        loginAttempt.Attempted,
                        loginAttempt.Succeeded);
                    aborted = true;
                    remainingSkipReason = FormatRemainingSkipReason("auth-required", sub.Name);
                    await HandleLoginRequiredForAccountAsync(
                            account,
                            new AvitoLoginRequiredException(
                                loginEx.Url,
                                loginEx.Title,
                                loginEx.ScreenshotPng,
                                sub.Id,
                                sub.Name,
                                loginEx.PasswordResetSmsPhone),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (AdsPowerProxyFailureException proxyEx)
                {
                    var captcha = TakeCaptchaSnapshot(captchaCounters);
                    _cycleJournal.FailSubProfile(
                        cycleId,
                        subRunId,
                        "proxy",
                        proxyEx.UserMessage,
                        subFoundCount,
                        subPublishedCount,
                        subCollectedCount,
                        captcha.Seen,
                        captcha.Solved);
                    aborted = true;
                    remainingSkipReason = FormatRemainingSkipReason("proxy", sub.Name, proxyEx.UserMessage);
                    await HandleAdsPowerProxyFailureForAccountAsync(account, proxyEx, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ShouldHandleAsSubProfileAutomationFailure(ex))
                {
                    var deferTransientCdpTimeout = ShouldDeferSubProfileRetry(ex, deferredRetry);
                    var blocking = await HandleSubProfileAutomationFailureAsync(
                        account,
                        session,
                        sub,
                        ex,
                        "сбор откликов",
                        cancellationToken).ConfigureAwait(false);
                    var captcha = TakeCaptchaSnapshot(captchaCounters);
                    _cycleJournal.FailSubProfile(
                        cycleId,
                        subRunId,
                        "automation",
                        ex.Message,
                        subFoundCount,
                        subPublishedCount,
                        subCollectedCount,
                        captcha.Seen,
                        captcha.Solved);
                    if (blocking)
                    {
                        WorkerMonitoringLogger.AccountBlockingStop(
                            account,
                            $"проблема на субпрофиле «{sub.Name}»");
                        aborted = true;
                        remainingSkipReason = FormatRemainingSkipReason("automation", sub.Name, ex.Message);
                        break;
                    }

                    if (deferTransientCdpTimeout)
                    {
                        switchQueue.Add((sub, true));
                    }
                }

                if (i < switchQueue.Count - 1 && !cancellationToken.IsCancellationRequested)
                {
                    await HumanDelay.BetweenSubProfilesAsync(cancellationToken).ConfigureAwait(false);
                    if (AvitoHumanVariation.RollPermille(MonitoringTiming.ExtraSubProfilePauseChancePermille))
                    {
                        await HumanDelay.DelayAsync(3000, 9000, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (aborted)
            {
                SkipRemainingSubProfiles(
                    cycleId,
                    switchQueue.Select(static item => item.Sub).ToList(),
                    lastStartedIndex,
                    remainingSkipReason);
            }

            await PersistAccountSubProfilesAsync(account, allSubProfiles, cancellationToken).ConfigureAwait(false);
            AccountIssueTracker.RefreshAccountIssueMessage(account);

            if (collectStats && statsAggregate is { ParseSuccess: true })
            {
                await ApplyStatsSnapshotAsync(account, statsAggregate, cancellationToken).ConfigureAwait(false);
            }

            return (publishedTotal, collectedTotal, false, subProfilesProcessed, aborted);
            }
            catch (Exception ex) when (ShouldAttachSessionDiagnostic(ex))
            {
                await ThrowWithSessionDiagnosticAsync(session, ex, diagnosticSubProfile, cancellationToken)
                    .ConfigureAwait(false);
                throw; // unreachable
            }
        }
        finally
        {
            if (passBudget is not null)
            {
                // Сводка нагрузки прохода: база для оценки «капч на 100 проходов»
                // против «действий на проход». Считаем и пустые, и оборванные проходы.
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker pass budget {account.DisplayName}: {passBudget.Describe()}.",
                    DeskLinkAuditLogLevel.Info,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "pass_budget_summary",
                        ["accountId"] = account.Id,
                        ["pass.phoneRevealClicksSpent"] = passBudget.PhoneRevealClicksSpent,
                        ["pass.phoneRevealClicksCap"] = passBudget.PhoneRevealClicksCap,
                        ["pass.autoRepliesSpent"] = passBudget.AutoRepliesSpent,
                        ["pass.autoRepliesCap"] = passBudget.AutoRepliesCap,
                        ["pass.sessionRestarts"] = passBudget.SessionRestarts,
                        ["pass.sessionRestartCap"] = passBudget.SessionRestartCap
                    });
            }

            if (browserOpened)
            {
                browserMonitorSource.Unregister(account.Id);
                if (monitorScreencast is not null)
                {
                    await _monitorScreencastGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (_monitorScreencasts.TryGetValue(account.Id, out var active)
                            && ReferenceEquals(active, monitorScreencast))
                        {
                            _monitorScreencasts.Remove(account.Id);
                        }
                    }
                    finally
                    {
                        _monitorScreencastGate.Release();
                    }

                    await monitorScreencast.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                if (browserOpened)
                {
                    WorkerMonitoringLogger.BrowserClosed(account);
                }
            }

            if (localChromeLockHeld)
            {
                _localChromeLock.Release(account.Id, LocalChromeAccountLock.Monitoring);
            }
        }
    }

    /// <summary>
    /// Начинает новый логический проход аккаунта или возобновляет незавершённый.
    /// При старте НОВОГО прохода сбрасывает и счётчики бюджета предыдущего прохода:
    /// <see cref="AvitoAccountPassBudget"/> восстанавливает их только для незавершённого
    /// прохода, а после <see cref="MonitoringAccountResume.BeginOrResumePass"/> отличить
    /// «только что начатый» проход от возобновляемого по timestamps уже невозможно.
    /// </summary>
    private static void BeginOrResumeAccountPass(AvitoAccount account)
    {
        var passStarted = account.MonitoringPassStartedAtUtc;
        var passFinished = account.MonitoringPassFinishedAtUtc;
        if (MonitoringAccountResume.BeginOrResumePass(
                DateTime.UtcNow,
                ref passStarted,
                ref passFinished,
                account.MonitoringPassCompletedSubIds))
        {
            account.MonitoringPassPhoneRevealClicksSpent = 0;
            account.MonitoringPassAutoRepliesSpent = 0;
            account.MonitoringPassSessionRestarts = 0;
        }

        account.MonitoringPassStartedAtUtc = passStarted;
        account.MonitoringPassFinishedAtUtc = passFinished;
    }

    /// <summary>
    /// Best-effort фиксация аккаунта при каждой мутации бюджета прохода: если процесс
    /// упадёт сразу после браузерного действия, списанный резерв уже записан в персистентность
    /// (runtime store / account-resume.json), и продолжение прохода не получит его повторно.
    /// </summary>
    private void PersistAccountBestEffort(AvitoAccount account)
    {
        try
        {
            _ = repository.SaveAccountAsync(account, CancellationToken.None);
        }
        catch
        {
            // Счётчики остаются в памяти процесса и уйдут с ближайшим обычным сохранением.
        }
    }

    private async Task<WorkerOpenedAccountSession> OpenAccountSessionWithDiagnosticsAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions adsOptions,
        CancellationToken cancellationToken)
    {
        var startupStopwatch = Stopwatch.StartNew();
        var lastStage = "ожидание запуска";
        var kind = WorkerAccountRuntime.Resolve(account);
        var startLabel = kind switch
        {
            WorkerAccountRuntimeKind.Multilogin => "Запуск Multilogin",
            WorkerAccountRuntimeKind.Local => "Запуск обычного браузера",
            _ => "Запуск AdsPower"
        };

        void ReportStage(string stage, TimeSpan elapsed)
        {
            lastStage = stage;
            activityReporter.ReportAccount(
                account.Id,
                account.DisplayName,
                $"{startLabel}: {stage} · {elapsed.TotalSeconds:F0} с");
        }

        try
        {
            return await _accountSessions
                .OpenAsync(account, adsOptions, ReportStage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var prefix = kind switch
            {
                WorkerAccountRuntimeKind.Multilogin => "Multilogin CDP не открыл сессию",
                WorkerAccountRuntimeKind.Local => "Обычный браузер не открыл сессию",
                _ => "AdsPower не открыл сессию"
            };
            throw new InvalidOperationException(
                $"{prefix}: последний этап «{lastStage}», прошло {startupStopwatch.Elapsed.TotalSeconds:F0} с. {ex.Message}",
                ex);
        }
    }

    private async Task PublishCandidateAsync(CandidateResponse response, CancellationToken cancellationToken)
    {
        var names = candidateParser.ParseName(response.FullName);
        response.FirstName = names.FirstName;
        response.LastName = names.LastName;
        response.MiddleName = names.MiddleName;
        response.PhoneNormalized = phoneNormalizer.Normalize(response.PhoneRaw);
        response.Status = ResponseStatus.InProgress;
        var now = DateTime.UtcNow;
        if (response.CollectedAt == default)
        {
            response.CollectedAt = now;
        }

        response.CreatedAt = response.CreatedAt == default ? response.CollectedAt : response.CreatedAt;

        await candidateSink.PublishAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static (int Seen, int Solved) TakeCaptchaSnapshot(
        AvitoCaptchaPassCounters counters,
        bool unsolvedFallback = false)
    {
        var (seen, solved) = counters.SnapshotAndReset();
        if (unsolvedFallback && seen == 0)
        {
            seen = 1;
        }

        return (seen, Math.Min(seen, solved));
    }

    private void SkipRemainingSubProfiles(
        Guid cycleId,
        IReadOnlyList<AvitoSubProfile> subProfiles,
        int lastStartedIndex,
        string? reason)
    {
        var skipReason = string.IsNullOrWhiteSpace(reason)
            ? FormatRemainingSkipReason("aborted", subName: null)
            : reason;
        for (var i = lastStartedIndex + 1; i < subProfiles.Count; i++)
        {
            var leftover = subProfiles[i];
            _cycleJournal.SkipSubProfile(
                cycleId,
                leftover.Id,
                leftover.Name,
                i + 1,
                subProfiles.Count,
                "not-reached",
                skipReason);
        }
    }

    private static string FormatRemainingSkipReason(string kind, string? subName, string? detail = null)
    {
        var who = string.IsNullOrWhiteSpace(subName) ? null : $"«{subName.Trim()}»";
        var trimmedDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        if (trimmedDetail is { Length: > 80 })
        {
            trimmedDetail = trimmedDetail[..80];
        }

        return kind switch
        {
            "captcha" => $"очередь не дошла: капча на {who}",
            "captcha-consecutive" => $"очередь не дошла: 2 капчи подряд, последняя на {who}",
            "ip-block" => $"очередь не дошла: блок IP на {who}",
            "auth-required" => $"очередь не дошла: нужен вход ({who})",
            "switch-failed" => $"очередь не дошла: не удалось переключить {who}",
            "proxy" => trimmedDetail is null
                ? $"очередь не дошла: прокси на {who}"
                : $"очередь не дошла: {trimmedDetail}",
            "automation" => trimmedDetail is null
                ? $"очередь не дошла: ошибка на {who}"
                : $"очередь не дошла: {trimmedDetail} ({who})",
            "cancelled" => "очередь не дошла: остановлен",
            _ => "очередь не дошла: цикл прерван"
        };
    }

    private Task<bool> IsOpenPhoneWatchForHintsAsync(
        string? avitoSubProfileId,
        string fullName,
        CancellationToken cancellationToken)
    {
        var nameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(fullName);
        if (string.IsNullOrWhiteSpace(nameKey))
        {
            return Task.FromResult(false);
        }

        return _phoneObservationStore.IsOpenWatchAsync(
            avitoSubProfileId ?? string.Empty,
            nameKey,
            cancellationToken);
    }

    /// <summary>
    /// Проставляет метрики номера и стабильный SourceResponseId (один отклик на sub+FIO).
    /// Avito ID динамический — не используем его как ключ идентичности.
    /// </summary>
    private static void ApplyPhoneWatchDecision(
        CandidateResponse candidate,
        ResponsePhoneWatchDecision decision,
        string phoneNormalized)
    {
        candidate.PhoneNormalized = phoneNormalized;
        var sourceId = decision.NextObservation.PublishedSourceResponseId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            sourceId = ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(
                candidate.AvitoSubProfileId,
                ResponsePhoneWatchEvaluator.BuildFullNameKey(candidate.FullName));
        }

        switch (decision.Action)
        {
            case ResponsePhoneWatchAction.PublishPhoneChanged:
                candidate.SourceResponseId = sourceId;
                candidate.OperationKind = WorkerCandidateOperationKinds.PhoneChanged;
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
                candidate.PreviousPhoneRaw = decision.PreviousPhoneRaw;
                candidate.PreviousPhoneNormalized = decision.PreviousPhoneNormalized;
                candidate.PhoneChangedAtUtc = decision.PhoneChangedAtUtc ?? DateTime.UtcNow;
                candidate.PhoneUnchangedHours = null;
                break;

            case ResponsePhoneWatchAction.PublishInitial:
                candidate.OperationKind = WorkerCandidateOperationKinds.NewResponse;
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.None;
                candidate.PreviousPhoneRaw = null;
                candidate.PreviousPhoneNormalized = null;
                candidate.PhoneUnchangedHours = null;
                candidate.PhoneChangedAtUtc = null;
                break;

            default:
                candidate.SourceResponseId = sourceId;
                candidate.OperationKind = WorkerCandidateOperationKinds.WatchRefresh;
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.None;
                candidate.PreviousPhoneRaw = null;
                candidate.PreviousPhoneNormalized = null;
                candidate.PhoneUnchangedHours = null;
                candidate.PhoneChangedAtUtc = null;
                break;
        }
    }

    private static bool IsAdsPowerAccount(AvitoAccount account) =>
        WorkerAccountRuntime.IsAdsPower(account);

    private static List<AvitoAccount> SelectRunnableAccounts(WorkerMonitoringConfig config) =>
        config.Accounts
            .Where(static account => account.IsEnabled)
            .Where(HasSupportedRuntime)
            .Where(account => WorkerAccountRuntime.IsBrowserProviderEnabled(
                account,
                config.AdsPowerEnabled,
                config.MultiloginEnabled,
                config.LocalChromeEnabled))
            .ToList();

    private static bool HasSupportedRuntime(AvitoAccount account) =>
        WorkerAccountRuntime.Resolve(account) is WorkerAccountRuntimeKind.AdsPower
            or WorkerAccountRuntimeKind.Multilogin
        || WorkerAccountRuntime.IsLocal(account);

    private static bool ShouldRefreshSubProfiles(AvitoAccount account)
    {
        if (account.ForceSubProfilesRefresh)
        {
            return true;
        }

        if (account.SubProfiles.Count == 0)
        {
            return true;
        }

        if (SubProfileEnabledFilter.GetEnabled(account.SubProfiles, account.DisabledSubProfileIds).Count == 0)
        {
            return true;
        }

        var last = account.SubProfilesRefreshedAt;
        if (last is null)
        {
            return true;
        }

        return DateTime.UtcNow - last.Value
            >= TimeSpan.FromHours(MonitoringTiming.SubProfilesRefreshIntervalHours);
    }

    private async Task TryRefreshSubProfilesAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await session.CaptureProfileSwitchHtmlAsync(cancellationToken).ConfigureAwait(false);
            var discovered = AvitoSubProfileRules.FilterValid(AvitoSubProfilesParser.Parse(html));
            if (discovered.Count > 0)
            {
                var merged = AvitoSubProfileMerger.Merge(account.SubProfiles, discovered);
                account.SetSubProfiles(merged);
                account.SubProfilesRefreshedAt = DateTime.UtcNow;
                account.ForceSubProfilesRefresh = false;
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker {account.DisplayName}: sub-profiles refreshed, count={merged.Count}.",
                    DeskLinkAuditLogLevel.Info);
            }
            else
            {
                var pageState = await session.GetPageStateAsync(cancellationToken).ConfigureAwait(false);
                var detail = pageState?.ProfileSwitchModalOpen == true
                    ? $"модалка открыта, но парсер не нашёл карточки ({pageState.ProfileCardsCount} в DOM)."
                    : "парсер не обнаружил субпрофили в HTML модалки.";
                var displayMessage = $"Субпрофили {account.DisplayName}: {detail}";
                var issueKind = pageState?.ProfileSwitchModalOpen == true
                    ? AvitoSubProfileIssueKind.SwitchFailed
                    : AvitoSubProfileIssueKind.ParseFailed;
                await PublishAccountDiagnosticFromSessionAsync(
                    account,
                    session,
                    issueKind,
                    displayMessage,
                    cancellationToken,
                    pageState,
                    "обновление субпрофилей").ConfigureAwait(false);
            }

            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            if (discovered.Count > 0)
            {
                await _telemetryPusher.PushNowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AvitoPageState? pageState = null;
            try
            {
                pageState = await session.GetPageStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }

            var message = AvitoAutomationFailureFormatter.Format("обновление субпрофилей", pageState, ex);
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker {account.DisplayName}: sub-profiles refresh failed: {message}",
                DeskLinkAuditLogLevel.Warning);
            var displayMessage = $"Субпрофили {account.DisplayName}: {message}";
            var issueKind = AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, ex);
            await PublishAccountDiagnosticFromSessionAsync(
                account,
                session,
                issueKind,
                displayMessage,
                cancellationToken,
                pageState,
                "обновление субпрофилей").ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WorkerPendingChatMessageDto>> GroupPendingOutbound(
        IReadOnlyList<WorkerPendingChatMessageDto> pending,
        string? avitoSubProfileId)
    {
        if (pending.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<WorkerPendingChatMessageDto>>(StringComparer.OrdinalIgnoreCase);
        }

        IEnumerable<WorkerPendingChatMessageDto> filtered = pending;
        if (!string.IsNullOrWhiteSpace(avitoSubProfileId))
        {
            filtered = pending.Where(item =>
                string.IsNullOrWhiteSpace(item.AvitoSubProfileId)
                || string.Equals(item.AvitoSubProfileId, avitoSubProfileId, StringComparison.Ordinal));
        }

        return filtered
            .GroupBy(item => item.SourceResponseId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                IReadOnlyList<WorkerPendingChatMessageDto> (group) => group.ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAdsStatsStale(AvitoAccount account)
    {
        var last = account.AdsStatsUpdatedAt;
        if (last is null)
        {
            return true;
        }

        return DateTime.UtcNow - last.Value >= TimeSpan.FromMinutes(MonitoringTiming.ActiveAdsRefreshIntervalMinutes);
    }

    private static AppSettings ToAppSettings(WorkerMonitoringConfig config) => new()
    {
        DemoModeEnabled = config.DemoModeEnabled,
        DuplicateScope = config.DuplicateScope,
        MonitoringSafety = new MonitoringSafetyOptions { MaxConcurrentAccounts = config.MaxConcurrentAccounts },
        ResponseFilters = config.ResponseFilters ?? ResponseCollectionFilters.Disabled,
        Avito = new AvitoSettings
        {
            MessengerAutoReply = config.MessengerAutoReply?.Clone()
                ?? new AvitoMessengerAutoReplySettings { Enabled = false }
        }
    };

    private async Task ApplyStatsSnapshotAsync(
        AvitoAccount account,
        ProfileResult snapshot,
        CancellationToken ct)
    {
        snapshot.ActiveAds ??= [];
        snapshot.BlockedAds ??= [];
        snapshot.UnpublishedAds ??= [];
        if (!snapshot.ParseSuccess)
        {
            return;
        }

        account.ActiveAdsCount = snapshot.ActiveAds.Count;
        account.BlockedCount = snapshot.BlockedAds.Count > 0 ? snapshot.BlockedAds.Count : snapshot.BlockedCount;
        account.DraftsCount = snapshot.DraftsCount;
        account.AdsStatsUpdatedAt = DateTime.UtcNow;
        account.ActiveAdsSnapshotJson = AvitoAdSnapshots.Serialize(snapshot.ActiveAds);
        account.BlockedAdsSnapshotJson = AvitoAdSnapshots.Serialize(snapshot.BlockedAds);
        account.UnpublishedAdsSnapshotJson = AvitoAdSnapshots.Serialize(snapshot.UnpublishedAds);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        _telemetryPusher.RequestDebouncedPush(ct);
    }

    private async Task<ProfileResult> CollectProfileItemsFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        CancellationToken cancellationToken)
    {
        var activeHtml = await session.LoadProfileItemsHtmlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeHtml))
        {
            return new ProfileResult { ParseSuccess = false, ParseFailureReason = "empty_active_items_html" };
        }

        var part = avitoParser.ParseProfilePage(activeHtml, account.Id);
        part.PageLoadedSuccessfully = true;
        AvitoBalanceParser.ParseMoneySidebar(activeHtml)?.ApplyTo(part);

        if (part.BlockedCount > 0)
        {
            try
            {
                var blockedHtml = await session.LoadBlockedItemsHtmlAsync(cancellationToken).ConfigureAwait(false);
                part.BlockedAds.AddRange(avitoParser.ParseBlockedTabPage(blockedHtml, account.Id));
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker: blocked tab (session) failed for {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        if (part.UnpublishedCount > 0)
        {
            try
            {
                var unpublishedHtml = await session.LoadUnpublishedItemsHtmlAsync(cancellationToken).ConfigureAwait(false);
                part.UnpublishedAds.AddRange(avitoParser.ParseUnpublishedTabPage(unpublishedHtml, account.Id));
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker: unpublished tab (session) failed for {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        return part;
    }

    private static async Task TryCaptureSubProfileBalanceAsync(
        AvitoSubProfile sub,
        IAdsPowerAccountSession session,
        CancellationToken cancellationToken)
    {
        var money = await session.TryReadMoneySidebarAsync(cancellationToken).ConfigureAwait(false);
        money?.ApplyTo(sub);
    }

    /// <summary>
    /// Фиксирует неблокирующие предупреждения Avito после чтения сайдбара страницы «Мои объявления».
    /// </summary>
    private async Task RefreshProfileAlertsAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        AvitoSubProfile sub,
        CancellationToken cancellationToken)
    {
        var state = await TryGetPageStateAsync(session, cancellationToken).ConfigureAwait(false);
        var kind = state?.HasInsufficientAdvance == true
            ? AvitoSubProfileIssueKind.InsufficientAdvance
            : state?.HasEmailConfirmationRequired == true
                ? AvitoSubProfileIssueKind.EmailConfirmationRequired
                : null;
        if (kind is null)
        {
            if (sub.LastIssueKind is AvitoSubProfileIssueKind.InsufficientAdvance
                or AvitoSubProfileIssueKind.EmailConfirmationRequired)
            {
                AccountIssueTracker.ClearSubProfileIssue(sub);
                AccountIssueTracker.RefreshAccountIssueMessage(account);
            }

            return;
        }

        if (string.Equals(sub.LastIssueKind, kind, StringComparison.Ordinal))
        {
            return;
        }

        var detail = AvitoAutomationFailureFormatter.Format("проверка объявлений", state);
        await PublishSubProfileIssueWithDiagnosticAsync(
                account,
                session,
                sub,
                kind,
                detail,
                cancellationToken,
                pageState: state,
                expectedStep: "проверка объявлений")
            .ConfigureAwait(false);
    }

    private async Task TryCaptureAccountBalanceAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        CancellationToken cancellationToken)
    {
        var money = await session.TryReadMoneySidebarAsync(cancellationToken).ConfigureAwait(false);
        if (money is null || !money.HasAnyData)
        {
            return;
        }

        var subs = account.SubProfiles.ToList();
        if (subs.Count == 0)
        {
            var created = new AvitoSubProfile
            {
                Id = account.Id.ToString("N"),
                Name = account.DisplayName,
                IsCurrent = true
            };
            money.ApplyTo(created);
            subs.Add(created);
            account.SetSubProfiles(subs);
            return;
        }

        var target = subs.FirstOrDefault(s => s.IsCurrent) ?? subs[0];
        money.ApplyTo(target);
        account.SetSubProfiles(subs);
    }

    /// <summary>
    /// Сохраняет субпрофили в локальную БД воркера сразу после обновления баланса/рейтинга,
    /// чтобы snapshot (раз в ~60 с) отдал данные на сайт без ожидания конца всего аккаунта.
    /// </summary>
    private async Task PersistAccountSubProfilesAsync(
        AvitoAccount account,
        IReadOnlyList<AvitoSubProfile> profiles,
        CancellationToken cancellationToken)
    {
        account.SetSubProfiles(profiles);
        await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
        _telemetryPusher.RequestDebouncedPush(cancellationToken);
    }

    private async Task<bool> TryCloseAdsPowerBrowserForAccountAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions options)
    {
        if (WorkerAccountRuntime.IsLocalProvider(account)
            || WorkerAccountRuntime.IsMultiloginProvider(account)
            || string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
        {
            return true;
        }

        try
        {
            await adsPowerAvitoAutomationService
                .CloseBrowserAsync(options, account.AdsPowerProfileId!, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker: failed to close AdsPower browser for {account.DisplayName}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
            return false;
        }
    }

    /// <summary>
    /// Orphan-уборка: вызывается после порога account-проходов или смены дня.
    /// Не трогаем hold капчи.
    /// </summary>
    private async Task MaybeHousekeepBrowsersAfterPassesAsync(
        WorkerMonitoringConfig config,
        CancellationToken cancellationToken)
    {
        if (_captchaHold)
        {
            WorkerMonitoringLogger.BrowserHousekeepingSkipped("активна сессия капчи");
            return;
        }

        var todayLocal = DateOnly.FromDateTime(DateTime.Now);
        var reason = _lastBrowserHousekeepingLocalDate is not null
                     && todayLocal != _lastBrowserHousekeepingLocalDate.Value
            ? "конец/смена дня"
            : $"каждые ~{MonitoringTiming.BrowserHousekeepingEveryNCycles}×N проходов";

        await CloseAllKnownAdsPowerBrowsersAsync(config.Accounts, reason, cancellationToken)
            .ConfigureAwait(false);
        _completedPassesSinceBrowserHousekeeping = 0;
    }

    private async Task CloseAllKnownAdsPowerBrowsersAsync(
        IReadOnlyList<AvitoAccount> accounts,
        string reason,
        CancellationToken cancellationToken,
        bool ignoreCancellation = false)
    {
        // Дедуп по profile id: один профиль — один browser/stop.
        var targets = accounts
            .Where(IsAdsPowerAccount)
            .GroupBy(static a => a.AdsPowerProfileId!, StringComparer.Ordinal)
            .Select(static g => g.First())
            .ToList();
        var localTargets = accounts
            .Where(WorkerAccountRuntime.IsLocal)
            .Where(account => !_localChromeLock.IsHeld(account.Id, LocalChromeAccountLock.Login))
            .GroupBy(static account => account.Id)
            .Select(static group => group.First())
            .ToList();

        if (targets.Count == 0 && localTargets.Count == 0)
        {
            _completedPassesSinceBrowserHousekeeping = 0;
            _lastBrowserHousekeepingLocalDate = DateOnly.FromDateTime(DateTime.Now);
            return;
        }

        if (targets.Count > 0)
        {
            WorkerMonitoringLogger.BrowserHousekeepingStarted(reason, targets.Count);
            var closedOk = 0;
            foreach (var account in targets)
            {
                if (!ignoreCancellation && cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var options = new AdsPowerConnectionOptions(
                    account.AdsPowerApiBaseUrl!,
                    string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);
                if (await TryCloseAdsPowerBrowserForAccountAsync(account, options).ConfigureAwait(false))
                {
                    closedOk++;
                }
            }

            WorkerMonitoringLogger.BrowserHousekeepingFinished(closedOk, targets.Count);
        }

        await ReclaimLocalChromeProfilesAsync(localTargets, reason, cancellationToken, ignoreCancellation)
            .ConfigureAwait(false);

        _completedPassesSinceBrowserHousekeeping = 0;
        _lastBrowserHousekeepingLocalDate = DateOnly.FromDateTime(DateTime.Now);
    }

    private async Task ReclaimLocalChromeProfilesAsync(
        IReadOnlyList<AvitoAccount> accounts,
        string reason,
        CancellationToken cancellationToken,
        bool ignoreCancellation)
    {
        if (_localChromeLauncher is null || accounts.Count == 0)
        {
            return;
        }

        WorkerMonitoringLogger.LocalChromeHousekeepingStarted(reason, accounts.Count);
        var closedOk = 0;
        foreach (var account in accounts)
        {
            if (!ignoreCancellation && cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var dir = LocalChromePaths.NormalizeUserDataDir(account.BrowserProfilePath, account.Id);
                var result = await _localChromeLauncher
                    .ReclaimAsync(dir, CancellationToken.None)
                    .ConfigureAwait(false);
                closedOk++;
                if (result.KilledProcessCount > 0)
                {
                    WorkerMonitoringLogger.LocalChromeReclaimed(account, result);
                }
            }
            catch
            {
                // Housekeeping must continue across accounts.
            }
        }

        WorkerMonitoringLogger.LocalChromeHousekeepingFinished(closedOk, accounts.Count);
    }

    private async Task HandleLoginRequiredForAccountAsync(
        AvitoAccount account,
        AvitoLoginRequiredException loginEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresLogin;
        var sub = FindSubProfile(account, loginEx.SubProfileId);
        var detail = loginEx.RequiresPasswordResetSms
            ? $"Avito сбросил пароль из-за защиты профиля. Автовход остановлен: получите SMS-код на {loginEx.PasswordResetSmsPhone}, установите новый пароль и войдите в браузере."
            : "требуется повторная авторизация в Avito — автовход не удался, откройте браузер AdsPower и войдите (телефон/почта и пароль).";
        account.LastErrorMessage = sub is not null
            ? AccountIssueFormatting.FormatIssue(account, sub, AvitoSubProfileIssueKind.AuthRequired, detail)
            : detail;
        if (sub is not null)
        {
            AccountIssueTracker.ApplySubProfileIssue(account, sub, AvitoSubProfileIssueKind.AuthRequired, detail);
        }

        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);

        var diagnostic = await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
            diagnosticsUploader,
            account.Id,
            AvitoSubProfileIssueKind.AuthRequired,
            account.LastErrorMessage,
            loginEx.Url,
            loginEx.ScreenshotPng,
            loginEx.SubProfileId,
            loginEx.SubProfileName,
            ct).ConfigureAwait(false);
        StoreSubProfileDiagnosticAttachment(account, sub, diagnostic.AttachmentId);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        WorkerMonitoringLogger.AccountFailed(account, "авторизация", account.LastErrorMessage);
        await PublishAccountEventAsync(
            account,
            "Warning",
            sub is not null
                ? account.LastErrorMessage
                : $"Требуется вход в Avito для {account.DisplayName}",
            diagnostic.Details,
            ct).ConfigureAwait(false);
    }

    private async Task HandleCaptchaForAccountAsync(
        AvitoAccount account,
        AvitoCaptchaDetectedException captchaEx,
        CancellationToken ct)
    {
        var sub = FindSubProfile(account, captchaEx.SubProfileId);
        var issueKind = AvitoSubProfileIssueKind.FromCaptchaKind(captchaEx.Kind);
        if (MonitoringPassFailurePolicy.AccountStatusForIssueKind(issueKind) is { } blockingStatus)
        {
            account.Status = blockingStatus;
        }
        else if (account.Status == AvitoAccountStatus.Monitoring)
        {
            account.Status = AvitoAccountStatus.Authorized;
        }

        account.LastErrorMessage = sub is not null
            ? AccountIssueFormatting.FormatIssue(
                account,
                sub,
                issueKind,
                issueKind == AvitoSubProfileIssueKind.IpBlock
                    ? "доступ ограничен: проблема с IP. Откройте браузер AdsPower и дождитесь разблокировки или смените IP."
                    : "нужна проверка на странице откликов.")
            : issueKind == AvitoSubProfileIssueKind.IpBlock
                ? $"Avito ограничил доступ из-за IP ({captchaEx.Kind}). Откройте браузер и дождитесь разблокировки или смените IP."
                : $"Avito показал капчу ({captchaEx.Kind}). Откройте браузер и пройдите проверку.";
        if (sub is not null)
        {
            AccountIssueTracker.ApplySubProfileIssue(
                account,
                sub,
                issueKind,
                issueKind == AvitoSubProfileIssueKind.IpBlock
                    ? "доступ ограничен: проблема с IP. Откройте браузер AdsPower и дождитесь разблокировки или смените IP."
                    : "нужна проверка на странице откликов.");
        }

        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);

        var diagnostic = await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
            diagnosticsUploader,
            account.Id,
            captchaEx.Kind,
            account.LastErrorMessage,
            captchaEx.Url,
            captchaEx.ScreenshotPng,
            captchaEx.SubProfileId,
            captchaEx.SubProfileName,
            ct).ConfigureAwait(false);
        StoreSubProfileDiagnosticAttachment(account, sub, diagnostic.AttachmentId);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        WorkerMonitoringLogger.AccountFailed(account, issueKind == AvitoSubProfileIssueKind.IpBlock ? "блок IP" : "капча", account.LastErrorMessage);
        await PublishAccountEventAsync(
            account,
            "Warning",
            sub is not null
                ? account.LastErrorMessage
                : issueKind == AvitoSubProfileIssueKind.IpBlock
                    ? $"Блок IP на аккаунте {account.DisplayName}"
                    : $"Капча на аккаунте {account.DisplayName}",
            diagnostic.Details,
            ct).ConfigureAwait(false);
    }

    private async Task HandleSessionDiagnosticForAccountAsync(
        AvitoAccount account,
        SessionDiagnosticException diagnosticEx,
        CancellationToken ct)
    {
        var inner = diagnosticEx.InnerException ?? diagnosticEx;
        var transientTimeout = WorkerAdsPowerPassRetry.FromException(diagnosticEx) is not null;
        var sub = FindSubProfile(account, diagnosticEx.SubProfileId);
        AvitoPageState? pageState = null;
        var formatted = AvitoAutomationFailureFormatter.Format(
            "мониторинг аккаунта",
            pageState,
            inner,
            inner is AvitoPageMismatchException mm ? mm.RecoveryAttempts : null);
        account.LastErrorMessage = sub is not null
            ? AccountIssueFormatting.FormatIssue(account, sub, diagnosticEx.DiagnosticKind, formatted)
            : formatted;
        // CDP / Local API timeout — переходный сбой сессии, не блокирующая Error.
        account.Status = transientTimeout ? AvitoAccountStatus.Authorized : AvitoAccountStatus.Error;
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        WorkerMonitoringLogger.AccountFailed(
            account,
            diagnosticEx.SubProfileName is not null ? $"субпрофиль «{diagnosticEx.SubProfileName}»" : "мониторинг",
            account.LastErrorMessage);

        var diagnostic = await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
            diagnosticsUploader,
            account.Id,
            diagnosticEx.DiagnosticKind,
            account.LastErrorMessage,
            diagnosticEx.PageUrl,
            diagnosticEx.ScreenshotPng,
            diagnosticEx.SubProfileId,
            diagnosticEx.SubProfileName,
            ct).ConfigureAwait(false);
        StoreSubProfileDiagnosticAttachment(account, sub, diagnostic.AttachmentId);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            transientTimeout ? "Warning" : "Error",
            transientTimeout
                ? $"AdsPower timeout на аккаунте {account.DisplayName}, браузер закрыт, повтор через ~1 мин: {account.LastErrorMessage}"
                : $"Ошибка аккаунта {account.DisplayName}: {account.LastErrorMessage}",
            diagnostic.Details,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerDailyOpenLimitForAccountAsync(
        AvitoAccount account,
        AdsPowerDailyOpenLimitExceededException limitEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresManualAction;
        account.LastErrorMessage = AdsPowerStartupLogSanitizer.ExternalDetail(limitEx.ApiMessage, limitEx.Message);
        WorkerMonitoringLogger.AccountFailed(account, "AdsPower", account.LastErrorMessage);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Лимит AdsPower для {account.DisplayName}",
            account.LastErrorMessage,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerRateLimitForAccountAsync(
        AvitoAccount account,
        AdsPowerRateLimitExceededException rateEx,
        CancellationToken ct)
    {
        account.LastErrorMessage = AdsPowerStartupLogSanitizer.ExternalDetail(rateEx.ApiMessage, rateEx.Message);
        WorkerMonitoringLogger.AccountFailed(account, "AdsPower rate limit", account.LastErrorMessage);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Rate limit AdsPower для {account.DisplayName}",
            account.LastErrorMessage,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerProfileInUseForAccountAsync(
        AvitoAccount account,
        AdsPowerProfileInUseException profileInUseEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresManualAction;
        var safeUserMessage = AdsPowerStartupLogSanitizer.LimitText(profileInUseEx.UserMessage);
        account.LastErrorMessage = AccountIssueFormatting.FormatIssue(
            account,
            null,
            AvitoSubProfileIssueKind.ProfileInUse,
            safeUserMessage);
        WorkerMonitoringLogger.AccountFailed(account, "AdsPower", safeUserMessage);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Профиль AdsPower занят для {account.DisplayName}",
            safeUserMessage,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerProxyFailureForAccountAsync(
        AvitoAccount account,
        AdsPowerProxyFailureException proxyEx,
        CancellationToken ct)
    {
        account.LastErrorMessage = AccountIssueFormatting.FormatIssue(
            account,
            null,
            AvitoSubProfileIssueKind.ProxyFailure,
            proxyEx.UserMessage);
        WorkerMonitoringLogger.AccountFailed(account, "AdsPower proxy", proxyEx.UserMessage);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Прокси AdsPower не работает для {account.DisplayName}",
            proxyEx.Message,
            ct).ConfigureAwait(false);
    }

    private Task PublishAccountEventAsync(
        AvitoAccount account,
        string level,
        string message,
        string? details,
        CancellationToken ct) =>
        eventSink.PublishAsync(account.Id, level, message, details, ct);

    private async Task PublishSubProfileIssueWithDiagnosticAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        AvitoSubProfile sub,
        string kind,
        string detail,
        CancellationToken ct,
        bool applyIssue = true,
        AvitoPageState? pageState = null,
        string? expectedStep = null)
    {
        if (applyIssue)
        {
            AccountIssueTracker.ApplySubProfileIssue(account, sub, kind, detail);
        }

        var message = AccountIssueFormatting.FormatIssue(account, sub, kind, detail);
        var diagnostic = await BuildDiagnosticEventDetailsFromSessionAsync(
            account,
            session,
            $"subprofile-{kind}",
            message,
            sub.Id,
            sub.Name,
            ct,
            pageState,
            expectedStep).ConfigureAwait(false);
        StoreSubProfileDiagnosticAttachment(account, sub, diagnostic.AttachmentId);
        await PublishAccountEventAsync(account, "Warning", message, diagnostic.Details, ct).ConfigureAwait(false);
    }

    private async Task PublishAccountDiagnosticFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string message,
        CancellationToken ct,
        AvitoPageState? pageState = null,
        string? expectedStep = null)
    {
        var diagnostic = await BuildDiagnosticEventDetailsFromSessionAsync(
            account,
            session,
            kind,
            message,
            null,
            null,
            ct,
            pageState,
            expectedStep).ConfigureAwait(false);
        await PublishAccountEventAsync(account, "Warning", message, diagnostic.Details, ct).ConfigureAwait(false);
    }

    private Task<WorkerDiagnosticEventDetails> BuildDiagnosticEventDetailsFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string text,
        string? subProfileId,
        string? subProfileName,
        CancellationToken ct,
        AvitoPageState? pageState = null,
        string? expectedStep = null) =>
        BuildDiagnosticEventDetailsFromSessionAsync(
            account,
            session,
            kind,
            text,
            subProfileId,
            subProfileName,
            session.CapturePageScreenshotAsync(ct),
            ct,
            pageState,
            expectedStep);

    private async Task<WorkerDiagnosticEventDetails> BuildDiagnosticEventDetailsFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string text,
        string? subProfileId,
        string? subProfileName,
        Task<byte[]?> screenshotTask,
        CancellationToken ct,
        AvitoPageState? pageState = null,
        string? expectedStep = null)
    {
        byte[]? screenshot = null;
        try
        {
            screenshot = await screenshotTask.ConfigureAwait(false);
        }
        catch
        {
            // Screenshot is best-effort diagnostics.
        }

        pageState ??= await TryGetPageStateAsync(session, ct).ConfigureAwait(false);

        return await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
            diagnosticsUploader,
            account.Id,
            kind,
            text,
            pageState?.Url ?? session.CurrentPageUrl,
            screenshot,
            subProfileId,
            subProfileName,
            ct,
            pageState,
            expectedStep).ConfigureAwait(false);
    }

    private static async Task<AvitoPageState?> TryGetPageStateAsync(
        IAdsPowerAccountSession session,
        CancellationToken ct)
    {
        try
        {
            return await session.GetPageStateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static AvitoSubProfile? FindSubProfile(AvitoAccount account, string? subProfileId)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return null;
        }

        return account.SubProfiles.FirstOrDefault(x => string.Equals(x.Id, subProfileId, StringComparison.Ordinal));
    }

    private static void StoreSubProfileDiagnosticAttachment(
        AvitoAccount account,
        AvitoSubProfile? sub,
        Guid? attachmentId)
    {
        if (sub is null || attachmentId is null)
        {
            return;
        }

        sub.LastDiagnosticAttachmentId = attachmentId;
        account.SetSubProfiles(account.SubProfiles.ToList());
    }

    private static bool ShouldAttachSessionDiagnostic(Exception ex) =>
        ex is not OperationCanceledException
        && ex is not AdsPowerRateLimitExceededException
        && ex is not AdsPowerDailyOpenLimitExceededException
        && ex is not AdsPowerProfileInUseException
        && ex is not AdsPowerProxyFailureException
        && ex is not SessionDiagnosticException
        && !ShouldHandleAsSubProfileAutomationFailure(ex);

    internal static bool ShouldHandleAsSubProfileAutomationFailure(Exception ex) =>
        ex is not AdsPowerProxyFailureException
        && (AdsPowerCdpGuard.IsCdpTimeout(ex)
            || ex is AvitoPageMismatchException
            or JsonException
            or PuppeteerException
            or InvalidOperationException);

    internal static bool ShouldDeferSubProfileRetry(Exception ex, bool deferredRetry) =>
        !deferredRetry && AdsPowerCdpGuard.IsCdpTimeout(ex);

    private async Task<bool> HandleSubProfileSwitchFailureAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        AvitoSubProfile sub,
        CancellationToken ct)
    {
        AvitoPageState? pageState = await TryGetPageStateAsync(session, ct).ConfigureAwait(false);
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, null);
        var detail = AvitoAutomationFailureFormatter.Format("переключение субпрофиля", pageState, null);
        WorkerMonitoringLogger.PageStateHint(account, sub, pageState);
        WorkerMonitoringLogger.SubProfileSwitchFailed(account, sub, detail);
        await PublishSubProfileIssueWithDiagnosticAsync(
            account,
            session,
            sub,
            kind,
            detail,
            ct,
            pageState: pageState,
            expectedStep: "переключение субпрофиля").ConfigureAwait(false);

        return AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind);
    }

    private async Task<bool> HandleSubProfileAutomationFailureAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        AvitoSubProfile sub,
        Exception ex,
        string expectedStep,
        CancellationToken ct)
    {
        AvitoPageState? pageState = null;
        var transientCdpTimeout = AdsPowerCdpGuard.IsCdpTimeout(ex);
        if (ex is AvitoPageMismatchException mismatch && mismatch.ActualState is not null)
        {
            pageState = mismatch.ActualState;
        }

        if (!transientCdpTimeout)
        {
            try
            {
                var liveState = await session.GetPageStateAsync(ct).ConfigureAwait(false);
                pageState = PreferPageState(pageState, liveState);
            }
            catch
            {
                // best effort
            }
        }

        var recoveryAttempts = ex is AvitoPageMismatchException mismatchEx
            ? mismatchEx.RecoveryAttempts
            : null;
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, ex);
        var detail = AvitoAutomationFailureFormatter.Format(expectedStep, pageState, ex, recoveryAttempts);
        var blocking = AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind);
        WorkerMonitoringLogger.PageStateHint(account, sub, pageState);
        WorkerMonitoringLogger.SubProfileIssue(account, sub, kind, detail, blocking);
        if (transientCdpTimeout)
        {
            await PublishSubProfileIssueWithoutSessionProbeAsync(
                    account,
                    session,
                    sub,
                    kind,
                    detail,
                    ct,
                    expectedStep)
                .ConfigureAwait(false);
        }
        else
        {
            await PublishSubProfileIssueWithDiagnosticAsync(
                    account,
                    session,
                    sub,
                    kind,
                    detail,
                    ct,
                    pageState: pageState,
                    expectedStep: expectedStep)
                .ConfigureAwait(false);
        }

        return blocking;
    }

    private async Task PublishSubProfileIssueWithoutSessionProbeAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        AvitoSubProfile sub,
        string kind,
        string detail,
        CancellationToken ct,
        string expectedStep)
    {
        AccountIssueTracker.ApplySubProfileIssue(account, sub, kind, detail);
        var message = AccountIssueFormatting.FormatIssue(account, sub, kind, detail);
        var diagnostic = await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
                diagnosticsUploader,
                account.Id,
                $"subprofile-{kind}",
                message,
                session.CurrentPageUrl,
                screenshotPng: null,
                sub.Id,
                sub.Name,
                ct,
                expectedStep: expectedStep)
            .ConfigureAwait(false);
        await PublishAccountEventAsync(account, "Warning", message, diagnostic.Details, ct).ConfigureAwait(false);
    }

    private static AvitoPageState? PreferPageState(AvitoPageState? primary, AvitoPageState? secondary)
    {
        if (primary is null)
        {
            return secondary;
        }

        if (secondary is null)
        {
            return primary;
        }

        if (AvitoAutomationFailureFormatter.SuggestsLogin(secondary)
            && !AvitoAutomationFailureFormatter.SuggestsLogin(primary))
        {
            return secondary;
        }

        if (primary.PageKind == AvitoPageKind.Unknown && secondary.PageKind != AvitoPageKind.Unknown)
        {
            return secondary;
        }

        return primary;
    }

    private static async Task ThrowWithSessionDiagnosticAsync(
        IAdsPowerAccountSession session,
        Exception ex,
        AvitoSubProfile? activeSubProfile,
        CancellationToken ct)
    {
        byte[]? screenshot = null;
        AvitoPageState? pageState = null;
        if (!AdsPowerCdpGuard.IsCdpTimeout(ex))
        {
            try
            {
                screenshot = await session.CapturePageScreenshotAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Screenshot is best-effort diagnostics.
            }

            try
            {
                pageState = await session.GetPageStateAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }

        var pageUrl = pageState?.Url ?? session.CurrentPageUrl;
        var subProfileId = activeSubProfile?.Id ?? (ex as AvitoCaptchaDetectedException)?.SubProfileId;
        var subProfileName = activeSubProfile?.Name ?? (ex as AvitoCaptchaDetectedException)?.SubProfileName;

        if (ex is AvitoCaptchaDetectedException captchaEx && captchaEx.ScreenshotPng is null && screenshot is not null)
        {
            throw new AvitoCaptchaDetectedException(
                captchaEx.Kind,
                captchaEx.Url ?? pageUrl,
                captchaEx.HtmlPreview,
                screenshot,
                subProfileId ?? captchaEx.SubProfileId,
                subProfileName ?? captchaEx.SubProfileName);
        }

        var kind = AdsPowerCdpGuard.IsCdpTimeout(ex)
            ? "cdp-timeout"
            : ex is AvitoCaptchaDetectedException captcha
                ? captcha.Kind
                : AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, ex);

        throw new SessionDiagnosticException(ex, kind, screenshot, pageUrl, subProfileId, subProfileName);
    }
}
