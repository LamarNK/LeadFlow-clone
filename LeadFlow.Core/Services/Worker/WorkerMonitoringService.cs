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
    IBrowserMonitorSource browserMonitorSource) : IWorkerMonitoringService
{
    private const int LoopRecoveryPauseMinutes = 12;
    private const int MaxLoopRecoveryFailuresBeforeStop = 10;

    private readonly DebouncedWorkerTelemetryPusher _telemetryPusher = new(telemetrySink);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _consecutiveMonitoringLoopFailures;
    private int _consecutiveQuietMonitoringCycles;
    private volatile bool _captchaHold;

    public bool IsActive { get; private set; }
    public bool IsCaptchaHold => _captchaHold;

    public void EnterCaptchaHold()
    {
        _captchaHold = true;
        _ = GlobalLogger.Instance.LogAsync(
            "Мониторинг приостановлен для сессии капчи (текущие браузеры не закрываются).",
            DeskLinkAuditLogLevel.Info);
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
        _consecutiveQuietMonitoringCycles = 0;
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
        await _telemetryPusher.PushNowAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_captchaHold)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
                    var settings = ToAppSettings(config);
                    var accounts = config.Accounts
                        .Where(static a => a.IsEnabled)
                        .Where(IsAdsPowerAccount)
                        .ToList();

                    if (accounts.Count == 0)
                    {
                        WorkerMonitoringLogger.CycleSkippedNoAccounts();
                        activityReporter.ReportNoEnabledAccounts();
                        configProvider.InvalidateConfigCache();
                        await Task.Delay(
                                TimeSpan.FromSeconds(MonitoringTiming.NoAccountsConfigPollSeconds),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    WorkerMonitoringLogger.CycleStarted(accounts.Count);
                    activityReporter.ReportCycle(accounts.Count);

                    var cycleSw = Stopwatch.StartNew();
                    var (newResponsesThisCycle, accountsPolled, hadUndischargedBacklog, notPolled) =
                        await ProcessAccountsInCycleAsync(accounts, cancellationToken).ConfigureAwait(false);

                    cycleSw.Stop();
                    _consecutiveMonitoringLoopFailures = 0;

                    if (newResponsesThisCycle > 0 || hadUndischargedBacklog)
                    {
                        _consecutiveQuietMonitoringCycles = 0;
                    }
                    else
                    {
                        _consecutiveQuietMonitoringCycles++;
                    }

                    var historicalHeat = await repository
                        .GetHistoricalResponseIngestHeatScoreAsync(DateTime.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                    var delay = MonitoringCycleDelay.GetDelayAfterCycle(
                        newResponsesThisCycle,
                        accountsPolled,
                        _consecutiveQuietMonitoringCycles,
                        hadUndischargedBacklog,
                        historicalHeat);

                    WorkerMonitoringLogger.CycleFinished(
                        cycleSw.Elapsed.TotalSeconds,
                        newResponsesThisCycle,
                        accountsPolled,
                        accounts.Count,
                        delay.TotalMinutes);
                    WorkerMonitoringLogger.CycleNotPolledSummary(accounts.Count, notPolled);

                    if (pendingUpdateCoordinator.HasPendingInstall)
                    {
                        var pendingMessage = pendingUpdateCoordinator.BuildWaitingMessage(delay)
                            ?? "Пауза · установка обновления";
                        activityReporter.ReportWaiting(DateTime.UtcNow, pendingMessage);

                        if (pendingUpdateCoordinator.TryApplyPendingInstallAtPause())
                        {
                            break;
                        }

                        configProvider.InvalidateConfigCache();
                        await Task.Delay(
                                TimeSpan.FromSeconds(MonitoringTiming.PendingUpdateRetrySeconds),
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var nextCycleAt = DateTime.UtcNow.Add(delay);
                    activityReporter.ReportWaiting(
                        nextCycleAt,
                        $"Пауза до следующего цикла (~{Math.Max(1, (int)Math.Round(delay.TotalMinutes))} мин)");

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
                parallelism = Math.Clamp(config.MaxConcurrentAccounts, 1, 10);

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

        try
        {
            var (detectedTotal, backlog, subProfilesProcessed) = await StreamProcessAccountResponsesAsync(
                    account, settings, cancellationToken)
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
            return new AccountCycleOutcome(detectedTotal, true, backlog);
        }
        catch (AvitoCaptchaDetectedException captchaEx)
        {
            await HandleCaptchaForAccountAsync(account, captchaEx, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AvitoLoginRequiredException loginEx)
        {
            await HandleLoginRequiredForAccountAsync(account, loginEx, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerDailyOpenLimitExceededException limitEx)
        {
            await HandleAdsPowerDailyOpenLimitForAccountAsync(account, limitEx, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (AdsPowerRateLimitExceededException rateEx)
        {
            await HandleAdsPowerRateLimitForAccountAsync(account, rateEx, cancellationToken).ConfigureAwait(false);
            var reason = string.IsNullOrWhiteSpace(account.LastErrorMessage)
                ? rateEx.ApiMessage ?? rateEx.Message
                : account.LastErrorMessage;
            return new AccountCycleOutcome(0, false, false, $"AdsPower rate limit: {reason}");
        }
        catch (AdsPowerProfileInUseException profileInUseEx)
        {
            await HandleAdsPowerProfileInUseForAccountAsync(account, profileInUseEx, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (SessionDiagnosticException diagnosticEx)
        {
            await HandleSessionDiagnosticForAccountAsync(account, diagnosticEx, cancellationToken).ConfigureAwait(false);
            return new AccountCycleOutcome(0, true, false);
        }
        catch (OperationCanceledException)
        {
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
            return new AccountCycleOutcome(0, true, false);
        }
    }

    private async Task<(int Detected, bool Backlog, int SubProfilesProcessed)> StreamProcessAccountResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var publishedTotal = 0;
        var subProfilesProcessed = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        async Task<CandidateBatchPublishResult> ProcessBatchInlineAsync(IReadOnlyList<CandidateResponse> batch)
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

                var normalizedForKey = phoneNormalizer.Normalize(response.PhoneRaw);
                var key = !string.IsNullOrWhiteSpace(normalizedForKey)
                    ? $"phone:{normalizedForKey}"
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

            var profiles = readyCandidates
                .Select(response => new CandidateLookupProfileDto(
                    response.FullName,
                    response.Age,
                    response.City ?? string.Empty,
                    phoneNormalizer.Normalize(response.PhoneRaw) ?? string.Empty))
                .ToList();
            var matchedProfiles = await duplicateRepository
                .GetMatchedProfileIndicesAsync(profiles, account.Id, cancellationToken)
                .ConfigureAwait(false);

            var publishedCount = 0;
            var skippedPersonDuplicates = 0;
            var filteredAge = 0;
            var filteredGender = 0;
            var filterSamples = new List<string>(5);
            for (var i = 0; i < readyCandidates.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (matchedProfiles.Contains(i))
                {
                    skippedPersonDuplicates++;
                    continue;
                }

                var candidate = readyCandidates[i];
                var genderResolution = CandidateGenderResolver.Resolve(
                    candidate.FullName,
                    candidate.Gender,
                    candidate.RawText);
                candidate.Gender = CandidateGenderResolver.ToStoredGender(genderResolution);

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

                await PublishCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
                publishedCount++;
                publishedTotal++;

                if (!cancellationToken.IsCancellationRequested)
                {
                    await HumanDelay.BetweenResponsesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (filteredAge > 0 || filteredGender > 0)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Response filters for {account.DisplayName}: filtered_age={filteredAge}, filtered_gender={filteredGender}. Samples: {string.Join("; ", filterSamples)}",
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
            return (publishedTotal, false, 0);
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
            return (publishedTotal, false, 0);
        }

        var adsOptions = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        var browserOpened = false;
        BrowserMonitorScreencastCapture? monitorScreencast = null;
        var monitorContext = new BrowserMonitorRuntimeContext();
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
                        if (monitorScreencast is null)
                        {
                            monitorScreencast = await session
                                .CreateMonitorScreencastCaptureAsync(ct)
                                .ConfigureAwait(false);
                            await monitorScreencast
                                .WaitForFirstFrameAsync(TimeSpan.FromSeconds(4), ct)
                                .ConfigureAwait(false);
                        }

                        var bytes = monitorScreencast.TryGetLatestJpeg();
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

                var singleProfileHints = new CandidatesMessengerEnrichmentHints(
                    account.Id,
                    settings.DuplicateScope,
                    ResponseFilters: settings.ResponseFilters);
                var rawJson = await session
                    .ExtractCandidatesJsonAsync(singleProfileHints, cancellationToken)
                    .ConfigureAwait(false);
                var singleParse = await avitoResponseSource
                    .ParseCandidatesDetailedFromRawAsync(account, settings, rawJson, cancellationToken)
                    .ConfigureAwait(false);
                WorkerMonitoringLogger.ExtractionSummary(account, null, singleParse.Summary);
                var singleBatch = singleParse.Candidates;
                var singlePublishResult = await ProcessBatchInlineAsync(singleBatch).ConfigureAwait(false);
                WorkerMonitoringLogger.ExtractionPublished(
                    account,
                    null,
                    singlePublishResult.PublishedCount,
                    singlePublishResult.ReadyCount,
                    MonitoringTiming.MaxResponsesPerSubProfilePerCycle,
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

                return (publishedTotal, false, 0);
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
                return (publishedTotal, false, 0);
            }

            var collectStats = MonitoringTiming.CollectActiveAdsInWorkerPass && IsAdsStatsStale(account);
            ProfileResult? statsAggregate = collectStats
                ? new ProfileResult { ParseSuccess = false, PageLoadedSuccessfully = true, ActiveTabCounterResolved = true }
                : null;

            for (var i = 0; i < subProfiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var sub = subProfiles[i];
                diagnosticSubProfile = sub;
                monitorContext.SubProfileId = sub.Id;
                monitorContext.SubProfileName = sub.Name;
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
                            break;
                        }

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
                    var messengerHints = new CandidatesMessengerEnrichmentHints(
                        account.Id,
                        settings.DuplicateScope,
                        sub.Id,
                        settings.ResponseFilters);
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

                    var publishResult = await ProcessBatchInlineAsync(batch).ConfigureAwait(false);
                    WorkerMonitoringLogger.ExtractionPublished(
                        account,
                        sub,
                        publishResult.PublishedCount,
                        publishResult.ReadyCount,
                        MonitoringTiming.MaxResponsesPerSubProfilePerCycle,
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
                    throw;
                }
                catch (AvitoLoginRequiredException loginEx)
                {
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
                    if (blocking)
                    {
                        WorkerMonitoringLogger.AccountBlockingStop(
                            account,
                            $"проблема на субпрофиле «{sub.Name}»");
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

            return (publishedTotal, false, subProfilesProcessed);
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
        response.CreatedAt = response.CreatedAt == default ? DateTime.UtcNow : response.CreatedAt;

        await candidateSink.PublishAsync(response, cancellationToken).ConfigureAwait(false);
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
        ResponseFilters = config.ResponseFilters ?? ResponseCollectionFilters.Disabled
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
        && ex is not SessionDiagnosticException
        && !ShouldHandleAsSubProfileAutomationFailure(ex);

    private static bool ShouldHandleAsSubProfileAutomationFailure(Exception ex) =>
        ex is AvitoPageMismatchException
        or JsonException
        or PuppeteerException
        or InvalidOperationException;

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