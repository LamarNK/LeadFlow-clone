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
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
