using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Data;
using LeadFlow.Core.Services;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;

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
    IWorkerDiagnosticsUploader diagnosticsUploader,
    IPhoneNormalizer phoneNormalizer,
    ICandidateParser candidateParser,
    IAvitoResponseSource avitoResponseSource,
    AvitoDemoResponseSource avitoDemoResponseSource,
    AvitoParserService avitoParser,
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService) : IWorkerMonitoringService
{
    private const int LoopRecoveryPauseMinutes = 12;
    private const int MaxLoopRecoveryFailuresBeforeStop = 10;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _consecutiveMonitoringLoopFailures;
    private int _consecutiveQuietMonitoringCycles;

    public bool IsActive { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loopTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsActive = true;
        _consecutiveQuietMonitoringCycles = 0;
        _ = GlobalLogger.Instance.LogAsync("Worker monitoring started.", DeskLinkAuditLogLevel.Info);
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
        _ = GlobalLogger.Instance.LogAsync("Worker monitoring stopped.", DeskLinkAuditLogLevel.Info);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
                    var settings = ToAppSettings(config);
                    var accounts = config.Accounts
                        .Where(static a => a.IsEnabled)
                        .Where(IsAdsPowerAccount)
                        .ToList();

                    _ = GlobalLogger.Instance.LogAsync(
                        $"Worker cycle start: {accounts.Count} AdsPower account(s).",
                        DeskLinkAuditLogLevel.Info);

                    var cycleSw = Stopwatch.StartNew();
                    var newResponsesThisCycle = 0;
                    var accountsPolled = 0;
                    var hadUndischargedBacklog = false;
                    var parallelism = Math.Clamp(config.MaxConcurrentAccounts, 1, 10);
                    var launchSlot = 0;

                    if (parallelism == 1)
                    {
                        foreach (var account in accounts)
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }

                            var (newCount, polled, backlog) = await ProcessAccountAsync(
                                    account, settings, cancellationToken)
                                .ConfigureAwait(false);
                            if (polled)
                            {
                                accountsPolled++;
                                newResponsesThisCycle += newCount;
                                hadUndischargedBacklog |= backlog;
                            }

                            await Task.Delay(
                                    TimeSpan.FromSeconds(MonitoringTiming.DelayBetweenAccountsSeconds),
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await Parallel.ForEachAsync(
                            accounts,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = parallelism,
                                CancellationToken = cancellationToken
                            },
                            async (account, ct) =>
                            {
                                var slot = Interlocked.Increment(ref launchSlot) - 1;
                                if (slot > 0)
                                {
                                    await Task.Delay(
                                            TimeSpan.FromMilliseconds(slot * MonitoringTiming.ParallelAccountLaunchStaggerMs),
                                            ct)
                                        .ConfigureAwait(false);
                                }

                                var (newCount, polled, backlog) = await ProcessAccountAsync(account, settings, ct)
                                    .ConfigureAwait(false);
                                if (polled)
                                {
                                    Interlocked.Increment(ref accountsPolled);
                                    Interlocked.Add(ref newResponsesThisCycle, newCount);
                                    if (backlog)
                                    {
                                        Volatile.Write(ref hadUndischargedBacklog, true);
                                    }
                                }
                            }).ConfigureAwait(false);
                    }

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

                    _ = GlobalLogger.Instance.LogAsync(
                        $"Worker cycle done in {cycleSw.Elapsed.TotalSeconds:F1}s; next in {delay.TotalMinutes:F1} min (new={newResponsesThisCycle}, polled={accountsPolled}).",
                        DeskLinkAuditLogLevel.Info);

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
    }

    private async Task<bool> TryRecoverLoopAsync(Exception ex, CancellationToken cancellationToken)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"Worker monitoring loop failed.{Environment.NewLine}{ex}",
            DeskLinkAuditLogLevel.Error);

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

    private async Task<(int NewResponsesDetected, bool PolledSource, bool HasUndischargedBacklog)> ProcessAccountAsync(
        AvitoAccount account,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (!IsAdsPowerAccount(account))
        {
            return (0, false, false);
        }

        if (account.Status is AvitoAccountStatus.RequiresLogin
            or AvitoAccountStatus.RequiresManualAction
            or AvitoAccountStatus.Paused)
        {
            AccountIssueTracker.RefreshAccountIssueMessage(account);
            await repository.AddLogAsync(new ProcessingLogItem
            {
                AccountId = account.Id,
                Level = "Warning",
                Message = "Аккаунт пропущен",
                Details = $"Статус: {account.Status}"
            }, cancellationToken).ConfigureAwait(false);
            return (0, false, false);
        }

        account.Status = AvitoAccountStatus.Monitoring;
        account.LastMonitoringAt = DateTime.UtcNow;
        await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);

        try
        {
            var maxPerCycle = MonitoringTiming.MaxResponsesPerAccountPerCycle;
            var (detectedTotal, backlog) = await StreamProcessAccountResponsesAsync(
                    account, settings, maxPerCycle, cancellationToken)
                .ConfigureAwait(false);

            if (account.Status == AvitoAccountStatus.Monitoring)
            {
                account.Status = AvitoAccountStatus.Authorized;
            }

            AccountIssueTracker.RefreshAccountIssueMessage(account);
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            return (detectedTotal, true, backlog);
        }
        catch (AvitoCaptchaDetectedException captchaEx)
        {
            await HandleCaptchaForAccountAsync(account, captchaEx, cancellationToken).ConfigureAwait(false);
            return (0, true, false);
        }
        catch (AdsPowerDailyOpenLimitExceededException limitEx)
        {
            await HandleAdsPowerDailyOpenLimitForAccountAsync(account, limitEx, cancellationToken).ConfigureAwait(false);
            return (0, true, false);
        }
        catch (AdsPowerRateLimitExceededException rateEx)
        {
            await HandleAdsPowerRateLimitForAccountAsync(account, rateEx, cancellationToken).ConfigureAwait(false);
            return (0, false, false);
        }
        catch (SessionDiagnosticException diagnosticEx)
        {
            await HandleSessionDiagnosticForAccountAsync(account, diagnosticEx, cancellationToken).ConfigureAwait(false);
            return (0, true, false);
        }
        catch (Exception ex)
        {
            account.LastErrorMessage = ex.Message;
            account.Status = AvitoAccountStatus.Error;
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker account {account.DisplayName} failed: {ex.Message}",
                DeskLinkAuditLogLevel.Error);
            await PublishAccountEventAsync(
                account,
                "Error",
                $"Ошибка аккаунта {account.DisplayName}: {ex.Message}",
                ex.Message,
                cancellationToken).ConfigureAwait(false);
            return (0, true, false);
        }
    }

    private async Task<(int Detected, bool Backlog)> StreamProcessAccountResponsesAsync(
        AvitoAccount account,
        AppSettings settings,
        int maxPerCycle,
        CancellationToken cancellationToken)
    {
        var processedInCycle = 0;
        var detectedTotal = 0;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var budgetExhausted = false;

        async Task<int> ProcessBatchInlineAsync(IReadOnlyList<CandidateResponse> batch, string sourceLabel)
        {
            if (batch.Count == 0)
            {
                return 0;
            }

            var freshCount = 0;
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

                freshCount++;
                detectedTotal++;

                if (processedInCycle >= maxPerCycle)
                {
                    budgetExhausted = true;
                    continue;
                }

                await PublishCandidateAsync(response, cancellationToken).ConfigureAwait(false);
                processedInCycle++;

                if (processedInCycle < maxPerCycle && !cancellationToken.IsCancellationRequested)
                {
                    await HumanDelay.BetweenResponsesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"Worker {account.DisplayName} ({sourceLabel}): batch {batch.Count}, published {Math.Min(freshCount, maxPerCycle)}.",
                DeskLinkAuditLogLevel.Info);
            return freshCount;
        }

        if (settings.DemoModeEnabled)
        {
            var demo = await avitoDemoResponseSource
                .GetBatchAsync(account, maxPerCycle, cancellationToken)
                .ConfigureAwait(false);
            await ProcessBatchInlineAsync(demo, "demo").ConfigureAwait(false);
            return (detectedTotal, budgetExhausted || detectedTotal > maxPerCycle);
        }

        var hasAdsPowerCreds =
            !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
            && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

        if (!hasAdsPowerCreds)
        {
            var responses = await avitoResponseSource
                .GetNewResponsesAsync(account, settings, cancellationToken)
                .ConfigureAwait(false);
            await ProcessBatchInlineAsync(responses, account.DisplayName).ConfigureAwait(false);
            return (detectedTotal, budgetExhausted || responses.Count > maxPerCycle);
        }

        var adsOptions = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        try
        {
            await using var session = await adsPowerAvitoAutomationService
                .OpenAccountSessionAsync(adsOptions, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(false);

            AvitoSubProfile? diagnosticSubProfile = null;
            try
            {
            if (ShouldRefreshSubProfiles(account))
            {
                await TryRefreshSubProfilesAsync(account, session, cancellationToken).ConfigureAwait(false);
            }

            var messengerHints = new CandidatesMessengerEnrichmentHints(account.Id, settings.DuplicateScope);
            var allSubProfiles = account.SubProfiles;
            if (allSubProfiles.Count == 0)
            {
                if (IsAdsStatsStale(account))
                {
                    var part = await CollectProfileItemsFromSessionAsync(account, session, cancellationToken)
                        .ConfigureAwait(false);
                    if (part.ParseSuccess)
                    {
                        await ApplyStatsSnapshotAsync(account, part, cancellationToken).ConfigureAwait(false);
                    }
                }

                var rawJson = await session
                    .ExtractCandidatesJsonAsync(messengerHints, cancellationToken)
                    .ConfigureAwait(false);
                var singleBatch = await avitoResponseSource
                    .ParseCandidatesFromRawAsync(account, settings, rawJson, cancellationToken)
                    .ConfigureAwait(false);
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

                await ProcessBatchInlineAsync(singleBatch, account.DisplayName).ConfigureAwait(false);
                return (detectedTotal, budgetExhausted || singleBatch.Count > maxPerCycle);
            }

            var subProfiles = SubProfileEnabledFilter.GetEnabled(allSubProfiles, account.DisabledSubProfileIds);
            if (subProfiles.Count == 0)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker {account.DisplayName}: все субпрофили отключены в панели, мониторинг пропущен.",
                    DeskLinkAuditLogLevel.Warning);
                return (detectedTotal, budgetExhausted);
            }

            var collectStats = IsAdsStatsStale(account);
            ProfileResult? statsAggregate = collectStats
                ? new ProfileResult { ParseSuccess = false, PageLoadedSuccessfully = true, ActiveTabCounterResolved = true }
                : null;

            for (var i = 0; i < subProfiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested || budgetExhausted)
                {
                    break;
                }

                var sub = subProfiles[i];
                diagnosticSubProfile = sub;
                var switched = await session.SwitchSubProfileAsync(sub.Id, cancellationToken).ConfigureAwait(false);
                if (!switched)
                {
                    await PublishSubProfileIssueWithDiagnosticAsync(
                        account,
                        session,
                        sub,
                        AvitoSubProfileIssueKind.SwitchFailed,
                        "не удалось переключить суб-профиль.",
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var rawJson = await session
                    .ExtractCandidatesJsonAsync(messengerHints, cancellationToken)
                    .ConfigureAwait(false);
                var issueAtBefore = sub.LastIssueAt;
                var batch = await avitoResponseSource
                    .ParseCandidatesFromRawAsync(account, settings, rawJson, cancellationToken, sub)
                    .ConfigureAwait(false);
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
                }

                await ProcessBatchInlineAsync(batch, sub.Name).ConfigureAwait(false);

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

                        if (part.Balance.HasValue)
                        {
                            sub.Balance = part.Balance;
                        }
                    }
                }

                if (i < subProfiles.Count - 1 && !cancellationToken.IsCancellationRequested && !budgetExhausted)
                {
                    await HumanDelay.BetweenSubProfilesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            account.SetSubProfiles(allSubProfiles);
            AccountIssueTracker.RefreshAccountIssueMessage(account);

            if (collectStats && statsAggregate is { ParseSuccess: true })
            {
                await ApplyStatsSnapshotAsync(account, statsAggregate, cancellationToken).ConfigureAwait(false);
            }

            return (detectedTotal, budgetExhausted);
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
            await TryCloseAdsPowerBrowserForAccountAsync(account, adsOptions, cancellationToken).ConfigureAwait(false);
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

        await repository.SaveCandidateAsync(response, cancellationToken).ConfigureAwait(false);
        await repository.AddLogAsync(new ProcessingLogItem
        {
            CandidateResponseId = response.Id,
            AccountId = response.AccountId,
            Level = "Info",
            Message = "Новый отклик (worker)",
            Details = response.FullName
        }, cancellationToken).ConfigureAwait(false);

        var publishResult = await candidateSink.PublishAsync(response, cancellationToken).ConfigureAwait(false);
        if (publishResult.Synchronized)
        {
            response.Status = publishResult.Status;
            if (!string.IsNullOrWhiteSpace(publishResult.ErrorMessage))
            {
                response.ErrorMessage = publishResult.ErrorMessage;
            }

            response.ProcessedAt = DateTime.UtcNow;
            await repository.SaveCandidateAsync(response, cancellationToken).ConfigureAwait(false);
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
            var discovered = AvitoSubProfilesParser.Parse(html);
            if (discovered.Count > 0)
            {
                var merged = AvitoSubProfileMerger.Merge(account.SubProfiles, discovered);
                account.SetSubProfiles(merged);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker {account.DisplayName}: sub-profiles refreshed, count={merged.Count}.",
                    DeskLinkAuditLogLevel.Info);
            }

            account.SubProfilesRefreshedAt = DateTime.UtcNow;
            await repository.SaveAccountAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker {account.DisplayName}: sub-profiles refresh failed: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
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
        MonitoringSafety = new MonitoringSafetyOptions { MaxConcurrentAccounts = config.MaxConcurrentAccounts }
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
    }

    private async Task<ProfileResult> CollectProfileItemsAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var activeHtml = await adsPowerAvitoAutomationService
            .LoadProfileItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(activeHtml))
        {
            return new ProfileResult { ParseSuccess = false, ParseFailureReason = "empty_active_items_html" };
        }

        var part = avitoParser.ParseProfilePage(activeHtml, account.Id);
        part.PageLoadedSuccessfully = true;

        if (part.BlockedCount > 0)
        {
            try
            {
                var blockedHtml = await adsPowerAvitoAutomationService
                    .LoadBlockedItemsHtmlAsync(options, account.AdsPowerProfileId!, cancellationToken)
                    .ConfigureAwait(false);
                part.BlockedAds.AddRange(avitoParser.ParseBlockedTabPage(blockedHtml, account.Id));
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Worker: blocked tab load failed for {account.DisplayName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        return part;
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

    private async Task TryCloseAdsPowerBrowserForAccountAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
        {
            return;
        }

        try
        {
            await adsPowerAvitoAutomationService
                .CloseBrowserAsync(options, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Worker: failed to close AdsPower browser for {account.DisplayName}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning);
        }
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
        account.LastErrorMessage = sub is not null
            ? AccountIssueFormatting.FormatIssue(account, sub, diagnosticEx.DiagnosticKind, inner.Message)
            : inner.Message;
        account.Status = AvitoAccountStatus.Error;
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        _ = GlobalLogger.Instance.LogAsync(
            $"Worker account {account.DisplayName} failed: {inner.Message}",
            DeskLinkAuditLogLevel.Error);

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
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Rate limit AdsPower для {account.DisplayName}",
            account.LastErrorMessage,
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
        bool applyIssue = true)
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
            ct).ConfigureAwait(false);
        StoreSubProfileDiagnosticAttachment(account, sub, diagnostic.AttachmentId);
        await PublishAccountEventAsync(account, "Warning", message, diagnostic.Details, ct).ConfigureAwait(false);
    }

    private async Task PublishAccountDiagnosticFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string message,
        CancellationToken ct)
    {
        var diagnostic = await BuildDiagnosticEventDetailsFromSessionAsync(
            account,
            session,
            kind,
            message,
            null,
            null,
            ct).ConfigureAwait(false);
        await PublishAccountEventAsync(account, "Warning", message, diagnostic.Details, ct).ConfigureAwait(false);
    }

    private Task<WorkerDiagnosticEventDetails> BuildDiagnosticEventDetailsFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string text,
        string? subProfileId,
        string? subProfileName,
        CancellationToken ct) =>
        BuildDiagnosticEventDetailsFromSessionAsync(
            account,
            session,
            kind,
            text,
            subProfileId,
            subProfileName,
            session.CapturePageScreenshotAsync(ct),
            ct);

    private async Task<WorkerDiagnosticEventDetails> BuildDiagnosticEventDetailsFromSessionAsync(
        AvitoAccount account,
        IAdsPowerAccountSession session,
        string kind,
        string text,
        string? subProfileId,
        string? subProfileName,
        Task<byte[]?> screenshotTask,
        CancellationToken ct)
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

        return await WorkerDiagnosticEventDetailsBuilder.BuildAsync(
            diagnosticsUploader,
            account.Id,
            kind,
            text,
            session.CurrentPageUrl,
            screenshot,
            subProfileId,
            subProfileName,
            ct).ConfigureAwait(false);
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
        && ex is not SessionDiagnosticException;

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

        var pageUrl = session.CurrentPageUrl;
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

        var kind = ex switch
        {
            AvitoCaptchaDetectedException captcha => captcha.Kind,
            JsonException => "parse-error",
            _ => "account-error"
        };

        throw new SessionDiagnosticException(ex, kind, screenshot, pageUrl, subProfileId, subProfileName);
    }
}