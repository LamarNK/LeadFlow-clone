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
    private readonly ConcurrentDictionary<Guid, ResumeEntry> _resume = LoadResume();

    public void Upsert(AvitoAccount account)
    {
        var clone = Clone(account);
        var nextEntry = ResumeEntry.FromAccount(clone);
        if (_resume.TryGetValue(account.Id, out var prev)
            && nextEntry.NextUtc is null
            && prev.NextUtc is not null)
        {
            // Snapshot без NextMonitoringAtUtc не должен затирать сохранённую паузу.
            nextEntry.NextUtc = prev.NextUtc;
            clone.NextMonitoringAtUtc = prev.NextUtc;
        }

        _accounts[account.Id] = clone;
        if (!_resume.TryGetValue(account.Id, out prev) || !prev.SameAs(nextEntry))
        {
            _resume[account.Id] = nextEntry;
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

        if (_resume.TryGetValue(target.Id, out var resumed))
        {
            resumed.ApplyTo(target);
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
        target.NextMonitoringAtUtc = LaterUtc(source.NextMonitoringAtUtc, target.NextMonitoringAtUtc);
        target.MonitoringPassStartedAtUtc = source.MonitoringPassStartedAtUtc ?? target.MonitoringPassStartedAtUtc;
        target.MonitoringPassFinishedAtUtc = source.MonitoringPassFinishedAtUtc ?? target.MonitoringPassFinishedAtUtc;
        if (source.MonitoringPassCompletedSubIds.Count > 0)
        {
            target.MonitoringPassCompletedSubIds = new HashSet<string>(
                source.MonitoringPassCompletedSubIds,
                StringComparer.Ordinal);
        }
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
        target.LocalTrafficLastStats = source.LocalTrafficLastStats;
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
            MultiloginProfileId = source.MultiloginProfileId,
            MultiloginProfileName = source.MultiloginProfileName,
            MultiloginFolderId = source.MultiloginFolderId,
            MultiloginLauncherUrl = source.MultiloginLauncherUrl,
            MultiloginCloudApiUrl = source.MultiloginCloudApiUrl,
            MultiloginAutomationToken = source.MultiloginAutomationToken,
            BrowserProfilePath = source.BrowserProfilePath,
            LocalChromeExecutablePath = source.LocalChromeExecutablePath,
            AvitoLogin = source.AvitoLogin,
            AvitoPassword = source.AvitoPassword,
            AvitoCredentialsError = source.AvitoCredentialsError,
            LocalTrafficMode = source.LocalTrafficMode,
            LocalBlockMedia = source.LocalBlockMedia,
            LocalBlockAnalytics = source.LocalBlockAnalytics,
            LocalBlockImages = source.LocalBlockImages,
            LocalBlockFonts = source.LocalBlockFonts,
            LocalBlockPrefetch = source.LocalBlockPrefetch,
            LocalNavigationTimeoutSeconds = source.LocalNavigationTimeoutSeconds,
            LocalTrafficLastStats = source.LocalTrafficLastStats,
            Status = source.Status,
            LastErrorMessage = source.LastErrorMessage,
            LastMonitoringAt = source.LastMonitoringAt,
            NextMonitoringAtUtc = source.NextMonitoringAtUtc,
            MonitoringPassStartedAtUtc = source.MonitoringPassStartedAtUtc,
            MonitoringPassFinishedAtUtc = source.MonitoringPassFinishedAtUtc,
            MonitoringPassCompletedSubIds = new HashSet<string>(
                source.MonitoringPassCompletedSubIds,
                StringComparer.Ordinal),
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

    private static ConcurrentDictionary<Guid, ResumeEntry> LoadResume()
    {
        var map = new ConcurrentDictionary<Guid, ResumeEntry>();
        try
        {
            if (!File.Exists(ResumePath))
            {
                return map;
            }

            var json = File.ReadAllText(ResumePath);
            var file = JsonSerializer.Deserialize<AccountResumeFileDto>(json, ResumeJson);
            if (file?.Accounts is { Count: > 0 })
            {
                foreach (var kv in file.Accounts)
                {
                    if (Guid.TryParse(kv.Key, out var id) && kv.Value is not null)
                    {
                        map[id] = ResumeEntry.FromDto(kv.Value);
                    }
                }

                return map;
            }

            var legacy = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(json, ResumeJson);
            if (legacy is null)
            {
                return map;
            }

            foreach (var kv in legacy)
            {
                if (!Guid.TryParse(kv.Key, out var id))
                {
                    continue;
                }

                map[id] = new ResumeEntry { NextUtc = AsUtc(kv.Value) };
            }
        }
        catch
        {
            // Пустой resume — проход начнётся как раньше.
        }

        return map;
    }

    private void SaveResume()
    {
        try
        {
            Directory.CreateDirectory(WorkerConfigStore.ConfigDirectory);
            var file = new AccountResumeFileDto
            {
                Accounts = _resume.ToDictionary(
                    static kv => kv.Key.ToString("D"),
                    static kv => kv.Value.ToDto())
            };
            File.WriteAllText(ResumePath, JsonSerializer.Serialize(file, ResumeJson));
        }
        catch
        {
            // Пауза остаётся в памяти процесса.
        }
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static DateTime? AsUtc(DateTime? value) =>
        value is { } at ? AsUtc(at) : null;

    private static DateTime? LaterUtc(DateTime? left, DateTime? right)
    {
        var a = AsUtc(left);
        var b = AsUtc(right);
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return a >= b ? a : b;
    }

    private sealed class ResumeEntry
    {
        public DateTime? NextUtc { get; set; }
        public DateTime? PassStartedAtUtc { get; set; }
        public DateTime? PassFinishedAtUtc { get; set; }
        public HashSet<string> CompletedSubIds { get; set; } = new(StringComparer.Ordinal);

        public static ResumeEntry FromAccount(AvitoAccount account) => new()
        {
            NextUtc = account.NextMonitoringAtUtc,
            PassStartedAtUtc = account.MonitoringPassStartedAtUtc,
            PassFinishedAtUtc = account.MonitoringPassFinishedAtUtc,
            CompletedSubIds = new HashSet<string>(account.MonitoringPassCompletedSubIds, StringComparer.Ordinal)
        };

        public static ResumeEntry FromDto(AccountResumeEntryDto dto) => new()
        {
            NextUtc = AsUtc(dto.NextUtc),
            PassStartedAtUtc = AsUtc(dto.PassStartedAtUtc),
            PassFinishedAtUtc = AsUtc(dto.PassFinishedAtUtc),
            CompletedSubIds = new HashSet<string>(
                dto.CompletedSubIds ?? [],
                StringComparer.Ordinal)
        };

        public AccountResumeEntryDto ToDto() => new()
        {
            NextUtc = NextUtc,
            PassStartedAtUtc = PassStartedAtUtc,
            PassFinishedAtUtc = PassFinishedAtUtc,
            CompletedSubIds = CompletedSubIds.Count == 0 ? null : CompletedSubIds.ToList()
        };

        public void ApplyTo(AvitoAccount target)
        {
            target.NextMonitoringAtUtc = LaterUtc(target.NextMonitoringAtUtc, NextUtc);
            target.MonitoringPassStartedAtUtc ??= PassStartedAtUtc;
            target.MonitoringPassFinishedAtUtc ??= PassFinishedAtUtc;
            if (target.MonitoringPassCompletedSubIds.Count == 0 && CompletedSubIds.Count > 0)
            {
                target.MonitoringPassCompletedSubIds = new HashSet<string>(CompletedSubIds, StringComparer.Ordinal);
            }
        }

        public bool SameAs(ResumeEntry other) =>
            NextUtc == other.NextUtc
            && PassStartedAtUtc == other.PassStartedAtUtc
            && PassFinishedAtUtc == other.PassFinishedAtUtc
            && CompletedSubIds.SetEquals(other.CompletedSubIds);
    }

    private sealed class AccountResumeFileDto
    {
        public Dictionary<string, AccountResumeEntryDto>? Accounts { get; set; }
    }

    private sealed class AccountResumeEntryDto
    {
        public DateTime? NextUtc { get; set; }
        public DateTime? PassStartedAtUtc { get; set; }
        public DateTime? PassFinishedAtUtc { get; set; }
        public List<string>? CompletedSubIds { get; set; }
    }
}
