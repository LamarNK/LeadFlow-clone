using LeadFlow.Models;

namespace LeadFlow.Services;

public interface IMonitoringService
{
    event EventHandler<MonitoringStatus>? StatusChanged;
    event EventHandler<CandidateResponse>? ResponseProcessed;
    MonitoringStatus CurrentStatus { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
