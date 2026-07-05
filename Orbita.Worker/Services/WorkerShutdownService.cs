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
    private int _restartScriptLaunched;
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

    public bool RestartScriptLaunched => Volatile.Read(ref _restartScriptLaunched) == 1;

    public bool RequestRestart()
    {
        if (!TryBeginShutdown())
        {
            _ = WorkerLifecycleLog.WarningAsync(
                "Worker lifecycle: запрос перезапуска отклонён — shutdown уже выполняется",
                nameof(RequestRestart));
            return false;
        }

        lock (_sync)
        {
            _pendingRestart = true;
        }

        _ = WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: запрос перезапуска принят, запуск фонового скрипта",
            nameof(RequestRestart),
            new Dictionary<string, object?> { ["restart.source"] = "panel" });

        var restartLaunched = WorkerRestartHelper.LaunchProcessRestart(Environment.ProcessId, updateRestart: true);
        if (restartLaunched)
        {
            Volatile.Write(ref _restartScriptLaunched, 1);
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
        var reason = PendingRestart ? "restart" : PendingInstallPath is not null ? "update" : "shutdown";
        await WorkerLifecycleLog.InfoAsync(
            $"Worker lifecycle: начало graceful shutdown (причина: {reason})",
            nameof(BeginShutdownAsync),
            new Dictionary<string, object?> { ["shutdown.reason"] = reason })
            .ConfigureAwait(false);

        try
        {
            var monitoringService = services.GetRequiredService<IWorkerMonitoringService>();
            if (monitoringService.IsActive)
            {
                await WorkerLifecycleLog.InfoAsync(
                    "Worker lifecycle: остановка мониторинга",
                    nameof(BeginShutdownAsync))
                    .ConfigureAwait(false);

                var stopTask = monitoringService.StopAsync();
                var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(45)))
                    .ConfigureAwait(false);
                if (completed == stopTask)
                {
                    await stopTask.ConfigureAwait(false);
                    await WorkerLifecycleLog.InfoAsync(
                        "Worker lifecycle: мониторинг остановлен",
                        nameof(BeginShutdownAsync))
                        .ConfigureAwait(false);
                }
                else
                {
                    await WorkerLifecycleLog.WarningAsync(
                        "Worker lifecycle: остановка мониторинга превысила 45 с, продолжаем shutdown",
                        nameof(BeginShutdownAsync))
                        .ConfigureAwait(false);
                }
            }

            var candidateSink = services.GetRequiredService<OrbitaCandidateSink>();
            var eventSink = services.GetRequiredService<WorkerEventSink>();
            await candidateSink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await eventSink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            await WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: буферы кандидатов и событий сброшены",
                nameof(BeginShutdownAsync))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WorkerLifecycleLog.WarningAsync(
                $"Worker lifecycle: ошибка при подготовке к shutdown: {ex.Message}",
                nameof(BeginShutdownAsync))
                .ConfigureAwait(false);
        }

        lifetime.StopApplication();

        if (Application.MessageLoop)
        {
            await WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: Application.Exit()",
                nameof(BeginShutdownAsync))
                .ConfigureAwait(false);
            Application.Exit();
        }

        if (!PendingRestart)
        {
            await WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: запланирован принудительный Environment.Exit через 8 с",
                nameof(BeginShutdownAsync))
                .ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                await WorkerLifecycleLog.InfoAsync(
                    "Worker lifecycle: Environment.Exit(0)",
                    nameof(BeginShutdownAsync))
                    .ConfigureAwait(false);
                Environment.Exit(0);
            });
        }
        else
        {
            await WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: Environment.Exit не используется — ожидается перезапуск",
                nameof(BeginShutdownAsync))
                .ConfigureAwait(false);
        }
    }
}