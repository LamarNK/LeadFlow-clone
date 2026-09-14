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
            BrowserProfilePath = NullIfWhiteSpace(dto.LocalUserDataDir) ?? string.Empty,
            LocalChromeExecutablePath = NullIfWhiteSpace(config.LocalChromeExecutablePath),
            Status = WorkerAccountStatusMapper.ResolveRuntimeStatus(dto.IsEnabled, dto.Status),
            LastErrorMessage = dto.LastErrorMessage ?? string.Empty,
            LastMonitoringAt = dto.LastMonitoringAtUtc,
            LastAuthCheckAt = dto.LastAuthCheckAtUtc,
            ActiveAdsCount = dto.ActiveAdsCount,
            BlockedCount = dto.BlockedCount,
            DraftsCount = dto.DraftsCount,
            SubProfilesJson = string.IsNullOrWhiteSpace(dto.SubProfilesJson) ? "[]" : dto.SubProfilesJson,
            AvitoLogin = string.IsNullOrWhiteSpace(dto.AvitoLogin) ? null : dto.AvitoLogin.Trim(),
            AvitoPassword = string.IsNullOrEmpty(dto.AvitoPassword) ? null : dto.AvitoPassword,
            AvitoCredentialsError = string.IsNullOrWhiteSpace(dto.AvitoCredentialsError)
                ? null
                : dto.AvitoCredentialsError.Trim()
        };

        if (provider == AvitoProfileProvider.Local)
        {
            var traffic = LocalChromeTrafficRules.FromStored(
                dto.LocalTrafficMode,
                dto.LocalBlockMedia,
                dto.LocalBlockAnalytics,
                dto.LocalBlockImages,
                dto.LocalBlockFonts,
                dto.LocalBlockPrefetch,
                dto.LocalNavigationTimeoutSeconds);
            account.LocalTrafficMode = traffic.Mode;
            account.LocalBlockMedia = traffic.BlockMedia;
            account.LocalBlockAnalytics = traffic.BlockAnalytics;
            account.LocalBlockImages = traffic.BlockImages;
            account.LocalBlockFonts = traffic.BlockFonts;
            account.LocalBlockPrefetch = traffic.BlockPrefetch;
            account.LocalNavigationTimeoutSeconds = traffic.NavigationTimeoutSeconds;
        }

        if (provider == AvitoProfileProvider.Local && dto.LocalProxyEnabled)
        {
            if (LocalChromeProxyRules.TryNormalizeAddress(dto.LocalProxyAddress, out var proxyAddress, out _))
            {
                account.ProxyType = LocalChromeProxyRules.HttpType;
                account.ProxyAddress = proxyAddress;
                account.ProxyUsername = NullIfWhiteSpace(dto.LocalProxyUsername);
                account.ProxyPassword = string.IsNullOrEmpty(dto.LocalProxyPassword) ? null : dto.LocalProxyPassword;
            }
        }

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
        if (Enum.TryParse<AvitoProfileProvider>(dto.ProfileProvider, ignoreCase: true, out var parsed))
        {
            if (parsed == AvitoProfileProvider.Multilogin)
            {
                return AvitoProfileProvider.Multilogin;
            }

            if (parsed == AvitoProfileProvider.Local)
            {
                return AvitoProfileProvider.Local;
            }

            if (parsed == AvitoProfileProvider.AdsPower)
            {
                return AvitoProfileProvider.AdsPower;
            }
        }

        if (!string.IsNullOrWhiteSpace(dto.MultiloginProfileId))
        {
            return AvitoProfileProvider.Multilogin;
        }

        if (!string.IsNullOrWhiteSpace(dto.LocalUserDataDir))
        {
            return AvitoProfileProvider.Local;
        }

        return AvitoProfileProvider.AdsPower;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
