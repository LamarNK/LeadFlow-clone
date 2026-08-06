using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

/// <summary>
/// Периодически удаляет устаревшие записи логов воркеров из БД.
/// </summary>
public sealed class WorkerLogsCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerLogsOptions> options,
    ILogger<WorkerLogsCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.CleanupIntervalHours));

        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var logs = scope.ServiceProvider.GetRequiredService<WorkerLogsService>();
                var removed = await logs.PruneExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    logger.LogInformation("Удалено устаревших записей логов воркеров: {Count}", removed);
                }

                var monitoringRuns = scope.ServiceProvider.GetRequiredService<MonitoringRunIngestService>();
                var prunedRuns = await monitoringRuns.PruneExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (prunedRuns > 0)
                {
                    logger.LogInformation("Удалено устаревших записей журнала мониторинг-циклов: {Count}", prunedRuns);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фоновой очистки логов воркеров.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}