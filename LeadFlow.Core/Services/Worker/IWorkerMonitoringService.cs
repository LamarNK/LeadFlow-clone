namespace LeadFlow.Core.Services.Worker;

public interface IWorkerMonitoringService
{
    bool IsActive { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}