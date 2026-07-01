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

        var balances = accounts
            .Select(a =>
            {
                localAccounts.TryGetValue(a.AccountId, out var local);
                return MapBalance(a, local);
            })
            .ToList();

        var stats = await repository.GetDashboardStatsAsync(ct).ConfigureAwait(false);
        var statsDto = MapStats(stats, accounts);

        return new WorkerSnapshotRequest(
            config.WorkerId,
            DateTime.UtcNow,
            statsDto,
            accounts,
            balances);
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
            EnsureUtc(local?.LastMonitoringAt),
            cfg.IsEnabled,
            cfg.AdsPowerProfileId,
            MapSubProfiles(local),
            EnsureUtc(local?.SubProfilesRefreshedAt),
            SubProfilesRefreshRequestedAtUtc: null);
    }

    private static WorkerBalanceDto MapBalance(WorkerAccountDto account, AvitoAccount? local)
    {
        var subProfiles = account.SubProfiles ?? [];
        if (subProfiles.Count == 0)
        {
            return new WorkerBalanceDto(
                account.AccountId,
                account.DisplayName,
                0,
                [new SubProfileBalanceDto("—", null)]);
        }

        var balanceItems = subProfiles
            .Select(sp => new SubProfileBalanceDto(
                string.IsNullOrWhiteSpace(sp.Name) ? sp.Id : sp.Name,
                sp.Balance))
            .ToList();

        return new WorkerBalanceDto(
            account.AccountId,
            account.DisplayName,
            balanceItems.Sum(x => x.Balance ?? 0m),
            balanceItems);
    }

    private static IReadOnlyList<WorkerSubProfileDto>? MapSubProfiles(AvitoAccount? local)
    {
        if (local is null || local.SubProfiles.Count == 0)
        {
            return null;
        }

        return local.SubProfiles
            .Select(sp => new WorkerSubProfileDto(
                sp.Id,
                sp.Name,
                sp.Category,
                sp.IsCurrent,
                sp.Balance,
                string.IsNullOrWhiteSpace(sp.LastIssueKind) ? null : sp.LastIssueKind,
                string.IsNullOrWhiteSpace(sp.LastIssueMessage) ? null : sp.LastIssueMessage,
                sp.LastIssueAt,
                DiagnosticAttachmentId: sp.LastDiagnosticAttachmentId))
            .ToList();
    }

    private static DateTime? EnsureUtc(DateTime? value) =>
        value is null
            ? null
            : value.Value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            };

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