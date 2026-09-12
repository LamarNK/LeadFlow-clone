using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.LocalChrome;

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
    public static AvitoAdListingScheduleOptions DefaultSchedule { get; } = new()
    {
        ListCheckInterval = TimeSpan.FromHours(MonitoringTiming.AvitoAdsListCheckIntervalHours),
        MaxDetailPagesPerRun = MonitoringTiming.AvitoAdsMaxDetailPagesPerRun,
        FreshAgeDays = MonitoringTiming.AvitoAdsFreshAgeDays
    };

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

            try
            {
                if (await ProcessAccountAsync(workerId, account, cancellationToken).ConfigureAwait(false))
                {
                    processed++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Ads monitor: account {account.DisplayName} failed: {ex.Message}",
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

    public static bool IsListDue(
        IReadOnlyList<AvitoAdListingRecord> existing,
        IReadOnlyList<AvitoSubProfile> enabledSubProfiles,
        DateTime utcNow,
        AvitoAdListingScheduleOptions options)
    {
        if (existing.Count == 0)
        {
            return true;
        }

        if (enabledSubProfiles.Count == 0)
        {
            var last = existing
                .Select(x => x.LastSuccessfulListCheckAtUtc)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .DefaultIfEmpty()
                .Max();
            return last == default
                || AvitoAdListingScheduler.ShouldCheckList(last, utcNow, options.ListCheckInterval);
        }

        foreach (var sub in enabledSubProfiles)
        {
            var last = existing
                .Where(x => string.Equals(x.AvitoSubProfileId, sub.Id, StringComparison.Ordinal))
                .Select(x => x.LastSuccessfulListCheckAtUtc)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .DefaultIfEmpty()
                .Max();
            if (last == default
                || AvitoAdListingScheduler.ShouldCheckList(last, utcNow, options.ListCheckInterval))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ProcessAccountAsync(Guid workerId, AvitoAccount account, CancellationToken cancellationToken)
    {
        var existing = (await catalog
            .GetAccountListingsAsync(workerId, account.Id, cancellationToken)
            .ConfigureAwait(false))
            .ToList();
        var enabledSubs = SubProfileEnabledFilter
            .GetEnabled(account.SubProfiles, account.DisabledSubProfileIds)
            .ToList();
        var utcNow = DateTime.UtcNow;
        if (!IsListDue(existing, enabledSubs, utcNow, DefaultSchedule))
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

            foreach (var sub in enabledSubs)
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
            }

            return true;
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
            cards.AddRange(avitoParser.ToListCards(parsed));
        }

        if (!sawSupportedLayout)
        {
            return;
        }

        var utcNow = DateTime.UtcNow;
        var merged = AvitoAdListingSyncApplier
            .ApplyListSnapshot(
                existing,
                workerId,
                account.Id,
                subProfileId,
                cards,
                utcNow,
                capture.Complete)
            .ToList();

        await catalog.SaveSubProfileSyncAsync(
                workerId,
                account.Id,
                subProfileId,
                merged,
                capture.Complete,
                DateTime.UtcNow,
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

}
