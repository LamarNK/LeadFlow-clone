namespace Orbita.Api.Services;

/// <summary>
/// Периодически снимает CRM-смены, которые менеджеры забыли остановить.
/// </summary>
public sealed class CrmShiftSweeperService(
    IServiceProvider services,
    ILogger<CrmShiftSweeperService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Не стартовать в ноль секунд после boot — дать БД и миграциям подняться.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var workspace = scope.ServiceProvider.GetRequiredService<CrmWorkspaceService>();
                var expired = await workspace.ExpireStaleShiftsAsync(stoppingToken).ConfigureAwait(false);
                if (expired > 0)
                {
                    logger.LogInformation(
                        "CRM shift sweeper auto-stopped {Count} stale manager shift(s).",
                        expired);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CRM shift sweeper failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
