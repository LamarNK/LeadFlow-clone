using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class WorkerAvitoAdsMonitorHost(
    WorkerAvitoAdsMonitor monitor,
    WorkerCredentials credentials,
    IWorkerMonitoringService monitoringService) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OrbitaFeatureToggles.ListingsEnabled)
        {
            return;
        }

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
