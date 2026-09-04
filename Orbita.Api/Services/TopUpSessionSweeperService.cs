namespace Orbita.Api.Services;

/// <summary>
/// Фоновый sweeper сессий пополнения: завершает истёкшие активные сессии (снимая паузу,
/// поставленную сессией) и удаляет QR-данные завершённых сессий после короткого срока хранения.
/// </summary>
public sealed class TopUpSessionSweeperService(
    IServiceProvider services,
    ILogger<TopUpSessionSweeperService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var sessionService = scope.ServiceProvider.GetRequiredService<TopUpSessionService>();

                var expired = await sessionService.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
                var purged = await sessionService.PurgeQrDataAsync(stoppingToken).ConfigureAwait(false);

                if (expired > 0 || purged > 0)
                {
                    logger.LogInformation(
                        "Top-up session sweeper expired {Expired} sessions and purged QR data for {Purged} sessions.",
                        expired,
                        purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Top-up session sweeper failed.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
