using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

public sealed class CrmReprocessingHostedService(
    IServiceScopeFactory scopes,
    IOptions<CrmReprocessingOptions> options,
    ILogger<CrmReprocessingHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.ScanIntervalSeconds, 5, 300));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var count = await scope.ServiceProvider.GetRequiredService<CrmReprocessingService>()
                    .ProcessBatchAsync(stoppingToken);
                if (count > 0) logger.LogInformation("CRM repeat processing transferred {CardCount} cards.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CRM repeat processing failed; pending closed cards will be retried.");
            }
        }
    }
}
