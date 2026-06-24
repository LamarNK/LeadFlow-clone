using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class DashboardQueryService(OrbitaDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GlobalDashboardSummary> GetGlobalSummaryAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var workers = await db.Workers.AsNoTracking().ToListAsync(ct);
        var onlineWorkers = workers.Count(w => WorkerOnlineRules.IsOnline(w.LastSeenAtUtc, nowUtc));

        var latestSnapshots = await db.WorkerSnapshots
            .AsNoTracking()
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

        return new GlobalDashboardSummary(
            TotalWorkers: workers.Count,
            OnlineWorkers: onlineWorkers,
            TotalToday: statsList.Sum(s => s.TotalToday),
            SentToCrm: statsList.Sum(s => s.SentToCrm),
            InProgress: statsList.Sum(s => s.InProgress),
            Duplicates: statsList.Sum(s => s.Duplicates),
            Errors: statsList.Sum(s => s.Errors),
            ActionRequired: statsList.Sum(s => s.ActionRequired),
            ConnectedAccounts: statsList.Sum(s => s.ConnectedAccounts),
            RequiresAuthorization: statsList.Sum(s => s.RequiresAuthorization),
            AccountsNeedAttentionCount: statsList.Sum(s => s.AccountsNeedAttentionCount),
            ActiveAdsCount: statsList.Sum(s => s.ActiveAdsCount),
            BlockedAdsCount: statsList.Sum(s => s.BlockedAdsCount),
            TotalBalance: balances.Sum(b => b.TotalBalance),
            HourlyActivity: AggregateHourly(statsList),
            WeeklyByDayActivity: AggregateWeekly(statsList),
            AggregatedAtUtc: nowUtc);
    }

    public async Task<IReadOnlyList<WorkerListItem>> GetWorkersAsync(CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        var workers = await db.Workers.AsNoTracking().OrderBy(x => x.DisplayName).ToListAsync(ct);
        var accountCounts = await db.WorkerAccounts
            .AsNoTracking()
            .GroupBy(x => x.WorkerId)
            .Select(g => new { WorkerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.WorkerId, x => x.Count, ct);

        var latestStats = await db.WorkerSnapshots
            .AsNoTracking()
            .GroupBy(x => x.WorkerId)
            .Select(g => g.OrderByDescending(x => x.CapturedAtUtc).First())
            .ToDictionaryAsync(x => x.WorkerId, x => x.StatsJson, ct);

        return workers.Select(w =>
        {
            DashboardStatsDto? stats = null;
            if (latestStats.TryGetValue(w.Id, out var json))
            {
                stats = JsonSerializer.Deserialize<DashboardStatsDto>(json, JsonOptions);
            }

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
                stats?.TotalToday ?? 0,
                stats?.Errors ?? 0);
        }).ToList();
    }

    public async Task<WorkerDetail?> GetWorkerDetailAsync(Guid workerId, CancellationToken ct)
    {
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
            balances);
    }

    public async Task<IReadOnlyList<WorkerAccountDto>> GetWorkerAccountsAsync(Guid workerId, CancellationToken ct)
    {
        return await db.WorkerAccounts
            .AsNoTracking()
            .Where(x => x.WorkerId == workerId)
            .OrderBy(x => x.DisplayName)
            .Select(x => new WorkerAccountDto(
                x.AccountId,
                x.DisplayName,
                x.Status,
                x.IsEnabled,
                x.ActiveAdsCount,
                x.BlockedCount,
                x.DraftsCount,
                x.LastErrorMessage,
                x.LastMonitoringAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkerEventListItem>> GetWorkerEventsAsync(Guid? workerId, int limit, CancellationToken ct)
    {
        var query = db.WorkerEvents.AsNoTracking();
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
}