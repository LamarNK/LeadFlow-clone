using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Data;
using LeadFlow.Core.Services;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Headless AdsPower-only monitoring loop extracted from <c>MonitoringService</c> (no WebView2/Browser/Bitrix).
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
    IOutboundChatDispatch? outboundChatDispatch = null) : IWorkerMonitoringService
{
    private readonly IResponsePhoneObservationStore _phoneObservationStore =
        phoneObservationStore ?? new NullResponsePhoneObservationStore();
    private readonly IMonitoringCycleJournal _cycleJournal =
        monitoringCycleJournal ?? NullMonitoringCycleJournal.Instance;
    private readonly IOutboundChatDispatch _outboundChat =
        outboundChatDispatch ?? NullOutboundChatDispatch.Instance;

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

    public bool IsActive { get; private set; }
    public bool IsCaptchaHold => _captchaHold;

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
        _accountNextEligibleUtc.Clear();
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
                    var accounts = config.Accounts
                        .Where(static a => a.IsEnabled)
                        .Where(IsAdsPowerAccount)
                        .ToList();

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
                                await finishedEmpty.Task.ConfigureAwait(false);
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
                            await drained.Task.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // ignore pass errors during drain for update
                        }

                        // Не планируем nextEligible — после install процесс перезапустится.
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
                    var due = accounts
                        .Where(a => !runningIds.Contains(a.Id))
                        .Where(a => GetNextEligibleUtc(a.Id) <= now)
                        .OrderBy(a => GetNextEligibleUtc(a.Id))
                        .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    while (running.Count < parallelism && due.Count > 0)
                    {
                        var account = due[0];
                        due.RemoveAt(0);
                        var slot = launchSlot++;
                        running.Add(new AccountCycleJob(
                            account,
                            RunAccountInCycleSlotAsync(account, settings, slot, cancellationToken)));
                    }

                    activityReporter.ReportCycleProgress(
                        $"Активно {running.Count}/{parallelism} · аккаунтов {accounts.Count}");

                    if (running.Count == 0)
                    {
                        var nextDue = accounts
                            .Select(a => GetNextEligibleUtc(a.Id))
                            .DefaultIfEmpty(now.AddSeconds(30))
                            .Min();
                        var wait = nextDue - DateTime.UtcNow;
                        if (wait < TimeSpan.FromSeconds(2))
                        {
                            wait = TimeSpan.FromSeconds(2);
                        }

                        if (wait > TimeSpan.FromSeconds(30))
                        {
                            wait = TimeSpan.FromSeconds(30);
                        }

                        activityReporter.ReportWaiting(
                            DateTime.UtcNow.Add(wait),
                            $"Жду due-аккаунты (~{Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))} с)");
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var completedTask = await Task.WhenAny(running.Select(static j => j.Task))
                        .ConfigureAwait(false);
                    var job = running.First(j => j.Task == completedTask);
                    running.Remove(job);

                    var newResponses = 0;
                    var polled = false;
                    var backlog = false;
                    try
                    {
                        var outcome = await job.Task.ConfigureAwait(false);
                        if (outcome.PolledSource)
                        {
                            polled = true;
                            newResponses = outcome.NewResponses;
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
                    if (polled && (newResponses > 0 || backlog))
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
                    TimeSpan personalDelay;
                    if (polled)
                    {
                        personalDelay = MonitoringCycleDelay.GetDelayAfterCycle(
                            newResponses,
                            accountsPolled: 1,
                            quietStreak,
                            backlog,
                            historicalHeat);
                    }
                    else
                    {
                        // Skip: короткая пауза, не блокируем на max quiet.
                        personalDelay = TimeSpan.FromMinutes(MonitoringTiming.CycleDelayMinMinutes);
                    }

                    _accountNextEligibleUtc[job.Account.Id] = DateTime.UtcNow.Add(personalDelay);

                    WorkerMonitoringLogger.AccountPersonalDelay(
                        job.Account,
                        personalDelay.TotalMinutes,
                        newResponses,
                        polled);

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

    private DateTime GetNextEligibleUtc(Guid accountId) =>
        _accountNextEligibleUtc.TryGetValue(accountId, out var at) ? at : DateTime.MinValue;

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
    private async Task<(int NewResponses, int AccountsPolled, bool HadBacklog, List<(string DisplayName, string Reason)> NotPolled)>
        ProcessAccountsInCycleAsync(
        IReadOnlyList<AvitoAccount> accounts,
        CancellationToken cancellationToken)
    {
        var newResponsesThisCycle = 0;
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
                        RunAccountInCycleSlotAsync(account, settings, slot, cancellationToken)));
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
                    newResponsesThisCycle += outcome.NewResponses;
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
        return (newResponsesThisCycle, accountsPolled, hadUndischargedBacklog, notPolled);
    }

    private sealed record AccountCycleJob(AvitoAccount Account, Task<AccountCycleOutcome> Task);

    private sealed record AccountCycleOutcome(
        int NewResponses,
        bool PolledSource,
        bool HasUndischargedBacklog,
        string? NotPolledReason = null);

    private async Task<AccountCycleOutcome> RunAccountInCycleSlotAsync(
        AvitoAccount account,
        AppSettings settings,
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

        try
        {
            return await ProcessAccountAsync(account, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            activityReporter.ReportAccountFinished(account.Id);
            await _telemetryPusher.PushNowAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AccountCycleOutcome> ProcessAccountAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsAdsPowerAccount(account))
        {
            return new AccountCycleOutcome(0, false, false, "не AdsPower профиль");
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
        var cycleTerminal = false;
        try
        {
            var (detectedTotal, backlog, subProfilesProcessed, aborted) = await StreamProcessAccountResponsesAsync(
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
                detectedTotal,
                accountSw.Elapsed.TotalSeconds,
                subProfilesProcessed);
            if (aborted)
            {
                _cycleJournal.AbortCycle(cycleId);
            }
            else
            {
                _cycleJournal.CompleteCycle(cycleId);
            }

            cycleTerminal = true;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(detectedTotal, true, backlog);
        }
        catch (AvitoCaptchaDetectedException captchaEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleCaptchaForAccountAsync(account, captchaEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AvitoLoginRequiredException loginEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleLoginRequiredForAccountAsync(account, loginEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerDailyOpenLimitExceededException limitEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleAdsPowerDailyOpenLimitForAccountAsync(account, limitEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerRateLimitExceededException rateEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleAdsPowerRateLimitForAccountAsync(account, rateEx, cancellationToken).ConfigureAwait(false);
            var reason = string.IsNullOrWhiteSpace(account.LastErrorMessage)
                ? rateEx.ApiMessage ?? rateEx.Message
                : account.LastErrorMessage;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, false, false, $"AdsPower rate limit: {reason}");
        }
        catch (AdsPowerProfileInUseException profileInUseEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleAdsPowerProfileInUseForAccountAsync(account, profileInUseEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerProxyFailureException proxyEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleAdsPowerProxyFailureForAccountAsync(account, proxyEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (SessionDiagnosticException diagnosticEx)
        {
            _cycleJournal.AbortCycle(cycleId);
            cycleTerminal = true;
            await HandleSessionDiagnosticForAccountAsync(account, diagnosticEx, cancellationToken).ConfigureAwait(false);
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
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
            account.LastErrorMessage = ex.Message;
            account.Status = AvitoAccountStatus.Error;
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            WorkerMonitoringLogger.AccountFailed(account, "мониторинг", ex.Message);
            await PublishAccountEventAsync(
                account,
                "Error",
                $"Ошибка аккаунта {account.DisplayName}: {ex.Message}",
                ex.Message,
                cancellationToken).ConfigureAwait(false);
            _cycleJournal.FailCycle(cycleId);
            cycleTerminal = true;
            await _cycleJournal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
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

    private async Task<(int Detected, bool Backlog, int SubProfilesProcessed, bool Aborted)> StreamProcessAccountResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        Guid cycleId,
        CancellationToken cancellationToken)
    {
        var publishedTotal = 0;
        var subProfilesProcessed = 0;
        var aborted = false;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        // Окно наблюдения (часов) после первой отправки — из конфига воркера, default 120 (5 суток).
        var phoneWatchHours = ResponsePhoneWatchRules.DefaultUnchangedHours;
        try
        {
            var liveConfig = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
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
            var skippedPersonDuplicates = 0;
            var filteredAge = 0;
            var filteredGender = 0;
            var filteredResponseAge = 0;
            var filterSamples = new List<string>(5);
            var utcNow = DateTime.UtcNow;
            for (var i = 0; i < readyCandidates.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var candidate = readyCandidates[i];
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
                    await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                    publishedCount++;
                    publishedTotal++;
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

                var phoneNormalized = phoneNormalizer.Normalize(candidate.PhoneRaw) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(phoneNormalized))
                {
                    skippedPersonDuplicates++;
                    continue;
                }

                var fullNameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(candidate.FullName);
                if (string.IsNullOrWhiteSpace(fullNameKey))
                {
                    skippedPersonDuplicates++;
                    continue;
                }

                var existingObs = await _phoneObservationStore
                    .GetAsync(candidate.AvitoSubProfileId, fullNameKey, cancellationToken)
                    .ConfigureAwait(false);
                var alreadyInOrbit = matchedProfiles.Contains(i);
                var watchingOpen = ResponsePhoneObservationWatch.IsOpen(existingObs);

                // Проверка давности отклика (пропускать старше N дней).
                // Открытое phone-watch — не режем: окно наблюдения (например 5 суток) может быть
                // длиннее фильтра «старше 3 дней», смена номера всё равно должна дойти.
                var responseCreatedAt = candidate.CreatedAt == default
                    ? (candidate.CollectedAt == default ? DateTime.UtcNow : candidate.CollectedAt)
                    : candidate.CreatedAt;
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

                    if (ResponsePhoneWatchChatRefresh.ShouldPublish(
                            watchingOpen,
                            decision.Action,
                            candidate.ChatMessagesJson,
                            ResponsePhoneWatchChatRefresh.HasProfileRefresh(candidate)))
                    {
                        ApplyPhoneWatchDecision(candidate, decision, phoneNormalized);
                        await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                        publishedCount++;
                        publishedTotal++;
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
                publishedCount++;
                publishedTotal++;
            }

            if (filteredAge > 0 || filteredGender > 0 || filteredResponseAge > 0)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Response filters for {account.DisplayName}: filtered_age={filteredAge}, filtered_gender={filteredGender}, filtered_response_age={filteredResponseAge}. Samples: {string.Join("; ", filterSamples)}",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(StreamProcessAccountResponsesAsync));
            }

            return new CandidateBatchPublishResult(
                readyCandidates.Count,
                publishedCount,
                DeferredByCycleLimit: 0,
                skippedPersonDuplicates);
        }

        if (settings.DemoModeEnabled)
        {
            var demo = await avitoDemoResponseSource
                .GetBatchAsync(account, int.MaxValue, cancellationToken)
                .ConfigureAwait(false);
            await ProcessBatchInlineAsync(demo).ConfigureAwait(false);
            var demoRunId = _cycleJournal.BeginSubProfile(cycleId, "demo", "demo", 1, 1);
            _cycleJournal.CompleteSubProfile(cycleId, demoRunId, demo.Count, publishedTotal);
            return (publishedTotal, false, 1, false);
        }

        var hasAdsPowerCreds =
            !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
            && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

        if (!hasAdsPowerCreds)
        {
            var responses = await avitoResponseSource
                .GetNewResponsesAsync(account, settings, cancellationToken)
                .ConfigureAwait(false);
            await ProcessBatchInlineAsync(responses).ConfigureAwait(false);
            var legacyRunId = _cycleJournal.BeginSubProfile(cycleId, "legacy", "legacy", 1, 1);
            _cycleJournal.CompleteSubProfile(cycleId, legacyRunId, responses.Count, publishedTotal);
            return (publishedTotal, false, 1, false);
        }

        var adsOptions = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        var browserOpened = false;
        BrowserMonitorScreencastCapture? monitorScreencast = null;
        var monitorContext = new BrowserMonitorRuntimeContext();
        var loginCredentials = AvitoLoginCredentials.TryCreate(account.AvitoLogin, account.AvitoPassword);
        using var loginScope = AvitoAutoLoginContext.Use(loginCredentials);
        try
        {
            await using var session = await adsPowerAvitoAutomationService
                .OpenAccountSessionAsync(adsOptions, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(false);
            browserOpened = true;
            WorkerMonitoringLogger.BrowserOpened(account);

            browserMonitorSource.Register(
                account.Id,
                account.DisplayName,
                account.AdsPowerProfileId!,
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
                    AckOutboundChatSentAsync: _outboundChat.AckSentAsync);
                var rawJson = await session
                    .ExtractCandidatesJsonAsync(singleProfileHints, cancellationToken)
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
                    MonitoringTiming.MaxResponsesPerSubProfilePerCycle,
                    singlePublishResult.DeferredByCycleLimit,
                    singlePublishResult.SkippedPersonDuplicates);
                _cycleJournal.CompleteSubProfile(
                    cycleId,
                    singleRunId,
                    singleParse.Summary.ParsedValidCount,
                    singlePublishResult.PublishedCount,
                    singlePublishResult.DeferredByCycleLimit,
                    singlePublishResult.SkippedPersonDuplicates);
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

                return (publishedTotal, false, 1, false);
            }

            var subProfiles = SubProfileEnabledFilter.GetEnabled(allSubProfiles, account.DisabledSubProfileIds);
            if (subProfiles.Count == 0)
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
                return (publishedTotal, false, 0, true);
            }

            var collectStats = MonitoringTiming.CollectActiveAdsInWorkerPass && IsAdsStatsStale(account);
            ProfileResult? statsAggregate = collectStats
                ? new ProfileResult { ParseSuccess = false, PageLoadedSuccessfully = true, ActiveTabCounterResolved = true }
                : null;

            for (var i = 0; i < subProfiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                var sub = subProfiles[i];
                diagnosticSubProfile = sub;
                monitorContext.SubProfileId = sub.Id;
                monitorContext.SubProfileName = sub.Name;
                var subRunId = _cycleJournal.BeginSubProfile(
                    cycleId,
                    sub.Id,
                    sub.Name,
                    i + 1,
                    subProfiles.Count);
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
                    if (!switched)
                    {
                        if (await HandleSubProfileSwitchFailureAsync(
                                account,
                                session,
                                sub,
                                cancellationToken).ConfigureAwait(false))
                        {
                            WorkerMonitoringLogger.AccountBlockingStop(
                                account,
                                $"не удалось переключить субпрофиль «{sub.Name}»");
                            _cycleJournal.FailSubProfile(
                                cycleId,
                                subRunId,
                                "switch-failed",
                                "не удалось переключить субпрофиль");
                            aborted = true;
                            break;
                        }

                        _cycleJournal.FailSubProfile(
                            cycleId,
                            subRunId,
                            "switch-failed",
                            "не удалось переключить субпрофиль");
                        continue;
                    }

                    await TryCaptureSubProfileBalanceAsync(sub, session, cancellationToken).ConfigureAwait(false);
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
                        AckOutboundChatSentAsync: _outboundChat.AckSentAsync);
                    var rawJson = await session
                        .ExtractCandidatesJsonAsync(messengerHints, cancellationToken)
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
                    WorkerMonitoringLogger.ExtractionPublished(
                        account,
                        sub,
                        publishResult.PublishedCount,
                        publishResult.ReadyCount,
                        MonitoringTiming.MaxResponsesPerSubProfilePerCycle,
                        publishResult.DeferredByCycleLimit,
                        publishResult.SkippedPersonDuplicates);

                    _cycleJournal.CompleteSubProfile(
                        cycleId,
                        subRunId,
                        parseResult.Summary.ParsedValidCount,
                        publishResult.PublishedCount,
                        publishResult.DeferredByCycleLimit,
                        publishResult.SkippedPersonDuplicates);
                    subProfilesProcessed++;

                    if (collectStats && statsAggregate is not null)
                    {
                        var part = await CollectProfileItemsFromSessionAsync(account, session, cancellationToken)
                            .ConfigureAwait(false);
                        if (part.ParseSuccess)
                        {
                            statsAggregate.ParseSuccess = true;
                            statsAggregate.ActiveAds.AddRange(part.ActiveAds);
                            statsAggregate.BlockedAds.AddRange(part.BlockedAds);
                            statsAggregate.ActiveCount += part.ActiveCount;
                            statsAggregate.BlockedCount += part.BlockedCount;
                            statsAggregate.DraftsCount += part.DraftsCount;

                            part.ApplyMoneyTo(sub);
                            await PersistAccountSubProfilesAsync(account, allSubProfiles, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (AvitoCaptchaDetectedException)
                {
                    _cycleJournal.FailSubProfile(cycleId, subRunId, "captcha", "капча");
                    aborted = true;
                    throw;
                }
                catch (AvitoLoginRequiredException loginEx)
                {
                    _cycleJournal.FailSubProfile(cycleId, subRunId, "auth-required", "нужен вход");
                    aborted = true;
                    throw new AvitoLoginRequiredException(
                        loginEx.Url,
                        loginEx.Title,
                        loginEx.ScreenshotPng,
                        sub.Id,
                        sub.Name);
                }
                catch (Exception ex) when (ShouldHandleAsSubProfileAutomationFailure(ex))
                {
                    var blocking = await HandleSubProfileAutomationFailureAsync(
                        account,
                        session,
                        sub,
                        ex,
                        "сбор откликов",
                        cancellationToken).ConfigureAwait(false);
                    _cycleJournal.FailSubProfile(
                        cycleId,
                        subRunId,
                        "automation",
                        ex.Message);
                    if (blocking)
                    {
                        WorkerMonitoringLogger.AccountBlockingStop(
                            account,
                            $"проблема на субпрофиле «{sub.Name}»");
                        aborted = true;
                        break;
                    }
                }

                if (i < subProfiles.Count - 1 && !cancellationToken.IsCancellationRequested)
                {
                    await HumanDelay.BetweenSubProfilesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await PersistAccountSubProfilesAsync(account, allSubProfiles, cancellationToken).ConfigureAwait(false);
            AccountIssueTracker.RefreshAccountIssueMessage(account);

            if (collectStats && statsAggregate is { ParseSuccess: true })
            {
                await ApplyStatsSnapshotAsync(account, statsAggregate, cancellationToken).ConfigureAwait(false);
            }

            return (publishedTotal, false, subProfilesProcessed, aborted);
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

                var closed = await TryCloseAdsPowerBrowserForAccountAsync(account, adsOptions)
                    .ConfigureAwait(false);
                if (closed)
                {
                    WorkerMonitoringLogger.BrowserClosed(account);
                }
            }
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

        candidate.SourceResponseId = sourceId;

        switch (decision.Action)
        {
            case ResponsePhoneWatchAction.PublishPhoneChanged:
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
                candidate.PreviousPhoneRaw = decision.PreviousPhoneRaw;
                candidate.PreviousPhoneNormalized = decision.PreviousPhoneNormalized;
                candidate.PhoneChangedAtUtc = decision.PhoneChangedAtUtc ?? DateTime.UtcNow;
                candidate.PhoneUnchangedHours = null;
                break;

            case ResponsePhoneWatchAction.PublishInitial:
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.None;
                candidate.PreviousPhoneRaw = null;
                candidate.PreviousPhoneNormalized = null;
                candidate.PhoneUnchangedHours = null;
                candidate.PhoneChangedAtUtc = null;
                break;

            default:
                candidate.PhoneMetricKind = ResponsePhoneMetricKinds.None;
                candidate.PreviousPhoneRaw = null;
                candidate.PreviousPhoneNormalized = null;
                candidate.PhoneUnchangedHours = null;
                candidate.PhoneChangedAtUtc = null;
                break;
        }
    }

    private static bool IsAdsPowerAccount(AvitoAccount account) =>
        account.ProfileProvider == AvitoProfileProvider.AdsPower
        && !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
        && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

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
        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
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

        if (targets.Count == 0)
        {
            _completedPassesSinceBrowserHousekeeping = 0;
            _lastBrowserHousekeepingLocalDate = DateOnly.FromDateTime(DateTime.Now);
            return;
        }

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
        _completedPassesSinceBrowserHousekeeping = 0;
        _lastBrowserHousekeepingLocalDate = DateOnly.FromDateTime(DateTime.Now);
    }

    private async Task HandleLoginRequiredForAccountAsync(
        AvitoAccount account,
        AvitoLoginRequiredException loginEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresLogin;
        var sub = FindSubProfile(account, loginEx.SubProfileId);
        var detail = "требуется повторная авторизация в Avito — автовход не удался, откройте браузер AdsPower и войдите (телефон/почта и пароль).";
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
        account.Status = AvitoAccountStatus.RequiresManualAction;
        var sub = FindSubProfile(account, captchaEx.SubProfileId);
        account.LastErrorMessage = sub is not null
            ? AccountIssueFormatting.FormatIssue(
                account,
                sub,
                AvitoSubProfileIssueKind.Captcha,
                "нужна проверка на странице откликов.")
            : $"Avito captcha/firewall ({captchaEx.Kind}). Откройте браузер и пройдите проверку.";
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
        WorkerMonitoringLogger.AccountFailed(account, "капча / IP", account.LastErrorMessage);
        await PublishAccountEventAsync(
            account,
            "Warning",
            sub is not null
                ? account.LastErrorMessage
                : $"Капча/firewall на аккаунте {account.DisplayName}",
            diagnostic.Details,
            ct).ConfigureAwait(false);
    }

    private async Task HandleSessionDiagnosticForAccountAsync(
        AvitoAccount account,
        SessionDiagnosticException diagnosticEx,
        CancellationToken ct)
    {
        var inner = diagnosticEx.InnerException ?? diagnosticEx;
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
        account.Status = AvitoAccountStatus.Error;
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
            "Error",
            $"Ошибка аккаунта {account.DisplayName}: {account.LastErrorMessage}",
            diagnostic.Details,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerDailyOpenLimitForAccountAsync(
        AvitoAccount account,
        AdsPowerDailyOpenLimitExceededException limitEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresManualAction;
        account.LastErrorMessage = limitEx.ApiMessage ?? limitEx.Message;
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
        account.LastErrorMessage = rateEx.ApiMessage ?? rateEx.Message;
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
        account.LastErrorMessage = AccountIssueFormatting.FormatIssue(
            account,
            null,
            AvitoSubProfileIssueKind.ProfileInUse,
            profileInUseEx.UserMessage);
        WorkerMonitoringLogger.AccountFailed(account, "AdsPower", profileInUseEx.UserMessage);
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Профиль AdsPower занят для {account.DisplayName}",
            profileInUseEx.UserMessage,
            ct).ConfigureAwait(false);
    }

    private async Task HandleAdsPowerProxyFailureForAccountAsync(
        AvitoAccount account,
        AdsPowerProxyFailureException proxyEx,
        CancellationToken ct)
    {
        account.Status = AvitoAccountStatus.RequiresManualAction;
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

    private static bool ShouldHandleAsSubProfileAutomationFailure(Exception ex) =>
        ex is not AdsPowerProxyFailureException
        && (ex is AvitoPageMismatchException
            or JsonException
            or PuppeteerException
            or InvalidOperationException);

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
        if (ex is AvitoPageMismatchException mismatch && mismatch.ActualState is not null)
        {
            pageState = mismatch.ActualState;
        }

        try
        {
            var liveState = await session.GetPageStateAsync(ct).ConfigureAwait(false);
            pageState = PreferPageState(pageState, liveState);
        }
        catch
        {
            // best effort
        }

        var recoveryAttempts = ex is AvitoPageMismatchException mismatchEx
            ? mismatchEx.RecoveryAttempts
            : null;
        var kind = AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, ex);
        var detail = AvitoAutomationFailureFormatter.Format(expectedStep, pageState, ex, recoveryAttempts);
        var blocking = AvitoAutomationFailureFormatter.IsAccountBlockingIssue(kind);
        WorkerMonitoringLogger.PageStateHint(account, sub, pageState);
        WorkerMonitoringLogger.SubProfileIssue(account, sub, kind, detail, blocking);
        await PublishSubProfileIssueWithDiagnosticAsync(
            account,
            session,
            sub,
            kind,
            detail,
            ct,
            pageState: pageState,
            expectedStep: expectedStep).ConfigureAwait(false);

        return blocking;
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
        try
        {
            screenshot = await session.CapturePageScreenshotAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Screenshot is best-effort diagnostics.
        }

        AvitoPageState? pageState = null;
        try
        {
            pageState = await session.GetPageStateAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // best effort
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

        var kind = ex is AvitoCaptchaDetectedException captcha
            ? captcha.Kind
            : AvitoAutomationFailureFormatter.MapDiagnosticKind(pageState, ex);

        throw new SessionDiagnosticException(ex, kind, screenshot, pageUrl, subProfileId, subProfileName);
    }
}
