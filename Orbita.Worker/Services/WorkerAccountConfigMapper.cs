using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

internal static class WorkerAccountConfigMapper
{
    public static AvitoAccount ToAccount(
        WorkerAccountConfigDto dto,
        WorkerConfigDto config,
        string defaultBaseUrl)
    {
        var account = new AvitoAccount
        {
            Id = dto.AccountId,
            DisplayName = dto.DisplayName,
            IsEnabled = dto.IsEnabled,
            ProfileProvider = AvitoProfileProvider.AdsPower,
            AdsPowerProfileId = dto.AdsPowerProfileId,
            AdsPowerProfileName = dto.DisplayName,
            AdsPowerApiBaseUrl = string.IsNullOrWhiteSpace(dto.AdsPowerApiBaseUrl) ? defaultBaseUrl : dto.AdsPowerApiBaseUrl,
            AdsPowerApiKey = dto.AdsPowerApiKey ?? config.AdsPowerApiKey,
            Status = WorkerAccountStatusMapper.ResolveRuntimeStatus(dto.IsEnabled, dto.Status),
            LastErrorMessage = dto.LastErrorMessage ?? string.Empty,
            LastMonitoringAt = dto.LastMonitoringAtUtc,
            LastAuthCheckAt = dto.LastAuthCheckAtUtc,
            ActiveAdsCount = dto.ActiveAdsCount,
            BlockedCount = dto.BlockedCount,
            DraftsCount = dto.DraftsCount,
            SubProfilesJson = string.IsNullOrWhiteSpace(dto.SubProfilesJson) ? "[]" : dto.SubProfilesJson
        };

        if (!string.IsNullOrWhiteSpace(dto.SubProfilesJson))
        {
            try
            {
                var profiles = System.Text.Json.JsonSerializer.Deserialize<List<AvitoSubProfile>>(
                    dto.SubProfilesJson,
                    WorkerSubProfileJsonOptions.Deserialize);
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
}