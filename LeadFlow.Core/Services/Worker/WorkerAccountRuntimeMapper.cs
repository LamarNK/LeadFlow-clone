using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Multilogin;
using Orbita.Contracts;

namespace LeadFlow.Core.Services.Worker;

public static class WorkerAccountRuntimeMapper
{
    private static readonly System.Text.Json.JsonSerializerOptions SubProfileJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public static AvitoAccount ToAccount(
        WorkerAccountConfigDto dto,
        WorkerConfigDto config,
        string defaultBaseUrl)
    {
        var provider = ResolveProvider(dto);
        var account = new AvitoAccount
        {
            Id = dto.AccountId,
            DisplayName = dto.DisplayName,
            IsEnabled = dto.IsEnabled,
            ProfileProvider = provider,
            AdsPowerProfileId = dto.AdsPowerProfileId,
            AdsPowerProfileName = dto.DisplayName,
            AdsPowerApiBaseUrl = string.IsNullOrWhiteSpace(dto.AdsPowerApiBaseUrl) ? defaultBaseUrl : dto.AdsPowerApiBaseUrl,
            AdsPowerApiKey = dto.AdsPowerApiKey ?? config.AdsPowerApiKey,
            MultiloginProfileId = NullIfWhiteSpace(dto.MultiloginProfileId),
            MultiloginFolderId = NullIfWhiteSpace(dto.MultiloginFolderId),
            MultiloginProfileName = provider == AvitoProfileProvider.Multilogin
                ? NullIfWhiteSpace(dto.DisplayName)
                : null,
            MultiloginLauncherUrl = MultiloginUrl.Normalize(config.MultiloginLauncherUrl),
            MultiloginCloudApiUrl = MultiloginUrl.Normalize(config.MultiloginCloudApiUrl),
            MultiloginAutomationToken = NullIfWhiteSpace(config.MultiloginAutomationToken),
            Status = WorkerAccountStatusMapper.ResolveRuntimeStatus(dto.IsEnabled, dto.Status),
            LastErrorMessage = dto.LastErrorMessage ?? string.Empty,
            LastMonitoringAt = dto.LastMonitoringAtUtc,
            LastAuthCheckAt = dto.LastAuthCheckAtUtc,
            ActiveAdsCount = dto.ActiveAdsCount,
            BlockedCount = dto.BlockedCount,
            DraftsCount = dto.DraftsCount,
            SubProfilesJson = string.IsNullOrWhiteSpace(dto.SubProfilesJson) ? "[]" : dto.SubProfilesJson,
            AvitoLogin = string.IsNullOrWhiteSpace(dto.AvitoLogin) ? null : dto.AvitoLogin.Trim(),
            AvitoPassword = string.IsNullOrEmpty(dto.AvitoPassword) ? null : dto.AvitoPassword
        };

        if (!string.IsNullOrWhiteSpace(dto.SubProfilesJson))
        {
            try
            {
                var profiles = System.Text.Json.JsonSerializer.Deserialize<List<AvitoSubProfile>>(
                    dto.SubProfilesJson,
                    SubProfileJson);
                if (profiles is { Count: > 0 })
                {
                    account.SetSubProfiles(profiles);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // ignore malformed JSON from API
            }
        }

        account.SubProfilesRefreshedAt = dto.SubProfilesRefreshedAtUtc;

        if (dto.SubProfilesRefreshRequestedAtUtc is not null
            && (account.SubProfilesRefreshedAt is null
                || dto.SubProfilesRefreshRequestedAtUtc > account.SubProfilesRefreshedAt))
        {
            account.ForceSubProfilesRefresh = true;
        }

        if (dto.DisabledSubProfileIds is { Count: > 0 })
        {
            account.DisabledSubProfileIds = dto.DisabledSubProfileIds.ToHashSet(StringComparer.Ordinal);
        }

        return account;
    }

    internal static AvitoProfileProvider ResolveProvider(WorkerAccountConfigDto dto)
    {
        if (Enum.TryParse<AvitoProfileProvider>(dto.ProfileProvider, out var parsed)
            && parsed == AvitoProfileProvider.Multilogin)
        {
            return AvitoProfileProvider.Multilogin;
        }

        if (!string.IsNullOrWhiteSpace(dto.MultiloginProfileId))
        {
            return AvitoProfileProvider.Multilogin;
        }

        return AvitoProfileProvider.AdsPower;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
