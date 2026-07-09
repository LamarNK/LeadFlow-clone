namespace Orbita.Api.Services;

public sealed class CaptchaSessionSweeperService(
    IServiceProvider services,
    ILogger<CaptchaSessionSweeperService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var sessionService = scope.ServiceProvider.GetRequiredService<CaptchaSessionService>();
                var swept = await sessionService.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
                if (swept > 0)
                {
                    logger.LogInformation("Captcha session sweeper expired {Count} sessions.", swept);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Captcha session sweeper failed.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}