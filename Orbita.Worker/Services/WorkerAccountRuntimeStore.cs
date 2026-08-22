using System.Collections.Concurrent;
using System.Text.Json;
using LeadFlow.Core.Models;

namespace Orbita.Worker.Services;

/// <summary>
/// In-memory runtime state for worker accounts (Orbita is long-term source; this holds the active cycle).
/// NextMonitoringAtUtc additionally persisted so a process restart does not re-walk every account.
/// </summary>
public sealed class WorkerAccountRuntimeStore
{
    private static readonly JsonSerializerOptions ResumeJson = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, AvitoAccount> _accounts = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _resumeNextUtc = LoadResume();

    public void Upsert(AvitoAccount account)
    {
        _accounts[account.Id] = Clone(account);
        if (account.NextMonitoringAtUtc is { } next
            && (!_resumeNextUtc.TryGetValue(account.Id, out var prev) || prev != next))
        {
            _resumeNextUtc[account.Id] = next;
            SaveResume();
        }
    }

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

        if (target.NextMonitoringAtUtc is null
            && _resumeNextUtc.TryGetValue(target.Id, out var resumed))
        {
            target.NextMonitoringAtUtc = resumed;
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
        target.NextMonitoringAtUtc = source.NextMonitoringAtUtc ?? target.NextMonitoringAtUtc;
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
            NextMonitoringAtUtc = source.NextMonitoringAtUtc,
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

    private static string ResumePath =>
        Path.Combine(WorkerConfigStore.ConfigDirectory, "account-resume.json");

    private static ConcurrentDictionary<Guid, DateTime> LoadResume()
    {
        try
        {
            if (!File.Exists(ResumePath))
            {
                return new ConcurrentDictionary<Guid, DateTime>();
            }

            var json = File.ReadAllText(ResumePath);
            var dto = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json, ResumeJson);
            var map = new ConcurrentDictionary<Guid, DateTime>();
            if (dto is null)
            {
                return map;
            }

            foreach (var kv in dto)
            {
                if (!Guid.TryParse(kv.Key, out var id))
                {
                    continue;
                }

                var at = kv.Value.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(kv.Value, DateTimeKind.Utc)
                    : kv.Value.ToUniversalTime();
                map[id] = at;
            }

            return map;
        }
        catch
        {
            return new ConcurrentDictionary<Guid, DateTime>();
        }
    }

    private void SaveResume()
    {
        try
        {
            Directory.CreateDirectory(WorkerConfigStore.ConfigDirectory);
            var dto = _resumeNextUtc.ToDictionary(
                static kv => kv.Key.ToString("D"),
                static kv => kv.Value);
            var json = JsonSerializer.Serialize(dto, ResumeJson);
            File.WriteAllText(ResumePath, json);
        }
        catch
        {
            // Пауза остаётся в памяти процесса.
        }
    }
}