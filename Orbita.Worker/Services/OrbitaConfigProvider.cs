using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class OrbitaConfigProvider(OrbitaApiClient apiClient, IMonitoringRepository repository) : IWorkerConfigProvider
{
    private WorkerConfigDto? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task<WorkerMonitoringConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (_cached is null || DateTime.UtcNow - _cachedAtUtc > CacheTtl)
        {
            _cached = await apiClient.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            _cachedAtUtc = DateTime.UtcNow;
        }

        if (_cached is null)
        {
            return new WorkerMonitoringConfig();
        }

        var defaultBaseUrl = string.IsNullOrWhiteSpace(_cached.AdsPowerApiBaseUrl)
            ? "http://local.adspower.net:50325"
            : _cached.AdsPowerApiBaseUrl;

        var persisted = (await repository.GetAccountsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(x => x.Id);

        var accounts = _cached.Accounts
            .Where(a => a.IsEnabled)
            .Select(a =>
            {
                var account = new AvitoAccount
                {
                    Id = a.AccountId,
                    DisplayName = a.DisplayName,
                    IsEnabled = true,
                    ProfileProvider = AvitoProfileProvider.AdsPower,
                    AdsPowerProfileId = a.AdsPowerProfileId,
                    AdsPowerProfileName = a.DisplayName,
                    AdsPowerApiBaseUrl = string.IsNullOrWhiteSpace(a.AdsPowerApiBaseUrl) ? defaultBaseUrl : a.AdsPowerApiBaseUrl,
                    AdsPowerApiKey = a.AdsPowerApiKey ?? _cached.AdsPowerApiKey
                };

                if (persisted.TryGetValue(a.AccountId, out var saved))
                {
                    MergeRuntimeState(account, saved);
                }

                if (a.SubProfilesRefreshRequestedAtUtc is not null
                    && (account.SubProfilesRefreshedAt is null
                        || a.SubProfilesRefreshRequestedAtUtc > account.SubProfilesRefreshedAt))
                {
                    account.ForceSubProfilesRefresh = true;
                }

                if (a.DisabledSubProfileIds is { Count: > 0 })
                {
                    account.DisabledSubProfileIds = a.DisabledSubProfileIds
                        .ToHashSet(StringComparer.Ordinal);
                }

                return account;
            })
            .ToList();

        return new WorkerMonitoringConfig
        {
            MaxConcurrentAccounts = _cached.MaxConcurrentAccounts,
            Accounts = accounts
        };
    }

    public void InvalidateCache() => _cached = null;

    private static void MergeRuntimeState(AvitoAccount target, AvitoAccount source)
    {
        target.Status = source.Status;
        target.LastErrorMessage = source.LastErrorMessage;
        target.LastMonitoringAt = source.LastMonitoringAt;
        target.LastAuthCheckAt = source.LastAuthCheckAt;
        target.ActiveAdsCount = source.ActiveAdsCount;
        target.BlockedCount = source.BlockedCount;
        target.DraftsCount = source.DraftsCount;
        target.AdsStatsUpdatedAt = source.AdsStatsUpdatedAt;
        target.SubProfilesJson = source.SubProfilesJson;
        target.SubProfilesRefreshedAt = source.SubProfilesRefreshedAt;
    }
}