using CommunityToolkit.Mvvm.ComponentModel;

namespace LeadFlow.Models;

/// <summary>
/// Поля, которые фоновый мониторинг и проверка авторизации обновляют в БД; при слиянии в UI не трогаем пользовательские настройки профиля.
/// </summary>
public sealed partial class AvitoAccount : ObservableObject
{
    [ObservableProperty] private Guid id = Guid.NewGuid();
    [ObservableProperty] private string displayName = string.Empty;
    [ObservableProperty] private string avitoResponsesUrl = "https://www.avito.ru";
    [ObservableProperty] private string browserProfilePath = string.Empty;
    [ObservableProperty] private bool isEnabled = true;
    [ObservableProperty] private AvitoAccountStatus status = AvitoAccountStatus.NotConfigured;
    [ObservableProperty] private DateTime? lastAuthCheckAt;
    [ObservableProperty] private DateTime? lastMonitoringAt;
    [ObservableProperty] private string lastErrorMessage = string.Empty;

    [ObservableProperty] private string? assignedUserAgent;
    [ObservableProperty] private string browserName = string.Empty;
    [ObservableProperty] private string browserVersion = "146";
    [ObservableProperty] private string userAgentDevice = "Все";
    [ObservableProperty] private bool useWindowsOs = true;
    [ObservableProperty] private string windowsVersion = string.Empty;
    [ObservableProperty] private bool useMacOs;
    [ObservableProperty] private string macOsVersion = "All macOS";
    [ObservableProperty] private bool useLinuxOs;
    [ObservableProperty] private string linuxVersion = "Linux x86_64";
    [ObservableProperty] private bool useAndroidOs;
    [ObservableProperty] private string androidVersion = "All Android";
    [ObservableProperty] private bool useIosOs;
    [ObservableProperty] private string iosVersion = "All iOS";
    [ObservableProperty] private string cookiesJson = string.Empty;
    [ObservableProperty] private bool importCookiesOnNextStart;
    [ObservableProperty] private string notes = string.Empty;
    [ObservableProperty] private string? screenResolution;
    [ObservableProperty] private bool useIpTimezone = true;
    [ObservableProperty] private string? timezone;
    [ObservableProperty] private string? languages;
    [ObservableProperty] private string? proxyAddress;
    [ObservableProperty] private string proxyType = "http";
    [ObservableProperty] private string? proxyUsername;
    [ObservableProperty] private string? proxyPassword;
    [ObservableProperty] private string? proxyRotationUrl;
    [ObservableProperty] private string browserLaunchArgs = string.Empty;

    [ObservableProperty] private string? navigatorPlatform;
    [ObservableProperty] private bool doNotTrack;
    [ObservableProperty] private string? webGlVendor;
    [ObservableProperty] private string? webGlRenderer;
    [ObservableProperty] private bool spoofWebGl;
    [ObservableProperty] private bool canvasFingerprintNoise;
    [ObservableProperty] private bool audioFingerprintNoise;
    [ObservableProperty] private string? webRtcLaunchFlags;

    [ObservableProperty] private string startupTabsJson = "[]";
    [ObservableProperty] private string proxyPresetsJson = "[]";
    [ObservableProperty] private string fingerprintOverviewJson = "{}";

    [ObservableProperty] private int activeAdsCount;
    [ObservableProperty] private int blockedCount;
    [ObservableProperty] private int draftsCount;
    [ObservableProperty] private DateTime? adsStatsUpdatedAt;

    /// <summary>
    /// Копирует в этот экземпляр поля, сохранённые в БД из фонового процесса (тот же <see cref="Id"/>).
    /// </summary>
    public void MergePersistedSnapshotFrom(AvitoAccount source)
    {
        if (source.Id != Id)
        {
            return;
        }

        Status = source.Status;
        LastErrorMessage = source.LastErrorMessage;
        LastMonitoringAt = source.LastMonitoringAt;
        LastAuthCheckAt = source.LastAuthCheckAt;
        ActiveAdsCount = source.ActiveAdsCount;
        BlockedCount = source.BlockedCount;
        DraftsCount = source.DraftsCount;
        AdsStatsUpdatedAt = source.AdsStatsUpdatedAt;
    }
}
