using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orbita.Worker.Services;

public sealed class WorkerShutdownService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime)
{
    private readonly Lock _sync = new();
    private int _shutdownRequested;
    private string? _pendingInstallPath;
    private bool _pendingRestart;

    public string? PendingInstallPath
    {
        get
        {
            lock (_sync)
            {
                return _pendingInstallPath;
            }
        }
    }

    public bool PendingRestart
    {
        get
        {
            lock (_sync)
            {
                return _pendingRestart;
            }
        }
    }

    public bool RequestRestart()
    {
        if (!TryBeginShutdown())
        {
            return false;
        }

        lock (_sync)
        {
            _pendingRestart = true;
        }

        _ = BeginShutdownAsync();
        return true;
    }

    public bool RequestInstall(string msiPath)
    {
        if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
        {
            return false;
        }

        if (!TryBeginShutdown())
        {
            return false;
        }

        lock (_sync)
        {
            _pendingInstallPath = msiPath;
        }

        _ = BeginShutdownAsync();
        return true;
    }

    private bool TryBeginShutdown()
    {
        return Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) == 0;
    }

    private async Task BeginShutdownAsync()
    {
        try
        {
            var monitoringService = services.GetRequiredService<IWorkerMonitoringService>();
            if (monitoringService.IsActive)
            {
                await monitoringService.StopAsync().ConfigureAwait(false);
            }

            var candidateSink = services.GetRequiredService<OrbitaCandidateSink>();
            var eventSink = services.GetRequiredService<WorkerEventSink>();
            await candidateSink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await eventSink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // proceed with shutdown even if cleanup fails
        }

        lifetime.StopApplication();

        if (Application.MessageLoop)
        {
            Application.Exit();
        }
    }
}