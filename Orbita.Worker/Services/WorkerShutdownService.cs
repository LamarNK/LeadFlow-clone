using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orbita.Worker.Services;

public sealed class WorkerShutdownService(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    WorkerUpdateStore updateStore)
{
    private readonly Lock _sync = new();
    private int _shutdownRequested;
    private int _installScriptLaunched;
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

    public bool IsShutdownInProgress => Volatile.Read(ref _shutdownRequested) == 1;

    public bool InstallScriptLaunched => Volatile.Read(ref _installScriptLaunched) == 1;

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

        if (Volatile.Read(ref _shutdownRequested) == 1)
        {
            lock (_sync)
            {
                return _pendingInstallPath is not null;
            }
        }

        if (!TryBeginShutdown())
        {
            return false;
        }

        lock (_sync)
        {
            _pendingInstallPath = msiPath;
        }

        PersistPendingInstallState();
        WorkerRestartHelper.LaunchInstallScript(msiPath, Environment.ProcessId);
        Volatile.Write(ref _installScriptLaunched, 1);
        _ = GlobalLogger.Instance.LogAsync(
            $"Worker update: фоновый установщик запущен, ожидание выхода процесса (PID {Environment.ProcessId}).",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(RequestInstall));
        _ = BeginShutdownAsync();
        return true;
    }

    private void PersistPendingInstallState()
    {
        var pendingMsi = updateStore.TryGetPendingMsi();
        if (pendingMsi is null)
        {
            return;
        }

        updateStore.SavePendingInstall(pendingMsi.Version);
        updateStore.ClearPendingMsi();
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
                var stopTask = monitoringService.StopAsync();
                var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(45)))
                    .ConfigureAwait(false);
                if (completed == stopTask)
                {
                    await stopTask.ConfigureAwait(false);
                }
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

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }
}