using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LeadFlow.Core.Models;

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

    /// <summary>JSON-массив последних активных объявлений (вакансий), сохранённый в БД между сеансами.</summary>
    [ObservableProperty] private string activeAdsSnapshotJson = "[]";

    /// <summary>JSON-массив последних объявлений с вкладки «С ошибками», сохранённый в БД между сеансами.</summary>
    [ObservableProperty] private string blockedAdsSnapshotJson = "[]";

    [ObservableProperty] private AvitoProfileProvider profileProvider = AvitoProfileProvider.Local;
    /// <summary>Идентификатор профиля в AdsPower (поле user_id в Local API).</summary>
    [ObservableProperty] private string? adsPowerProfileId;
    [ObservableProperty] private string? adsPowerProfileName;
    [ObservableProperty] private string? adsPowerApiBaseUrl;
    [ObservableProperty] private string? adsPowerApiKey;

    /// <summary>
    /// Имя пользователя, прочитанное со страницы Avito при последней проверке авторизации.
    /// DisplayName при этом не перезаписываем — показываем оба значения в UI.
    /// </summary>
    [ObservableProperty] private string? avitoProfileName;

    /// <summary>
    /// Сериализованный JSON-массив суб-профилей Avito Pro (модалка с дашборда <c>#profile/switch?withEntities=true</c>).
    /// Хранится как строка, чтобы не плодить отдельную таблицу в SQLite; UI читает его через <see cref="SubProfiles"/>.
    /// </summary>
    [ObservableProperty] private string subProfilesJson = "[]";

    private static readonly JsonSerializerOptions SubProfilesJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
    private string? _cachedSubProfilesJson;
    private IReadOnlyList<AvitoSubProfile> _cachedSubProfiles = Array.Empty<AvitoSubProfile>();

    /// <summary>Распарсенный список суб-профилей. Не наблюдаемое свойство — чтобы избежать рекурсивных уведомлений.</summary>
    public IReadOnlyList<AvitoSubProfile> SubProfiles
    {
        get
        {
            var json = string.IsNullOrWhiteSpace(SubProfilesJson) ? "[]" : SubProfilesJson;
            if (string.Equals(_cachedSubProfilesJson, json, StringComparison.Ordinal))
            {
                return _cachedSubProfiles;
            }

            IReadOnlyList<AvitoSubProfile> parsed;
            try
            {
                if (string.Equals(json, "[]", StringComparison.Ordinal))
                {
                    parsed = Array.Empty<AvitoSubProfile>();
                }
                else
                {
                    parsed = JsonSerializer.Deserialize<List<AvitoSubProfile>>(json, SubProfilesJsonOptions) is { Count: > 0 } list
                        ? list
                        : Array.Empty<AvitoSubProfile>();
                }
            }
            catch
            {
                parsed = Array.Empty<AvitoSubProfile>();
            }

            _cachedSubProfilesJson = json;
            _cachedSubProfiles = parsed;
            return parsed;
        }
    }

    public int SubProfilesCount => SubProfiles.Count;

    /// <summary>Короткое перечисление имён для отображения в карточке аккаунта.</summary>
    public string SubProfilesSummary
    {
        get
        {
            var items = SubProfiles;
            if (items.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", items.Select(s => string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name));
        }
    }

    public bool HasSubProfiles => SubProfiles.Count > 0;

    public bool HasSubProfileIssues => SubProfiles.Any(static s => s.HasIssue);

    public string SubProfileIssuesSummary
    {
        get
        {
            var lines = SubProfiles
                .Where(static s => s.HasIssue)
                .Select(static s => s.IssueSummaryLine)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Сохраняет распарсенный список и обновляет JSON-представление + наблюдатели.</summary>
    public void SetSubProfiles(IReadOnlyList<AvitoSubProfile> profiles)
    {
        var serialized = profiles is { Count: > 0 }
            ? JsonSerializer.Serialize(profiles, SubProfilesJsonOptions)
            : "[]";

        if (string.Equals(serialized, SubProfilesJson, StringComparison.Ordinal))
        {
            return;
        }

        _cachedSubProfilesJson = serialized;
        _cachedSubProfiles = profiles is { Count: > 0 } ? profiles.ToArray() : Array.Empty<AvitoSubProfile>();
        SubProfilesJson = serialized;
    }

    partial void OnSubProfilesJsonChanged(string value)
    {
        if (!string.Equals(_cachedSubProfilesJson, value, StringComparison.Ordinal))
        {
            _cachedSubProfilesJson = null;
            _cachedSubProfiles = Array.Empty<AvitoSubProfile>();
        }

        OnPropertyChanged(nameof(SubProfiles));
        OnPropertyChanged(nameof(SubProfilesCount));
        OnPropertyChanged(nameof(SubProfilesSummary));
        OnPropertyChanged(nameof(HasSubProfiles));
        OnPropertyChanged(nameof(HasSubProfileIssues));
        OnPropertyChanged(nameof(SubProfileIssuesSummary));
    }

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
        AvitoProfileName = source.AvitoProfileName;
        SubProfilesJson = string.IsNullOrWhiteSpace(source.SubProfilesJson) ? "[]" : source.SubProfilesJson;
        ActiveAdsSnapshotJson = string.IsNullOrWhiteSpace(source.ActiveAdsSnapshotJson)
            ? "[]"
            : source.ActiveAdsSnapshotJson;
        BlockedAdsSnapshotJson = string.IsNullOrWhiteSpace(source.BlockedAdsSnapshotJson)
            ? "[]"
            : source.BlockedAdsSnapshotJson;
    }
}
