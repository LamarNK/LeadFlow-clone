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
    [ObservableProperty] private string? screenResolution = "1920x1080";
    [ObservableProperty] private string? timezone = "Europe/Moscow";
    [ObservableProperty] private string? languages = "ru-RU,ru,en-US,en";
    [ObservableProperty] private string? proxyAddress;
    [ObservableProperty] private string proxyType = "http";

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
