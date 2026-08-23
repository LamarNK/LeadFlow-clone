namespace Orbita.Api.Services;

public sealed class CrmTelephonyRuntimeSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CrmTelephonyRuntimeSyncHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var telephony = scope.ServiceProvider.GetRequiredService<CrmTelephonyService>();
            var synchronized = await telephony.SynchronizeAsteriskWebRtcAsync(cancellationToken);
            if (synchronized > 0)
            {
                logger.LogInformation(
                    "Published {BindingCount} browser SIP endpoint bindings to the Asterisk runtime.",
                    synchronized);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown while startup work is in progress.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not synchronize browser SIP endpoints with Asterisk.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
