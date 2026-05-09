using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IMonitoringService
{
    event EventHandler<MonitoringStatus>? StatusChanged;
    event EventHandler<string>? StatusMessageChanged;
    event EventHandler<CandidateResponse>? ResponseProcessed;
    event EventHandler<ProfileStatsUpdatedEventArgs>? ProfileStatsUpdated;
    event EventHandler? NextCycleCheckTimeChanged;
    event EventHandler<string>? MonitoringAutoStopped;
    MonitoringStatus CurrentStatus { get; }
    string CurrentStatusMessage { get; }
    bool IsActive { get; }
    DateTime? NextCycleCheckAtUtc { get; }
    IReadOnlyList<AvitoAdStatus> GetActiveAdsSnapshot();

    /// <summary>
    /// Срез заблокированных объявлений (вкладка «С ошибками» Avito Pro) по всем аккаунтам.
    /// Используется дашбордом, чтобы показать список и подкорректировать поведение бота.
    /// </summary>
    IReadOnlyList<AvitoAdStatus> GetBlockedAdsSnapshot();

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
