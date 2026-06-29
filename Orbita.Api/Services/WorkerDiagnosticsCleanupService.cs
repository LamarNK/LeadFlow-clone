using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

/// <summary>
/// Периодически удаляет устаревшие скриншоты диагностики с диска и из БД.
/// </summary>
public sealed class WorkerDiagnosticsCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkerDiagnosticsOptions> options,
    ILogger<WorkerDiagnosticsCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.CleanupIntervalHours));

        // Небольшая задержка после старта API, чтобы не конкурировать с миграциями.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var diagnostics = scope.ServiceProvider.GetRequiredService<WorkerDiagnosticsService>();
                var removed = await diagnostics.PruneExpiredAttachmentsAsync(stoppingToken).ConfigureAwait(false);
                if (removed > 0)
                {
                    logger.LogInformation("Удалено устаревших скриншотов диагностики: {Count}", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ошибка фоновой очистки скриншотов диагностики.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}