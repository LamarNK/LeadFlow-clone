namespace Orbita.Api.Services;

/// <summary>
/// Completes persisted five-minute CRM distribution sessions. The state lives
/// in PostgreSQL, so a container restart cannot lose the waiting window.
/// </summary>
public sealed class CrmDailyDistributionHostedService(
    IServiceProvider services,
    ILogger<CrmDailyDistributionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
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
                var changedOffices = await workspace.ProcessDueDailyDistributionsAsync(stoppingToken);
                if (changedOffices > 0)
                {
                    logger.LogInformation(
                        "Completed daily CRM lead/NDZ distribution for {OfficeCount} office(s).",
                        changedOffices);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Daily CRM lead/NDZ distribution cycle failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
