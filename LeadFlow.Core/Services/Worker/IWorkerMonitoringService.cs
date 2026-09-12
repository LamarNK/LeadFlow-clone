namespace LeadFlow.Core.Services.Worker;

public interface IWorkerMonitoringService
{
    bool IsActive { get; }
    bool IsCaptchaHold { get; }
    bool IsAccountBusy(Guid accountId);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    void RequestImmediatePass();
    Task EnterCaptchaHoldAsync();
    void ExitCaptchaHold();
}
