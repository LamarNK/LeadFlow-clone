using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerTelemetryCollector(
    WorkerAccountRuntimeStore runtimeStore,
    OrbitaApiClient apiClient)
{
    public async Task<WorkerSnapshotRequest?> BuildSnapshotAsync(
        WorkerConfigDto config,
        CancellationToken ct)
    {
        if (config.Accounts.Count == 0)
        {
            return null;
        }

        var localAccounts = runtimeStore.GetAll().ToDictionary(x => x.Id);
        var configByAccountId = config.Accounts.ToDictionary(x => x.AccountId);

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
                configByAccountId.TryGetValue(a.AccountId, out var cfg);
                return MapBalance(a, local, cfg);
            })
            .ToList();

        var statsDto = await ResolveStatsAsync(ct).ConfigureAwait(false);
        var stats = MapStats(statsDto, accounts);

        return new WorkerSnapshotRequest(
            config.WorkerId,
            DateTime.UtcNow,
            stats,
            accounts,
            balances);
    }

    private async Task<DashboardStatsDto> ResolveStatsAsync(CancellationToken ct)
    {
        var remote = await apiClient.GetMonitoringStatsAsync(ct).ConfigureAwait(false);
        if (remote is not null)
        {
            return remote.Stats;
        }

        return new DashboardStatsDto(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);
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
            local?.ActiveAdsCount ?? cfg.ActiveAdsCount,
            local?.BlockedCount ?? cfg.BlockedCount,
            local?.DraftsCount ?? cfg.DraftsCount,
            string.IsNullOrWhiteSpace(local?.LastErrorMessage) ? null : local.LastErrorMessage.Trim(),
            EnsureUtc(local?.LastMonitoringAt ?? cfg.LastMonitoringAtUtc),
            cfg.IsEnabled,
            cfg.AdsPowerProfileId,
            MapSubProfiles(local, cfg),
            EnsureUtc(local?.SubProfilesRefreshedAt ?? cfg.SubProfilesRefreshedAtUtc),
            SubProfilesRefreshRequestedAtUtc: cfg.SubProfilesRefreshRequestedAtUtc);
    }

    private static WorkerBalanceDto MapBalance(
        WorkerAccountDto account,
        AvitoAccount? local,
        WorkerAccountConfigDto? cfg)
    {
        var subProfiles = ResolveBalanceSubProfiles(account, local, cfg);
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
                sp.Balance,
                sp.WalletBalance,
                string.IsNullOrWhiteSpace(sp.AdvanceDurationText) ? null : sp.AdvanceDurationText))
            .ToList();

        return new WorkerBalanceDto(
            account.AccountId,
            account.DisplayName,
            balanceItems.Sum(x => x.Balance ?? 0m),
            balanceItems,
            balanceItems.Sum(x => x.WalletBalance ?? 0m));
    }

    private static IReadOnlyList<WorkerSubProfileDto> ResolveBalanceSubProfiles(
        WorkerAccountDto account,
        AvitoAccount? local,
        WorkerAccountConfigDto? cfg)
    {
        var current = account.SubProfiles ?? [];
        if (current.Any(static sp => sp.Balance.HasValue || sp.WalletBalance.HasValue))
        {
            return current;
        }

        var persisted = MapSubProfiles(null, cfg) ?? [];
        if (persisted.Count == 0)
        {
            return current;
        }

        if (current.Count == 0)
        {
            return persisted;
        }

        var persistedById = persisted.ToDictionary(
            static sp => sp.Id,
            static sp => sp,
            StringComparer.Ordinal);
        return current
            .Select(sp =>
            {
                if (!persistedById.TryGetValue(sp.Id, out var stored))
                {
                    return sp;
                }

                return sp with
                {
                    Balance = sp.Balance ?? stored.Balance,
                    WalletBalance = sp.WalletBalance ?? stored.WalletBalance,
                    AdvanceDurationText = string.IsNullOrWhiteSpace(sp.AdvanceDurationText)
                        ? stored.AdvanceDurationText
                        : sp.AdvanceDurationText
                };
            })
            .ToList();
    }

    private static IReadOnlyList<WorkerSubProfileDto>? MapSubProfiles(AvitoAccount? local, WorkerAccountConfigDto cfg)
    {
        if (local is { SubProfiles.Count: > 0 })
        {
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
                    DiagnosticAttachmentId: sp.LastDiagnosticAttachmentId,
                    WalletBalance: sp.WalletBalance,
                    AdvanceDurationText: string.IsNullOrWhiteSpace(sp.AdvanceDurationText) ? null : sp.AdvanceDurationText,
                    Rating: sp.Rating,
                    ReviewsCount: sp.ReviewsCount,
                    ReviewsText: string.IsNullOrWhiteSpace(sp.ReviewsText) ? null : sp.ReviewsText))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(cfg.SubProfilesJson) || cfg.SubProfilesJson == "[]")
        {
            return null;
        }

        try
        {
            var profiles = System.Text.Json.JsonSerializer.Deserialize<List<AvitoSubProfile>>(cfg.SubProfilesJson);
            if (profiles is not { Count: > 0 })
            {
                return null;
            }

            return profiles
                .Select(sp => new WorkerSubProfileDto(
                    sp.Id,
                    sp.Name,
                    sp.Category,
                    sp.IsCurrent,
                    sp.Balance,
                    string.IsNullOrWhiteSpace(sp.LastIssueKind) ? null : sp.LastIssueKind,
                    string.IsNullOrWhiteSpace(sp.LastIssueMessage) ? null : sp.LastIssueMessage,
                    sp.LastIssueAt,
                    WalletBalance: sp.WalletBalance,
                    AdvanceDurationText: string.IsNullOrWhiteSpace(sp.AdvanceDurationText) ? null : sp.AdvanceDurationText,
                    Rating: sp.Rating,
                    ReviewsCount: sp.ReviewsCount,
                    ReviewsText: string.IsNullOrWhiteSpace(sp.ReviewsText) ? null : sp.ReviewsText))
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

    private static DashboardStatsDto MapStats(DashboardStatsDto stats, IReadOnlyList<WorkerAccountDto> accounts)
    {
        var hourly = stats.HourlyActivity?.Count > 0
            ? stats.HourlyActivity
            : Enumerable.Range(0, 24)
                .Select(h => new ActivityPointDto($"{h:00}:00", 0, 0, 0, 0, h, 1, null))
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
            stats.ActiveAdsCount > 0 ? stats.ActiveAdsCount : accounts.Sum(a => a.ActiveAdsCount),
            stats.BlockedAdsCount > 0 ? stats.BlockedAdsCount : accounts.Sum(a => a.BlockedCount),
            stats.DraftsCount > 0 ? stats.DraftsCount : accounts.Sum(a => a.DraftsCount),
            hourly,
            stats.WeeklyByDayActivity ?? []);
    }
}