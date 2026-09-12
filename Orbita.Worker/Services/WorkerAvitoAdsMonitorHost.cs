using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;

namespace Orbita.Worker.Services;

public sealed class WorkerAvitoAdsMonitorHost(
    WorkerAvitoAdsMonitor monitor,
    WorkerCredentials credentials,
    IWorkerMonitoringService monitoringService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (monitoringService.IsActive
                    && !monitoringService.IsCaptchaHold
                    && credentials.WorkerId is Guid workerId)
                {
                    await monitor.RunDueAsync(workerId, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Следующий тик повторит проверку; сбой объявлений не должен останавливать воркер.
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromSeconds(MonitoringTiming.AvitoAdsLoopIdleSeconds),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
