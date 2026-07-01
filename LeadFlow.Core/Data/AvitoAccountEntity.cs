using LeadFlow.Core.Models;

namespace LeadFlow.Core.Data;

public sealed class AvitoAccountEntity
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string AvitoResponsesUrl { get; set; } = string.Empty;
    public string BrowserProfilePath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? LastAuthCheckAt { get; set; }
    public DateTime? LastMonitoringAt { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public string BrowserName { get; set; } = string.Empty;
    public string BrowserVersion { get; set; } = "146";
    public string UserAgentDevice { get; set; } = "Все";
    public bool UseWindowsOs { get; set; } = true;
    public string WindowsVersion { get; set; } = "Windows 10";
    public bool UseMacOs { get; set; }
    public string MacOsVersion { get; set; } = "All macOS";
    public bool UseLinuxOs { get; set; }
    public string LinuxVersion { get; set; } = "Linux x86_64";
    public bool UseAndroidOs { get; set; }
    public string AndroidVersion { get; set; } = "All Android";
    public bool UseIosOs { get; set; }
    public string IosVersion { get; set; } = "All iOS";
    public string? AssignedUserAgent { get; set; }
    public string CookiesJson { get; set; } = string.Empty;
    public bool ImportCookiesOnNextStart { get; set; }
    public string Notes { get; set; } = string.Empty;
    public string? ProxyAddress { get; set; }
    public string ProxyType { get; set; } = "http";
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public string? ProxyRotationUrl { get; set; }
    public string BrowserLaunchArgs { get; set; } = string.Empty;
    public string? NavigatorPlatform { get; set; }
    public bool DoNotTrack { get; set; }
    public string? WebGlVendor { get; set; }
    public string? WebGlRenderer { get; set; }
    public bool SpoofWebGl { get; set; }
    public bool CanvasFingerprintNoise { get; set; } = true;
    public bool AudioFingerprintNoise { get; set; } = true;
    public string? WebRtcLaunchFlags { get; set; }
    public string StartupTabsJson { get; set; } = "[]";
    public string ProxyPresetsJson { get; set; } = "[]";
    public string FingerprintOverviewJson { get; set; } = "{}";
    public string? ScreenResolution { get; set; }
    public bool UseIpTimezone { get; set; }
    public string? Timezone { get; set; }
    public string? Languages { get; set; }
    public int ActiveAdsCount { get; set; }
    public int BlockedCount { get; set; }
    public int DraftsCount { get; set; }
    public DateTime? AdsStatsUpdatedAt { get; set; }

    /// <summary>JSON-массив активных вакансий (<c>AvitoAdStatus</c>), кэш между сеансами.</summary>
    public string ActiveAdsSnapshotJson { get; set; } = "[]";

    /// <summary>JSON-массив заблокированных / «с ошибками» объявлений.</summary>
    public string BlockedAdsSnapshotJson { get; set; } = "[]";

    public string ProfileProvider { get; set; } = nameof(AvitoProfileProvider.Local);
    public string? AdsPowerProfileId { get; set; }
    public string? AdsPowerProfileName { get; set; }
    public string? AdsPowerApiBaseUrl { get; set; }
    public string? AdsPowerApiKey { get; set; }

    /// <summary>
    /// Имя пользователя со страницы Avito (последняя удачная проверка авторизации).
    /// </summary>
    public string? AvitoProfileName { get; set; }

    /// <summary>
    /// JSON-массив суб-профилей Avito Pro, распарсенных из модалки переключения профилей.
    /// </summary>
    public string SubProfilesJson { get; set; } = "[]";

    /// <summary>Когда воркер последний раз перечитывал список суб-профилей из Avito (UTC).</summary>
    public DateTime? SubProfilesRefreshedAt { get; set; }
}
