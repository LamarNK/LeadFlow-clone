namespace Orbita.Api.Services;

public sealed class WorkerScheduleHostedService(
    IServiceProvider services,
    ILogger<WorkerScheduleHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var scheduler = scope.ServiceProvider.GetRequiredService<WorkerScheduleService>();
                var changed = await scheduler.ApplyAsync(stoppingToken).ConfigureAwait(false);
                if (changed > 0)
                {
                    logger.LogInformation("Worker schedule applied to {Count} workers.", changed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker schedule application failed.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
