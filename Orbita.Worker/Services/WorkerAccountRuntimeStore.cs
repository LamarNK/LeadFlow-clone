using System.Collections.Concurrent;
using LeadFlow.Core.Models;

namespace Orbita.Worker.Services;

/// <summary>
/// In-memory runtime state for worker accounts (Orbita is long-term source; this holds the active cycle).
/// </summary>
public sealed class WorkerAccountRuntimeStore
{
    private readonly ConcurrentDictionary<Guid, AvitoAccount> _accounts = new();

    public void Upsert(AvitoAccount account) =>
        _accounts[account.Id] = Clone(account);

    public bool TryGet(Guid accountId, out AvitoAccount? account)
    {
        if (_accounts.TryGetValue(accountId, out var stored))
        {
            account = Clone(stored);
            return true;
        }

        account = null;
        return false;
    }

    public IReadOnlyList<AvitoAccount> GetAll() =>
        _accounts.Values.Select(Clone).ToList();

    public void OverlayRuntime(AvitoAccount target)
    {
        if (_accounts.TryGetValue(target.Id, out var stored))
        {
            ApplyRuntimeFields(target, stored);
        }
    }

    private static void ApplyRuntimeFields(AvitoAccount target, AvitoAccount source)
    {
        if (!(target.IsEnabled && source.Status == AvitoAccountStatus.Paused))
        {
            target.Status = source.Status;
        }
        target.LastErrorMessage = source.LastErrorMessage;
        target.LastMonitoringAt = source.LastMonitoringAt;
        target.LastAuthCheckAt = source.LastAuthCheckAt;
        target.ActiveAdsCount = source.ActiveAdsCount;
        target.BlockedCount = source.BlockedCount;
        target.DraftsCount = source.DraftsCount;
        target.AdsStatsUpdatedAt = source.AdsStatsUpdatedAt;
        target.SubProfilesJson = source.SubProfilesJson;
        target.SubProfilesRefreshedAt = source.SubProfilesRefreshedAt;
        target.ActiveAdsSnapshotJson = source.ActiveAdsSnapshotJson;
        target.BlockedAdsSnapshotJson = source.BlockedAdsSnapshotJson;
        target.ForceSubProfilesRefresh = source.ForceSubProfilesRefresh;
        // AvitoLogin/AvitoPassword always come from panel config (target), not runtime store.
        if (source.SubProfiles.Count > 0)
        {
            target.SetSubProfiles(source.SubProfiles);
        }
    }

    private static AvitoAccount Clone(AvitoAccount source)
    {
        var clone = new AvitoAccount
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            IsEnabled = source.IsEnabled,
            ProfileProvider = source.ProfileProvider,
            AdsPowerProfileId = source.AdsPowerProfileId,
            AdsPowerProfileName = source.AdsPowerProfileName,
            AdsPowerApiBaseUrl = source.AdsPowerApiBaseUrl,
            AdsPowerApiKey = source.AdsPowerApiKey,
            AvitoLogin = source.AvitoLogin,
            AvitoPassword = source.AvitoPassword,
            Status = source.Status,
            LastErrorMessage = source.LastErrorMessage,
            LastMonitoringAt = source.LastMonitoringAt,
            LastAuthCheckAt = source.LastAuthCheckAt,
            ActiveAdsCount = source.ActiveAdsCount,
            BlockedCount = source.BlockedCount,
            DraftsCount = source.DraftsCount,
            AdsStatsUpdatedAt = source.AdsStatsUpdatedAt,
            SubProfilesJson = source.SubProfilesJson,
            SubProfilesRefreshedAt = source.SubProfilesRefreshedAt,
            ActiveAdsSnapshotJson = source.ActiveAdsSnapshotJson,
            BlockedAdsSnapshotJson = source.BlockedAdsSnapshotJson,
            ForceSubProfilesRefresh = source.ForceSubProfilesRefresh,
            DisabledSubProfileIds = source.DisabledSubProfileIds.ToHashSet(StringComparer.Ordinal)
        };
        if (source.SubProfiles.Count > 0)
        {
            clone.SetSubProfiles(source.SubProfiles);
        }

        return clone;
    }
}