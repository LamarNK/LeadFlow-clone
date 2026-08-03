using Microsoft.Extensions.Options;
using Orbita.Api.Models;

namespace Orbita.Api.Services;

public sealed class BitrixWorkforceHostedService(
    IServiceProvider services,
    IOptions<BitrixWorkforceOptions> options,
    TimeProvider timeProvider,
    ILogger<BitrixWorkforceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastReconciliationAtUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<BitrixWorkforceProcessor>();
                var processed = await processor.ProcessBatchAsync(stoppingToken);

                var now = timeProvider.GetUtcNow();
                var reconciled = 0;
                if (now - lastReconciliationAtUtc >= ReconciliationInterval)
                {
                    reconciled = await processor.ReconcileAsync(stoppingToken);
                    lastReconciliationAtUtc = now;
                }

                if (processed > 0 || reconciled > 0)
                {
                    logger.LogInformation(
                        "Bitrix workforce cycle processed {Processed} jobs and enqueued {Reconciled} reconciliation jobs.",
                        processed,
                        reconciled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bitrix workforce cycle failed.");
            }

            var delaySeconds = Math.Clamp(options.Value.WorkerIntervalSeconds, 1, 60);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}
