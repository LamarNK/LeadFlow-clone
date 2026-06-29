using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Logging.Audit;

namespace Orbita.Api.Services;

/// <summary>
/// Периодически удаляет устаревшие файлы сервисных логов Orbita (Api, Web и др.).
/// </summary>
public sealed class ServiceLogsCleanupService(
    IConfiguration configuration,
    IOptions<ServiceLogsOptions> options,
    ILogger<ServiceLogsCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.CleanupIntervalHours));

        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = PruneExpiredFiles();
                if (removed > 0)
                {
                    logger.LogInformation("Удалено устаревших файлов сервисных логов: {Count}", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фоновой очистки сервисных логов.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private int PruneExpiredFiles()
    {
        var sharedRoot = configuration["Logs:SharedRoot"];
        var root = string.IsNullOrWhiteSpace(sharedRoot)
            ? GlobalLogger.ResolveLogsRootDirectory(string.Empty)
            : Path.GetFullPath(sharedRoot);

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, options.Value.RetentionDays));
        return GlobalLogger.PruneLogFilesInRoot(root, cutoff, DateTime.UtcNow);
    }
}