using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

using static Orbita.Api.Helpers.SubProfilesDisabledIdsHelper;

namespace Orbita.Api.Services;

public sealed class DashboardQueryService(
    OrbitaDbContext db,
    WorkerReleaseService releases,
    OfficeScopeService officeScope)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Lightweight 5-8s cache for summary to reduce repeated heavy aggregates under polling
    private static (DateTime ExpiresUtc, GlobalDashboardSummary? Value, OfficeScope Scope, Guid? OfficeFilter) _summaryCache;
    private static readonly object _cacheLock = new();

    public async Task<GlobalDashboardSummary> GetGlobalSummaryAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;

        // Check short TTL cache (avoids re-computing aggregates on every 10s dashboard poll)
        lock (_cacheLock)
        {
            if (_summaryCache.Value is not null &&
                _summaryCache.ExpiresUtc > nowUtc &&
                _summaryCache.Scope.IsGlobalAdmin == scope.IsGlobalAdmin &&
                _summaryCache.OfficeFilter == officeFilter)
            {
                // return a copy-ish (immutable record is fine to share)
                return _summaryCache.Value with { AggregatedAtUtc = nowUtc };
            }
        }

        var todayStart = nowUtc.Date;
        var workersQuery = officeScope.ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter);
        var workers = await workersQuery.ToListAsync(ct);
        var workerIds = workers.Select(w => w.Id).ToHashSet();
        var onlineWorkers = workers.Count(w => WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc));

        // Compute response counts from source of truth (CandidateResponses) for accuracy
        // instead of relying on (often zero) pushed snapshots.
        var responseStats = await ComputeTodayResponseStatsAsync(workerIds, todayStart, ct);

        // Keep snapshot data only for account-level details (ads counts, balances) and activity charts
        var latestSnapshots = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .GroupBy(x => x.WorkerId)
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToListAsync(ct);

        var statsList = latestSnapshots
            .Select(s => JsonSerializer.Deserialize<DashboardStatsDto>(s.StatsJson, JsonOptions))
            .Where(s => s is not null)
            .Cast<DashboardStatsDto>()
            .ToList();

        var balances = latestSnapshots
            .Select(s => JsonSerializer.Deserialize<List<WorkerBalanceDto>>(s.BalancesJson, JsonOptions) ?? [])
            .SelectMany(x => x)
            .ToList();

        // Prefer real response counts; fall back to snapshot sums only for fields not derivable from responses (ads etc.)
        int totalToday = responseStats.TotalToday;
        int sentToCrm = responseStats.Sent;
        int duplicates = responseStats.Duplicates;
        int inProgress = responseStats.InProgress;
        int actionRequired = responseStats.ActionRequired;

        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(workerIds, todayStart, ct);
        int errors = workerEventErrors.TodayCount;

        int connectedAccounts = statsList.Sum(s => s.ConnectedAccounts);
        int requiresAuth = statsList.Sum(s => s.RequiresAuthorization);
        int needAttention = statsList.Sum(s => s.AccountsNeedAttentionCount);
        int activeAds = statsList.Sum(s => s.ActiveAdsCount);
        int blockedAds = statsList.Sum(s => s.BlockedAdsCount);

        var accountStatusCounts = new DashboardAccountStatusCounts(0, 0, 0, 0);

        // Prefer WorkerAccounts for account status breakdown (matches Accounts page classification).
        if (workerIds.Count > 0)
        {
            var accountRows = await db.WorkerAccounts
                .AsNoTracking()
                .Where(a => workerIds.Contains(a.WorkerId))
                .Select(a => new { a.Status, a.IsEnabledInPanel })
                .ToListAsync(ct);

            if (accountRows.Count > 0)
            {
                var breakdown = AccountDashboardStatusClassifier.Summarize(
                    accountRows.Select(a => (a.Status, a.IsEnabledInPanel)));
                connectedAccounts = Math.Max(connectedAccounts, breakdown.Total);
                needAttention = Math.Max(needAttention, breakdown.Errors + breakdown.Blocked);
                accountStatusCounts = new DashboardAccountStatusCounts(
                    breakdown.Active,
                    breakdown.Inactive,
                    breakdown.Blocked,
                    breakdown.Errors);
            }
        }

        var result = new GlobalDashboardSummary(
            TotalWorkers: workers.Count,
            OnlineWorkers: onlineWorkers,
            TotalToday: totalToday,
            SentToCrm: sentToCrm,
            InProgress: inProgress,
            Duplicates: duplicates,
            Errors: errors,
            ActionRequired: actionRequired,
            ConnectedAccounts: connectedAccounts,
            RequiresAuthorization: requiresAuth,
            AccountsNeedAttentionCount: needAttention,
            AccountStatusCounts: accountStatusCounts,
            ActiveAdsCount: activeAds,
            BlockedAdsCount: blockedAds,
            TotalBalance: balances.Sum(b => b.TotalBalance),
            HourlyActivity: await ComputeHourlyActivityFromDbAsync(
                workerIds,
                todayStart,
                workerEventErrors.Hourly,
                ct),
            WeeklyByDayActivity: WorkerEventErrorStatsHelper.MergeDailyErrors(
                AggregateWeekly(statsList),
                workerEventErrors.Daily),
            AggregatedAtUtc: nowUtc);

        // Store in short cache
        lock (_cacheLock)
        {
            _summaryCache = (nowUtc.AddSeconds(7), result, scope, officeFilter);
        }

        return result;
    }

    public async Task<IReadOnlyList<WorkerListItem>> GetWorkersAsync(
        OfficeScope scope,
        Guid? officeFilter = null,
        CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var todayStart = nowUtc.Date;
        var workers = await officeScope
            .ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter)
            .OrderBy(x => x.DisplayName)
            .Select(w => new
            {
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                w.LastSeenAtUtc,
                w.IsEnabled,
                w.OfficeId,
                OfficeName = w.Office.Name,
                w.ActivityPhase,
                w.ActivityMessage,
                w.ActivityAccountId,
                w.ActivityAccountName,
                w.ActivitySubProfileId,
                w.ActivitySubProfileName,
                w.ActivityUpdatedAtUtc,
                w.ActivityNextCycleAtUtc,
                w.ActivityActiveAccountsJson
            })
            .ToListAsync(ct);

        var workerIds = workers.Select(w => w.Id).ToList();
        var operationalStats = await ComputeWorkerOperationalStatsAsync(workerIds, todayStart, ct);

        var latestStats = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .GroupBy(x => x.WorkerId)
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToDictionaryAsync(x => x.WorkerId, x => x.StatsJson, ct);

        var latestRelease = await releases.GetLatestAsync(ct);
        var latestReleaseVersion = latestRelease?.Version;

        return workers.Select(w =>
        {
            DashboardStatsDto? stats = null;
            if (latestStats.TryGetValue(w.Id, out var json))
            {
                stats = JsonSerializer.Deserialize<DashboardStatsDto>(json, JsonOptions);
            }

            operationalStats.TryGetValue(w.Id, out var op);
            op ??= WorkerOperationalStats.Empty;
            var updateAvailable = AppVersionHelper.IsNewer(latestReleaseVersion, w.AppVersion);
            return new WorkerListItem(
                w.Id,
                w.DisplayName,
                w.MachineName,
                w.AppVersion,
                w.MonitoringStatus,
                w.MonitoringStatusMessage,
                w.IsMonitoringActive,
                WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc),
                w.LastSeenAtUtc,
                op.TotalAccounts,
                op.TodayResponses,
                op.TodayDuplicates,
                op.TodayEventErrors,
                updateAvailable,
                latestReleaseVersion,
                w.OfficeId,
                w.OfficeName,
                w.IsEnabled,
                op.ActiveAccounts,
                WorkerActivityMapper.ToDto(
                    w.ActivityPhase,
                    w.ActivityMessage,
                    w.ActivityAccountId,
                    w.ActivityAccountName,
                    w.ActivitySubProfileId,
                    w.ActivitySubProfileName,
                    w.ActivityNextCycleAtUtc,
                    w.ActivityUpdatedAtUtc,
                    WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson)),
                WorkerActivityMapper.DeserializeActiveAccounts(w.ActivityActiveAccountsJson));
        }).ToList();
    }

    public async Task<WorkerDetail?> GetWorkerDetailAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return null;
        }

        var nowUtc = DateTime.UtcNow;
        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct);
        if (worker is null)
        {
            return null;
        }

        var latestSnapshot = await db.WorkerSnapshots
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderByDescending(x => x.CapturedAtUtc)
            .FirstOrDefaultAsync(ct);

        DashboardStatsDto? stats = null;
        List<WorkerBalanceDto> balances = [];
        if (latestSnapshot is not null)
        {
            stats = JsonSerializer.Deserialize<DashboardStatsDto>(latestSnapshot.StatsJson, JsonOptions);
            balances = JsonSerializer.Deserialize<List<WorkerBalanceDto>>(latestSnapshot.BalancesJson, JsonOptions) ?? [];
        }

        var todayStart = nowUtc.Date;
        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(new HashSet<Guid> { workerId }, todayStart, ct);
        var hourlyActivity = await ComputeHourlyActivityFromDbAsync(
            new HashSet<Guid> { workerId },
            todayStart,
            workerEventErrors.Hourly,
            ct);
        if (stats is not null)
        {
            stats = stats with { HourlyActivity = hourlyActivity };
        }

        var operationalStats = await ComputeWorkerOperationalStatsAsync([workerId], todayStart, ct);
        operationalStats.TryGetValue(workerId, out var op);
        op ??= WorkerOperationalStats.Empty;

        return new WorkerDetail(
            worker.Id,
            worker.DisplayName,
            worker.MachineName,
            worker.AppVersion,
            worker.MonitoringStatus,
            worker.MonitoringStatusMessage,
            worker.IsMonitoringActive,
            WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, nowUtc),
            worker.LastSeenAtUtc,
            worker.NextCycleCheckAtUtc,
            stats,
            balances,
            worker.MaxConcurrentAccounts,
            worker.LastCpuPercent,
            worker.LastRamPercent,
            worker.LastRamUsedMb,
            worker.LastRamTotalMb,
            worker.IpAddress,
            worker.OperatingSystem,
            worker.StartedAtUtc,
            worker.AgentVersion,
            worker.AdsPowerApiBaseUrl,
            worker.AdsPowerApiKey,
            worker.IsEnabled,
            op.TodayResponses,
            op.TodayDuplicates,
            op.TodayEventErrors,
            op.ActiveAccounts,
            op.TotalAccounts,
            WorkerActivityMapper.ToDto(worker),
            WorkerActivityMapper.DeserializeActiveAccounts(worker.ActivityActiveAccountsJson));
    }

    public async Task<IReadOnlyList<WorkerAccountDto>> GetWorkerAccountsAsync(
        Guid workerId,
        OfficeScope scope,
        CancellationToken ct = default)
    {
        if (!await officeScope.CanAccessWorkerAsync(scope, workerId, ct))
        {
            return [];
        }

        var todayStart = DateTime.UtcNow.Date;
        var rows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new
            {
                x.AccountId,
                x.DisplayName,
                x.Status,
                x.IsEnabledInPanel,
                x.ActiveAdsCount,
                x.BlockedCount,
                x.DraftsCount,
                x.LastErrorMessage,
                x.LastMonitoringAt,
                x.AdsPowerProfileId,
                x.SubProfilesJson,
                x.SubProfilesRefreshedAtUtc,
                x.SubProfilesRefreshRequestedAtUtc,
                x.SubProfilesDisabledIdsJson
            })
            .ToListAsync(ct);

        var accountIds = rows.Select(x => x.AccountId).ToList();
        var responseStats = accountIds.Count == 0
            ? new Dictionary<Guid, (int Total, int Duplicates)>()
            : await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.WorkerId == workerId && accountIds.Contains(x.AccountId) && x.CreatedAt >= todayStart)
                .GroupBy(x => x.AccountId)
                .Select(g => new
                {
                    g.Key,
                    Total = g.Count(),
                    Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate)
                })
                .ToDictionaryAsync(x => x.Key, x => (x.Total, x.Duplicates), ct);

        var eventStats = accountIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.WorkerEvents
                .AsNoTracking()
                .Where(x => x.WorkerId == workerId
                    && x.AccountId != null
                    && accountIds.Contains(x.AccountId.Value)
                    && !x.IsDismissed
                    && x.CreatedAtUtc >= todayStart
                    && (x.Level == "Error" || x.Level == "Warning"))
                .GroupBy(x => x.AccountId!.Value)
                .Select(g => new { AccountId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.AccountId, x => x.Count, ct);

        var subProfileResponseRows = accountIds.Count == 0
            ? []
            : await db.CandidateResponses
                .AsNoTracking()
                .Where(x => x.WorkerId == workerId
                    && accountIds.Contains(x.AccountId)
                    && x.CreatedAt >= todayStart
                    && x.AvitoSubProfileId != "")
                .GroupBy(x => new { x.AccountId, x.AvitoSubProfileId })
                .Select(g => new
                {
                    g.Key.AccountId,
                    g.Key.AvitoSubProfileId,
                    Total = g.Count(),
                    Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                    Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired),
                    LastActivity = g.Max(x => (DateTime?)x.CreatedAt)
                })
                .ToListAsync(ct);

        var subProfileStats = SubProfileOperationalStatsHelper.MergeResponseStats(
            subProfileResponseRows.Select(x => (
                x.AccountId,
                x.AvitoSubProfileId,
                x.Total,
                x.Duplicates,
                x.Errors,
                x.LastActivity)));

        var deserializedProfiles = rows
            .Select(x => (
                x.AccountId,
                Profiles: DeserializeSubProfiles(x.SubProfilesJson, x.SubProfilesDisabledIdsJson)))
            .ToList();

        var referencedAttachmentIds = SubProfileIssueHelper.CollectAttachmentIds(
            deserializedProfiles.SelectMany(x => x.Profiles ?? []));
        var existingAttachmentIds = referencedAttachmentIds.Count == 0
            ? new HashSet<Guid>()
            : await db.WorkerDiagnosticAttachments
                .AsNoTracking()
                .Where(x => referencedAttachmentIds.Contains(x.Id))
                .Select(x => x.Id)
                .ToHashSetAsync(ct);

        if (accountIds.Count > 0)
        {
            var subProfileEventRows = await db.WorkerEvents
                .AsNoTracking()
                .Where(x => x.WorkerId == workerId
                    && x.AccountId != null
                    && accountIds.Contains(x.AccountId.Value)
                    && !x.IsDismissed
                    && x.CreatedAtUtc >= todayStart
                    && (x.Level == "Error" || x.Level == "Warning"))
                .Select(x => new { AccountId = x.AccountId!.Value, x.Details })
                .ToListAsync(ct);

            SubProfileOperationalStatsHelper.ApplyEventErrors(
                subProfileStats,
                subProfileEventRows.Select(x => (x.AccountId, x.Details)),
                SubProfileOperationalStatsHelper.BuildNameLookup(deserializedProfiles));
        }

        return rows
            .Select(x =>
            {
                responseStats.TryGetValue(x.AccountId, out var responses);
                eventStats.TryGetValue(x.AccountId, out var eventErrors);
                var subProfiles = SubProfileOperationalStatsHelper.Enrich(
                    SubProfileIssueHelper.StripMissingAttachmentIssues(
                        DeserializeSubProfiles(x.SubProfilesJson, x.SubProfilesDisabledIdsJson),
                        existingAttachmentIds),
                    x.AccountId,
                    subProfileStats);
                return new WorkerAccountDto(
                    x.AccountId,
                    x.DisplayName,
                    x.Status,
                    x.IsEnabledInPanel,
                    x.ActiveAdsCount,
                    x.BlockedCount,
                    x.DraftsCount,
                    x.LastErrorMessage,
                    x.LastMonitoringAt,
                    x.IsEnabledInPanel,
                    x.AdsPowerProfileId,
                    subProfiles,
                    x.SubProfilesRefreshedAtUtc,
                    x.SubProfilesRefreshRequestedAtUtc,
                    responses.Total,
                    responses.Duplicates,
                    eventErrors);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<WorkerEventListItem>> GetWorkerEventsAsync(
        OfficeScope scope,
        Guid? workerId,
        Guid? officeFilter,
        int limit,
        DateTime? sinceUtc = null,
        CancellationToken ct = default)
    {
        if (workerId.HasValue && !await officeScope.CanAccessWorkerAsync(scope, workerId.Value, ct))
        {
            return [];
        }

        var workersQuery = officeScope.ApplyWorkerFilter(db.Workers.AsNoTracking(), scope, officeFilter);
        var allowedWorkerIds = await workersQuery.Select(x => x.Id).ToListAsync(ct);

        var query = db.WorkerEvents.AsNoTracking()
            .Where(x => allowedWorkerIds.Contains(x.WorkerId) && !x.IsDismissed);
        if (workerId.HasValue)
        {
            query = query.Where(x => x.WorkerId == workerId.Value);
        }

        if (sinceUtc.HasValue)
        {
            query = query.Where(x => x.CreatedAtUtc >= sinceUtc.Value);
        }

        return await (
            from evt in query.OrderByDescending(x => x.CreatedAtUtc).Take(limit)
            join worker in db.Workers.AsNoTracking() on evt.WorkerId equals worker.Id
            join account in db.WorkerAccounts.AsNoTracking()
                on new { evt.WorkerId, AccountId = evt.AccountId }
                equals new { account.WorkerId, AccountId = (Guid?)account.AccountId }
                into accounts
            from account in accounts.DefaultIfEmpty()
            select new WorkerEventListItem(
                evt.Id,
                evt.WorkerId,
                worker.DisplayName,
                evt.AccountId,
                account != null ? account.DisplayName : null,
                evt.Level,
                evt.Message,
                evt.Details,
                evt.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<ActivityPointDto>> ComputeHourlyActivityFromDbAsync(
        HashSet<Guid> workerIds,
        DateTime todayStartUtc,
        IReadOnlyList<int> hourlyEventErrors,
        CancellationToken ct)
    {
        var newCounts = new int[24];
        var sentCounts = new int[24];
        var duplicateCounts = new int[24];

        if (workerIds.Count > 0)
        {
            var rows = await db.CandidateResponses
                .AsNoTracking()
                .Where(x => workerIds.Contains(x.WorkerId) && x.CreatedAt >= todayStartUtc)
                .Select(x => new { x.CreatedAt, x.Status })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                var hour = UtcHour(row.CreatedAt);
                if (hour is < 0 or > 23)
                {
                    continue;
                }

                newCounts[hour]++;
                if (row.Status == ResponseStatuses.Sent)
                {
                    sentCounts[hour]++;
                }

                if (row.Status == ResponseStatuses.Duplicate)
                {
                    duplicateCounts[hour]++;
                }
            }
        }

        var errorHourly = hourlyEventErrors.Count == 24
            ? hourlyEventErrors
            : Enumerable.Repeat(0, 24).ToArray();

        return Enumerable.Range(0, 24)
            .Select(hour => new ActivityPointDto(
                $"{hour:00}:00",
                newCounts[hour],
                sentCounts[hour],
                duplicateCounts[hour],
                errorHourly[hour],
                hour,
                1,
                null))
            .ToArray();
    }

    private static int UtcHour(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return utc.Hour;
    }

    private static IReadOnlyList<ActivityPointDto> AggregateHourly(IReadOnlyList<DashboardStatsDto> statsList)
    {
        var buckets = new ActivityPointDto[24];
        for (var hour = 0; hour < 24; hour++)
        {
            buckets[hour] = new ActivityPointDto($"{hour:00}:00", 0, 0, 0, 0, hour, 1, null);
        }

        foreach (var stats in statsList)
        {
            foreach (var point in stats.HourlyActivity)
            {
                if (point.SlotStartHour is < 0 or > 23)
                {
                    continue;
                }

                var bucket = buckets[point.SlotStartHour];
                buckets[point.SlotStartHour] = bucket with
                {
                    NewCount = bucket.NewCount + point.NewCount,
                    SentCount = bucket.SentCount + point.SentCount,
                    DuplicateCount = bucket.DuplicateCount + point.DuplicateCount,
                    ErrorCount = bucket.ErrorCount + point.ErrorCount
                };
            }
        }

        return buckets;
    }

    private static IReadOnlyList<ActivityPointDto> AggregateWeekly(IReadOnlyList<DashboardStatsDto> statsList)
    {
        var map = new Dictionary<DateTime, ActivityPointDto>();
        foreach (var stats in statsList)
        {
            foreach (var point in stats.WeeklyByDayActivity)
            {
                if (!point.LocalDate.HasValue)
                {
                    continue;
                }

                var date = point.LocalDate.Value.Date;
                if (!map.TryGetValue(date, out var existing))
                {
                    map[date] = point with { Label = date.ToString("ddd d.MM") };
                }
                else
                {
                    map[date] = existing with
                    {
                        NewCount = existing.NewCount + point.NewCount,
                        SentCount = existing.SentCount + point.SentCount,
                        DuplicateCount = existing.DuplicateCount + point.DuplicateCount,
                        ErrorCount = existing.ErrorCount + point.ErrorCount
                    };
                }
            }
        }

        return map.OrderBy(x => x.Key).Select(x => x.Value).ToList();
    }

    // Computes today response aggregates directly from CandidateResponses (source of truth).
    // This makes dashboard/worker-list numbers accurate even when workers send empty snapshots.
    private async Task<(int TotalToday, int Sent, int Duplicates, int Errors, int InProgress, int ActionRequired)>
        ComputeTodayResponseStatsAsync(HashSet<Guid> workerIds, DateTime todayStartUtc, CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return (0, 0, 0, 0, 0, 0);

        var query = db.CandidateResponses.AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId) && x.CreatedAt >= todayStartUtc);

        var totalToday = await query.CountAsync(ct);
        if (totalToday == 0)
            return (0, 0, 0, 0, 0, 0);

        var sent = await query.CountAsync(x => x.Status == ResponseStatuses.Sent, ct);
        var duplicates = await query.CountAsync(x => x.Status == ResponseStatuses.Duplicate, ct);
        var errors = await query.CountAsync(x => x.Status == ResponseStatuses.Error, ct);
        var actionReq = await query.CountAsync(x => x.Status == ResponseStatuses.ActionRequired, ct);
        var inProgress = await query.CountAsync(x => x.Status == ResponseStatuses.InProgress, ct);

        return (totalToday, sent, duplicates, errors + actionReq, inProgress, actionReq);
    }

    private async Task<WorkerEventErrorStats> ComputeWorkerEventErrorStatsAsync(
        HashSet<Guid> workerIds,
        DateTime todayStartUtc,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return WorkerEventErrorStats.Empty;

        var rangeStart = todayStartUtc.AddDays(-WorkerEventErrorStatsHelper.DailyLookbackDays);
        var rows = await db.WorkerEvents
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId) && !x.IsDismissed)
            .Where(x => x.CreatedAtUtc >= rangeStart)
            .Where(x => x.Level == "Error" || x.Level == "Warning")
            .Select(x => new { x.WorkerId, x.CreatedAtUtc })
            .ToListAsync(ct);

        return WorkerEventErrorStatsHelper.Compute(
            rows.Select(x => (x.WorkerId, x.CreatedAtUtc)),
            todayStartUtc);
    }

    private static Dictionary<Guid, (int Total, int Active)> BuildAccountCounts(
        IEnumerable<(Guid WorkerId, string Status, bool IsEnabledInPanel)> accountRows) =>
        accountRows
            .GroupBy(x => x.WorkerId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Total: g.Count(),
                    Active: g.Count(a => AccountDashboardStatusClassifier.IsActiveInPanel(
                        a.Status,
                        a.IsEnabledInPanel))));

    private async Task<Dictionary<Guid, WorkerOperationalStats>> ComputeWorkerOperationalStatsAsync(
        IReadOnlyList<Guid> workerIds,
        DateTime todayStartUtc,
        CancellationToken ct)
    {
        if (workerIds.Count == 0)
            return [];

        var accountRows = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .Select(x => new { x.WorkerId, x.Status, x.IsEnabledInPanel })
            .ToListAsync(ct);

        var accountCounts = BuildAccountCounts(
            accountRows.Select(x => (x.WorkerId, x.Status, x.IsEnabledInPanel)));
        var responseStats = await ComputeWorkerTodayStatsAsync(workerIds, todayStartUtc, ct);
        var workerEventErrors = await ComputeWorkerEventErrorStatsAsync(workerIds.ToHashSet(), todayStartUtc, ct);

        return WorkerOperationalStatsHelper.Merge(
            workerIds,
            responseStats.ToDictionary(
                x => x.Key,
                x => (x.Value.Total, x.Value.Duplicates, x.Value.Errors)),
            workerEventErrors.PerWorkerToday,
            accountCounts);
    }

    // Per-worker today response totals.
    private async Task<Dictionary<Guid, (int Total, int Duplicates, int Errors)>> ComputeWorkerTodayStatsAsync(
        IReadOnlyList<Guid> workerIds, DateTime todayStartUtc, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (int Total, int Duplicates, int Errors)>();
        if (workerIds.Count == 0) return result;

        var idSet = workerIds.ToHashSet();
        var rows = await db.CandidateResponses.AsNoTracking()
            .Where(x => idSet.Contains(x.WorkerId) && x.CreatedAt >= todayStartUtc)
            .GroupBy(x => x.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Duplicates = g.Count(x => x.Status == ResponseStatuses.Duplicate),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired)
            })
            .ToListAsync(ct);

        foreach (var r in rows)
            result[r.WorkerId] = (Total: r.Total, Duplicates: r.Duplicates, Errors: r.Errors);

        foreach (var wid in workerIds)
            if (!result.ContainsKey(wid)) result[wid] = (Total: 0, Duplicates: 0, Errors: 0);

        return result;
    }

    private static IReadOnlyList<WorkerSubProfileDto>? DeserializeSubProfiles(
        string? json,
        string? disabledIdsJson = null) =>
        SubProfileDeserializer.Deserialize(json, disabledIdsJson);
}