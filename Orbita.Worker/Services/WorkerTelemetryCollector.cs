using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerTelemetryCollector(AppRepository repository)
{
    public async Task<WorkerSnapshotRequest?> BuildSnapshotAsync(
        WorkerConfigDto config,
        CancellationToken ct)
    {
        if (config.Accounts.Count == 0)
        {
            return null;
        }

        var localAccounts = (await repository.GetAccountsAsync(ct).ConfigureAwait(false))
            .ToDictionary(x => x.Id);

        var accounts = config.Accounts
            .Select(cfg =>
            {
                localAccounts.TryGetValue(cfg.AccountId, out var local);
                return MapAccount(cfg, local);
            })
            .ToList();

        var stats = await repository.GetDashboardStatsAsync(ct).ConfigureAwait(false);
        var statsDto = MapStats(stats, accounts);

        return new WorkerSnapshotRequest(
            config.WorkerId,
            DateTime.UtcNow,
            statsDto,
            accounts,
            []);
    }

    private static WorkerAccountDto MapAccount(WorkerAccountConfigDto cfg, AvitoAccount? local)
    {
        var status = local is null
            ? (cfg.IsEnabled ? "Active" : "Paused")
            : WorkerAccountStatusMapper.ToSnapshotStatus(local.Status, cfg.IsEnabled);

        return new WorkerAccountDto(
            cfg.AccountId,
            cfg.DisplayName,
            status,
            cfg.IsEnabled,
            local?.ActiveAdsCount ?? 0,
            local?.BlockedCount ?? 0,
            local?.DraftsCount ?? 0,
            string.IsNullOrWhiteSpace(local?.LastErrorMessage) ? null : local.LastErrorMessage.Trim(),
            local?.LastMonitoringAt,
            cfg.IsEnabled,
            cfg.AdsPowerProfileId);
    }

    private static DashboardStatsDto MapStats(DashboardStats stats, IReadOnlyList<WorkerAccountDto> accounts)
    {
        var hourly = stats.HourlyActivity
            .Select(p => new ActivityPointDto(
                p.Label,
                p.NewCount,
                p.SentCount,
                p.DuplicateCount,
                p.ErrorCount,
                p.SlotStartHour,
                p.SlotSpanHours,
                p.LocalDate))
            .ToList();

        var weekly = stats.WeeklyByDayActivity
            .Select(p => new ActivityPointDto(
                p.Label,
                p.NewCount,
                p.SentCount,
                p.DuplicateCount,
                p.ErrorCount,
                p.SlotStartHour,
                p.SlotSpanHours,
                p.LocalDate))
            .ToList();

        return new DashboardStatsDto(
            stats.NewResponses,
            stats.TotalToday,
            stats.SentToCrm,
            stats.InProgress,
            stats.Duplicates,
            stats.Errors,
            stats.ActionRequired,
            stats.ConnectedAccounts > 0 ? stats.ConnectedAccounts : accounts.Count(a => a.IsEnabled),
            stats.RequiresAuthorization,
            stats.AccountsNeedAttentionCount,
            stats.ActiveAdsCount,
            stats.BlockedAdsCount,
            stats.DraftsCount,
            hourly,
            weekly);
    }
}