using LeadFlow.Core.Logging.Audit;
using Microsoft.Extensions.Hosting;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class EphemeralCacheCleanupService(EphemeralDedupCache dedupCache) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(WorkerMaintenanceOptions.DatabaseCleanupIntervalHours);
        await Task.Delay(TimeSpan.FromMinutes(12), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await dedupCache.PruneExpiredAsync(DateTime.UtcNow, stoppingToken)
                    .ConfigureAwait(false);
                if (removed > 0)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Очистка cache.db: удалено записей дедупа {removed}.",
                        DeskLinkAuditLogLevel.Info);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Очистка cache.db не удалась: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}