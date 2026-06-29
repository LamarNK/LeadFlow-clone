using System.Diagnostics;
using System.Text.Json;
using LeadFlow.Core.Data;
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

        var subProfiles = account.SubProfiles;
        var hasAdsPowerCreds =
            !string.IsNullOrWhiteSpace(account.AdsPowerProfileId)
            && !string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl);

        if (subProfiles.Count == 0 || !hasAdsPowerCreds)
        {
            var options = hasAdsPowerCreds
                ? new AdsPowerConnectionOptions(
                    account.AdsPowerApiBaseUrl!,
                    string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey)
                : null;

            try
            {
                if (hasAdsPowerCreds && IsAdsStatsStale(account))
                {
                    var part = await CollectProfileItemsAsync(account, options!, cancellationToken)
                        .ConfigureAwait(false);
                    if (part.ParseSuccess)
                    {
                        await ApplyStatsSnapshotAsync(account, part, cancellationToken).ConfigureAwait(false);
                    }
                }

                var responses = await avitoResponseSource
                    .GetNewResponsesAsync(account, settings, cancellationToken)
                    .ConfigureAwait(false);
                await ProcessBatchInlineAsync(responses, account.DisplayName).ConfigureAwait(false);
                return (detectedTotal, budgetExhausted || responses.Count > maxPerCycle);
            }
            finally
            {
                if (options is not null)
                {
                    await TryCloseAdsPowerBrowserForAccountAsync(account, options, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        var adsOptions = new AdsPowerConnectionOptions(
            account.AdsPowerApiBaseUrl!,
            string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey);

        try
        {
            var collectStats = IsAdsStatsStale(account);
            ProfileResult? statsAggregate = collectStats
                ? new ProfileResult { ParseSuccess = false, PageLoadedSuccessfully = true, ActiveTabCounterResolved = true }
                : null;

            await using var session = await adsPowerAvitoAutomationService
                .OpenAccountSessionAsync(adsOptions, account.AdsPowerProfileId!, cancellationToken)
                .ConfigureAwait(false);

            var messengerHints = new CandidatesMessengerEnrichmentHints(account.Id, settings.DuplicateScope);

            for (var i = 0; i < subProfiles.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested || budgetExhausted)
                {
                    break;
                }

                var sub = subProfiles[i];
                var switched = await session.SwitchSubProfileAsync(sub.Id, cancellationToken).ConfigureAwait(false);
                if (!switched)
                {
                    AccountIssueTracker.ApplySubProfileIssue(
                        account,
                        sub,
                        AvitoSubProfileIssueKind.SwitchFailed,
                        "не удалось переключить суб-профиль.");
                    continue;
                }

                var rawJson = await session
                    .ExtractCandidatesJsonAsync(messengerHints, cancellationToken)
                    .ConfigureAwait(false);
                var batch = await avitoResponseSource
                    .ParseCandidatesFromRawAsync(account, settings, rawJson, cancellationToken, sub)
                    .ConfigureAwait(false);

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
                    }
                }

                if (i < subProfiles.Count - 1 && !cancellationToken.IsCancellationRequested && !budgetExhausted)
                {
                    await HumanDelay.BetweenSubProfilesAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            account.SetSubProfiles(subProfiles);
            AccountIssueTracker.RefreshAccountIssueMessage(account);

            if (collectStats && statsAggregate is { ParseSuccess: true })
            {
                await ApplyStatsSnapshotAsync(account, statsAggregate, cancellationToken).ConfigureAwait(false);
            }

            return (detectedTotal, budgetExhausted);
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
        account.LastErrorMessage =
            $"Avito captcha/firewall ({captchaEx.Kind}). Откройте браузер и пройдите проверку.";
        await repository.SaveAccountAsync(account, ct).ConfigureAwait(false);

        var details = await BuildCaptchaEventDetailsAsync(account, captchaEx, ct).ConfigureAwait(false);
        await PublishAccountEventAsync(
            account,
            "Warning",
            $"Капча/firewall на аккаунте {account.DisplayName}",
            details,
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

    private async Task<string> BuildCaptchaEventDetailsAsync(
        AvitoAccount account,
        AvitoCaptchaDetectedException captchaEx,
        CancellationToken ct)
    {
        var fallback = $"{captchaEx.Kind} :: {captchaEx.Url ?? "<unknown url>"} :: {account.LastErrorMessage}";
        if (captchaEx.ScreenshotPng is not { Length: > 0 } png)
        {
            return fallback;
        }

        var attachmentId = await diagnosticsUploader
            .UploadScreenshotAsync(account.Id, png, captchaEx.Kind, captchaEx.Url, ct)
            .ConfigureAwait(false);
        if (attachmentId is null)
        {
            return fallback;
        }

        return JsonSerializer.Serialize(new
        {
            attachmentId,
            kind = captchaEx.Kind,
            url = captchaEx.Url,
            text = account.LastErrorMessage
        });
    }
}