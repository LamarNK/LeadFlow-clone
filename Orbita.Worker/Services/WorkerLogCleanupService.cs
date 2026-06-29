using LeadFlow.Core.Logging.Audit;
using Microsoft.Extensions.Hosting;

namespace Orbita.Worker.Services;

/// <summary>
/// Периодически удаляет локальные файлы логов воркера старше месяца (после синхронизации с Orbita).
/// </summary>
public sealed class WorkerLogCleanupService(WorkerLogSyncState syncState) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        syncState.Load();
        var interval = TimeSpan.FromHours(WorkerMaintenanceOptions.LogCleanupIntervalHours);

        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PruneLocalLogs();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // retry on next interval
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private void PruneLocalLogs()
    {
        var cutoff = DateTime.UtcNow.AddDays(-WorkerMaintenanceOptions.LogRetentionDays);
        var logDir = GlobalLogger.ResolveLogDirectoryForService("Orbita.Worker");
        var logger = new Logger(logDir);
        _ = logger.PruneLogFilesBeforeAsync(cutoff, syncState.LastSyncedUtc);
    }
}