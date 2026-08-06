using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

public sealed class CrmDeadlineNotificationHostedService(
    IServiceProvider services,
    IOptions<CrmDeadlineNotificationOptions> options,
    TimeProvider timeProvider,
    ILogger<CrmDeadlineNotificationHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanupAtUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<CrmDeadlineNotificationService>();
                var created = await processor.ProcessDueTasksAsync(stoppingToken);

                var now = timeProvider.GetUtcNow();
                var removed = 0;
                if (now - lastCleanupAtUtc >= CleanupInterval)
                {
                    removed = await processor.CleanupAsync(stoppingToken);
                    lastCleanupAtUtc = now;
                }

                if (created > 0 || removed > 0)
                {
                    logger.LogInformation(
                        "CRM deadline notification cycle created {Created} and removed {Removed} notifications.",
                        created,
                        removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CRM deadline notification cycle failed.");
            }

            var delaySeconds = Math.Clamp(options.Value.ScanIntervalSeconds, 10, 3600);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}
