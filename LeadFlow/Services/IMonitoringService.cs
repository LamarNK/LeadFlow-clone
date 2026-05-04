using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IMonitoringService
{
    event EventHandler<MonitoringStatus>? StatusChanged;
    event EventHandler<string>? StatusMessageChanged;
    event EventHandler<CandidateResponse>? ResponseProcessed;
    event EventHandler<ProfileStatsUpdatedEventArgs>? ProfileStatsUpdated;
    MonitoringStatus CurrentStatus { get; }
    string CurrentStatusMessage { get; }
    bool IsActive { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
