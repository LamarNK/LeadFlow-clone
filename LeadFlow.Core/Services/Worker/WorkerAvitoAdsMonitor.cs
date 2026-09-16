using System.Collections.Concurrent;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.LocalChrome;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

/// <summary>
/// Отдельный read-only мониторинг объявлений. Не входит в проход сбора откликов.
/// </summary>
public sealed class WorkerAvitoAdsMonitor(
    IWorkerConfigProvider configProvider,
    WorkerAccountSessionFactory accountSessions,
    AvitoParserService avitoParser,
    IAvitoAdListingCatalog catalog,
    IWorkerMonitoringService monitoringService,
    IWorkerActivityReporter activityReporter,
    LocalChromeAccountLock localChromeLock)
{
    private readonly ConcurrentDictionary<Guid, DateTime> failureRetryNotBeforeUtc = new();

    private static TimeSpan ListCheckInterval =>
        TimeSpan.FromHours(MonitoringTiming.AvitoAdsListCheckIntervalHours);

    public async Task<int> RunDueAsync(Guid workerId, CancellationToken cancellationToken)
    {
        if (monitoringService.IsCaptchaHold)
        {
            return 0;
        }

        var config = await configProvider.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var processed = 0;

        foreach (var account in SelectDueAccounts(config.Accounts, workerId, DateTime.UtcNow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processed >= MonitoringTiming.AvitoAdsMaxAccountsPerLoop)
            {
                break;
            }

            if (monitoringService.IsAccountBusy(account.Id) || monitoringService.IsCaptchaHold)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            if (failureRetryNotBeforeUtc.TryGetValue(account.Id, out var retryNotBeforeUtc))
            {
                if (IsFailureCooldownActive(retryNotBeforeUtc, now))
                {
                    continue;
                }

                failureRetryNotBeforeUtc.TryRemove(account.Id, out _);
            }

            try
            {
                if (await ProcessAccountAsync(workerId, account, cancellationToken).ConfigureAwait(false))
                {
                    failureRetryNotBeforeUtc.TryRemove(account.Id, out _);
                    processed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var retryAtUtc = DateTime.UtcNow.AddMinutes(MonitoringTiming.AvitoAdsFailureRetryMinutes);
                failureRetryNotBeforeUtc[account.Id] = retryAtUtc;
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ads monitor: account {account.DisplayName} failed; retry after {retryAtUtc:O}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        return processed;
    }

    internal static IReadOnlyList<AvitoAccount> SelectDueAccounts(
        IReadOnlyList<AvitoAccount> accounts,
        Guid workerId,
        DateTime utcNow)
    {
        return accounts
            .Where(account => account.IsEnabled && HasSupportedRuntime(account))
            .ToList();
    }

    internal static bool IsFailureCooldownActive(DateTime retryNotBeforeUtc, DateTime utcNow) =>
        retryNotBeforeUtc > utcNow;

    public static bool IsListDue(
        IReadOnlyList<WorkerAvitoAdListScheduleDto> schedules,
        IReadOnlyList<AvitoSubProfile> enabledSubProfiles,
        DateTime utcNow)
    {
        if (enabledSubProfiles.Count == 0)
        {
            return IsSubProfileDue(schedules, string.Empty, utcNow);
        }

        return enabledSubProfiles.Any(sub => IsSubProfileDue(schedules, sub.Id, utcNow));
    }

    public static bool IsSubProfileDue(
        IReadOnlyList<WorkerAvitoAdListScheduleDto> schedules,
        string? avitoSubProfileId,
        DateTime utcNow)
    {
        var subId = avitoSubProfileId ?? string.Empty;
        var next = schedules
            .Where(x => string.Equals(x.AvitoSubProfileId, subId, StringComparison.Ordinal))
            .Select(x => x.NextCheckAtUtc)
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .DefaultIfEmpty()
            .Max();

        return next == default || next <= utcNow;
    }

    private async Task<bool> ProcessAccountAsync(Guid workerId, AvitoAccount account, CancellationToken cancellationToken)
    {
        using var captchaRequestContext = CaptchaProviderRequestContext.Use(
            CreateCaptchaProviderRequestContext(workerId, account));
        using var captchaTaskContext = AvitoCaptchaTaskContext.Use(
            GeeTestV4TaskOptions.FromBrowserProfile(
                account.AssignedUserAgent,
                account.ProxyType,
                account.ProxyAddress,
                account.ProxyUsername,
                account.ProxyPassword));

        var existing = (await catalog
            .GetAccountListingsAsync(workerId, account.Id, cancellationToken)
            .ConfigureAwait(false))
            .ToList();
        var schedules = await catalog
            .GetAccountSchedulesAsync(workerId, account.Id, cancellationToken)
            .ConfigureAwait(false);
        var enabledSubs = SubProfileEnabledFilter
            .GetEnabled(account.SubProfiles, account.DisabledSubProfileIds)
            .ToList();
        var utcNow = DateTime.UtcNow;
        if (!IsListDue(schedules, enabledSubs, utcNow))
        {
            return false;
        }

        if (WorkerAccountRuntime.IsLocal(account)
            && !localChromeLock.TryAcquire(account.Id, LocalChromeAccountLock.Monitoring, out _))
        {
            return false;
        }

        activityReporter.ReportAccount(account.Id, account.DisplayName, "Проверка объявлений");
        var localHeld = WorkerAccountRuntime.IsLocal(account);
        try
        {
            await using var sessionHolder = await accountSessions
                .OpenAsync(
                    account,
                    new AdsPowerConnectionOptions(
                        string.IsNullOrWhiteSpace(account.AdsPowerApiBaseUrl) ? string.Empty : account.AdsPowerApiBaseUrl,
                        string.IsNullOrWhiteSpace(account.AdsPowerApiKey) ? null : account.AdsPowerApiKey),
                    reportStartupStage: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (enabledSubs.Count == 0)
            {
                await ProcessSubProfileAsync(
                        workerId,
                        account,
                        subProfileId: string.Empty,
                        subProfileName: account.DisplayName,
                        sessionHolder.Session,
                        existing,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            var processedAny = false;
            foreach (var sub in enabledSubs.Where(sub => IsSubProfileDue(schedules, sub.Id, utcNow)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (monitoringService.IsCaptchaHold)
                {
                    break;
                }

                activityReporter.ReportSubProfile(
                    account.Id,
                    account.DisplayName,
                    sub.Id,
                    sub.Name,
                    "Список объявлений");
                using var subProfileCaptchaRequestContext = CaptchaProviderRequestContext.Use(
                    CreateCaptchaProviderRequestContext(workerId, account, sub));
                var switched = await sessionHolder.Session
                    .SwitchSubProfileAsync(sub.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (!switched.Ok)
                {
                    continue;
                }

                await ProcessSubProfileAsync(
                        workerId,
                        account,
                        sub.Id,
                        sub.Name,
                        sessionHolder.Session,
                        existing,
                        cancellationToken)
                    .ConfigureAwait(false);
                processedAny = true;
            }

            return processedAny;
        }
        finally
        {
            if (localHeld)
            {
                localChromeLock.Release(account.Id, LocalChromeAccountLock.Monitoring);
            }

            activityReporter.ReportAccountFinished(account.Id);
        }
    }

    internal static CaptchaProviderRequestContextValue CreateCaptchaProviderRequestContext(
        Guid workerId,
        AvitoAccount account,
        AvitoSubProfile? subProfile = null) =>
        new(
            WorkerId: workerId,
            AccountId: account.Id,
            CycleRunId: null,
            SubProfileRunId: null,
            SubProfileId: subProfile?.Id,
            SubProfileName: subProfile?.Name,
            Stage: subProfile is null
                ? CaptchaProviderRequestStages.Other
                : CaptchaProviderRequestStages.SubProfileSwitch,
            Reason: subProfile is null
                ? CaptchaProviderRequestReasons.FirewallDetected
                : CaptchaProviderRequestReasons.AfterSubProfileSwitch);

    private async Task ProcessSubProfileAsync(
        Guid workerId,
        AvitoAccount account,
        string subProfileId,
        string subProfileName,
        IAdsPowerAccountSession session,
        List<AvitoAdListingRecord> existing,
        CancellationToken cancellationToken)
    {
        var capture = await session.CaptureActiveAdsListAsync(cancellationToken).ConfigureAwait(false);
        if (!capture.Success)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Ads monitor: list capture failed for {account.DisplayName}/{subProfileName}: {capture.FailureReason}",
                DeskLinkAuditLogLevel.Warning);
            return;
        }

        var cards = new List<AvitoAdListCard>();
        var sawSupportedLayout = false;
        var blockedCount = 0;
        var unpublishedCount = 0;
        var auxiliaryTabsComplete = true;
        foreach (var html in capture.PageHtml)
        {
            var parsed = avitoParser.ParseProfilePage(html, account.Id, DateTime.UtcNow);
            if (!AvitoProVacancyLayout.IsSupported(parsed.LayoutKind) || !parsed.ParseSuccess)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ads monitor: layout {parsed.LayoutKind ?? parsed.ParseFailureReason} for {account.DisplayName}/{subProfileName}, vacancy parser skipped.",
                    DeskLinkAuditLogLevel.Info);
                continue;
            }

            sawSupportedLayout = true;
            blockedCount = Math.Max(blockedCount, parsed.BlockedCount);
            unpublishedCount = Math.Max(unpublishedCount, parsed.UnpublishedCount);
            var parsedCards = avitoParser.ToListCards(parsed);
            cards.AddRange(parsedCards);

            foreach (var card in parsedCards.Where(static x => x.ExpiresAtUtc is null))
            {
                var snippetHtml = AvitoParserService.ExtractItemSnippetHtml(html, card.AvitoItemId);
                await LogListExpiryParseFailureAsync(
                        account,
                        subProfileId,
                        subProfileName,
                        card,
                        snippetHtml)
                    .ConfigureAwait(false);
            }
        }

        if (!sawSupportedLayout)
        {
            return;
        }

        if (blockedCount > 0)
        {
            try
            {
                var html = await session.LoadBlockedItemsHtmlAsync(cancellationToken).ConfigureAwait(false);
                var blocked = avitoParser.ParseBlockedTabPage(html, account.Id);
                cards.AddRange(avitoParser.ToListCards(blocked));
                auxiliaryTabsComplete &= blocked.Count > 0;
            }
            catch (Exception ex)
            {
                auxiliaryTabsComplete = false;
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ads monitor: blocked tab failed for {account.DisplayName}/{subProfileName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        if (unpublishedCount > 0)
        {
            try
            {
                var html = await session.LoadUnpublishedItemsHtmlAsync(cancellationToken).ConfigureAwait(false);
                var unpublished = avitoParser.ParseUnpublishedTabPage(html, account.Id);
                cards.AddRange(avitoParser.ToListCards(unpublished));
                auxiliaryTabsComplete &= unpublished.Count > 0;
            }
            catch (Exception ex)
            {
                auxiliaryTabsComplete = false;
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ads monitor: unpublished tab failed for {account.DisplayName}/{subProfileName}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }
        }

        var utcNow = DateTime.UtcNow;
        var nextListCheckAtUtc = utcNow.Add(ListCheckInterval);
        var merged = AvitoAdListingSyncApplier
            .ApplyListSnapshot(
                existing,
                workerId,
                account.Id,
                subProfileId,
                cards,
                utcNow,
                capture.Complete && auxiliaryTabsComplete)
            .ToList();

        await catalog.SaveSubProfileSyncAsync(
                workerId,
                account.Id,
                subProfileId,
                merged,
                capture.Complete && auxiliaryTabsComplete,
                utcNow,
                nextListCheckAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        existing.RemoveAll(x =>
            x.WorkerId == workerId
            && x.AccountId == account.Id
            && string.Equals(x.AvitoSubProfileId, subProfileId, StringComparison.Ordinal));
        existing.AddRange(merged);
    }

    private static bool HasSupportedRuntime(AvitoAccount account) =>
        WorkerAccountRuntime.IsAdsPower(account)
        || WorkerAccountRuntime.IsMultilogin(account)
        || WorkerAccountRuntime.IsLocal(account);

    internal static Task LogListExpiryParseFailureAsync(
        AvitoAccount account,
        string subProfileId,
        string subProfileName,
        AvitoAdListCard card,
        string snippetHtml)
    {
        var reason = card.ExpiryParseError ?? "list_expiry_missing";
        var statusLine = AvitoParserService.ExtractActiveListStatusLine(snippetHtml);
        return GlobalLogger.Instance.LogAsync(
            $"Ads monitor: listing {card.AvitoItemId} ({account.DisplayName}/{subProfileName}) — list expiry parse failed: {reason}.",
            DeskLinkAuditLogLevel.Warning,
            errorKey: $"ads.{reason}",
            properties: new Dictionary<string, object?>
            {
                ["ads.event"] = "list_expiry_parse_failed",
                ["ads.reason"] = reason,
                ["ads.accountId"] = account.Id,
                ["ads.accountName"] = account.DisplayName,
                ["ads.subProfileId"] = subProfileId,
                ["ads.subProfileName"] = subProfileName,
                ["ads.avitoItemId"] = card.AvitoItemId,
                ["ads.statusText"] = card.StatusText,
                ["ads.statusLine"] = statusLine,
                ["ads.expiryText"] = AvitoParserService.ExtractActiveListExpiryText(snippetHtml),
                ["ads.cardHtml"] = snippetHtml
            });
    }

}
