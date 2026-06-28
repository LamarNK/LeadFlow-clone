using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

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
        int errors = responseStats.Errors;
        int inProgress = responseStats.InProgress;
        int actionRequired = responseStats.ActionRequired;

        int connectedAccounts = statsList.Sum(s => s.ConnectedAccounts);
        int requiresAuth = statsList.Sum(s => s.RequiresAuthorization);
        int needAttention = statsList.Sum(s => s.AccountsNeedAttentionCount);
        int activeAds = statsList.Sum(s => s.ActiveAdsCount);
        int blockedAds = statsList.Sum(s => s.BlockedAdsCount);

        // If we have WorkerAccounts data we can compute some account aggregates directly (lightweight)
        if (workerIds.Count > 0)
        {
            var accountAgg = await db.WorkerAccounts
                .AsNoTracking()
                .Where(a => workerIds.Contains(a.WorkerId))
                .GroupBy(a => 1)
                .Select(g => new
                {
                    Total = g.Count(),
                    Active = g.Count(a => a.IsEnabledInPanel && a.Status != "Blocked"),
                    NeedAttention = g.Count(a => a.IsEnabledInPanel && (a.Status == "RequiresLogin" || a.Status == "RequiresManualAction" || a.Status == "Error" || a.Status == "Blocked"))
                })
                .FirstOrDefaultAsync(ct);

            if (accountAgg != null)
            {
                connectedAccounts = Math.Max(connectedAccounts, accountAgg.Total);
                needAttention = Math.Max(needAttention, accountAgg.NeedAttention);
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
            ActiveAdsCount: activeAds,
            BlockedAdsCount: blockedAds,
            TotalBalance: balances.Sum(b => b.TotalBalance),
            HourlyActivity: AggregateHourly(statsList),
            WeeklyByDayActivity: AggregateWeekly(statsList),
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
                w.OfficeId,
                OfficeName = w.Office.Name
            })
            .ToListAsync(ct);

        var workerIds = workers.Select(w => w.Id).ToList();
        var accountCounts = await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => workerIds.Contains(x.WorkerId))
            .GroupBy(x => x.WorkerId)
            .Select(g => new { WorkerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WorkerId, x => x.Count, ct);

        // Real per-worker today counts from CandidateResponses
        var workerTodayStats = await ComputeWorkerTodayStatsAsync(workerIds, todayStart, ct);

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

            var today = workerTodayStats.TryGetValue(w.Id, out var s) ? s : (Total: 0, Errors: 0);
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
                accountCounts.GetValueOrDefault(w.Id),
                today.Total,   // real TotalToday from responses
                today.Errors,  // real Errors (incl. ActionRequired)
                updateAvailable,
                latestReleaseVersion,
                w.OfficeId,
                w.OfficeName);
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
            worker.AdsPowerApiKey);
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

        return await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new WorkerAccountDto(
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
                x.AdsPowerProfileId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkerEventListItem>> GetWorkerEventsAsync(
        OfficeScope scope,
        Guid? workerId,
        Guid? officeFilter,
        int limit,
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

        return await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(limit)
            .Join(
                db.Workers.AsNoTracking(),
                evt => evt.WorkerId,
                worker => worker.Id,
                (evt, worker) => new WorkerEventListItem(
                    evt.Id,
                    evt.WorkerId,
                    worker.DisplayName,
                    evt.AccountId,
                    evt.Level,
                    evt.Message,
                    evt.Details,
                    evt.CreatedAtUtc))
            .ToListAsync(ct);
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

    // Per-worker today stats for list view (TotalToday + Errors for WorkerListItem)
    private async Task<Dictionary<Guid, (int Total, int Errors)>> ComputeWorkerTodayStatsAsync(
        IReadOnlyList<Guid> workerIds, DateTime todayStartUtc, CancellationToken ct)
    {
        var result = new Dictionary<Guid, (int Total, int Errors)>();
        if (workerIds.Count == 0) return result;

        var idSet = workerIds.ToHashSet();
        var rows = await db.CandidateResponses.AsNoTracking()
            .Where(x => idSet.Contains(x.WorkerId) && x.CreatedAt >= todayStartUtc)
            .GroupBy(x => x.WorkerId)
            .Select(g => new
            {
                WorkerId = g.Key,
                Total = g.Count(),
                Errors = g.Count(x => x.Status == ResponseStatuses.Error || x.Status == ResponseStatuses.ActionRequired)
            })
            .ToListAsync(ct);

        foreach (var r in rows)
            result[r.WorkerId] = (Total: r.Total, Errors: r.Errors);

        // Ensure all requested workers have entry
        foreach (var wid in workerIds)
            if (!result.ContainsKey(wid)) result[wid] = (Total: 0, Errors: 0);

        return result;
    }
}