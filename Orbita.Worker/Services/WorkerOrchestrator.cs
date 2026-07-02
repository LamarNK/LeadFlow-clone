using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class WorkerOrchestrator(
    AppRepository repository,
    AppSettings appSettings,
    OrbitaApiClient apiClient,
    OrbitaConfigProvider configProvider,
    OrbitaCandidateSink candidateSink,
    WorkerEventSink eventSink,
    WorkerTelemetryCollector telemetryCollector,
    IAdsPowerApiClient adsPowerApi,
    IWorkerMonitoringService monitoringService,
    WorkerCredentials credentials,
    WorkerRuntimeState runtimeState,
    SystemMetricsCollector metricsCollector,
    WorkerSystemInfoCollector systemInfoCollector,
    WorkerUpdateStore updateStore,
    IWorkerActivityReporter activityReporter) : BackgroundService
{
    private bool _monitoringRequested = true;

    public void RequestStartMonitoring() => _monitoringRequested = true;
    public void RequestStopMonitoring() => _monitoringRequested = false;

    private DateTime _lastAccountSyncUtc = DateTime.MinValue;
    private static readonly TimeSpan AccountSyncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", "Orbita.Worker");
        await repository.InitializeAsync(appSettings, stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = await apiClient.GetConfigAsync(stoppingToken).ConfigureAwait(false);
                if (config is null)
                {
                    runtimeState.Status = "Ошибка";
                    runtimeState.Detail = "Нет связи с API";
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                credentials.WorkerId ??= config.WorkerId;
                credentials.DisplayName ??= Environment.MachineName;

                if (string.Equals(config.PendingCommand, WorkerCommands.Restart, StringComparison.OrdinalIgnoreCase))
                {
                    runtimeState.Status = "Перезапуск";
                    runtimeState.Detail = "По команде из панели";
                    await monitoringService.StopAsync().ConfigureAwait(false);
                    await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                    await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                    WorkerRestartHelper.ScheduleRestart();
                    return;
                }

                runtimeState.Status = "Онлайн";
                runtimeState.Detail = config.Accounts.Count(a => a.IsEnabled) + " акк.";

                // Reduce expensive full profile sync chatter: only every ~5 min or on first run
                if (DateTime.UtcNow - _lastAccountSyncUtc >= AccountSyncInterval)
                {
                    await SyncAdsPowerProfilesAsync(config, stoppingToken).ConfigureAwait(false);
                    _lastAccountSyncUtc = DateTime.UtcNow;
                }
                configProvider.InvalidateCache();

                if (_monitoringRequested && !monitoringService.IsActive)
                {
                    await monitoringService.StartAsync(stoppingToken).ConfigureAwait(false);
                    runtimeState.IsMonitoring = true;
                }
                else if (!_monitoringRequested && monitoringService.IsActive)
                {
                    await monitoringService.StopAsync().ConfigureAwait(false);
                    await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                    await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                    runtimeState.IsMonitoring = false;
                }
                else if (!_monitoringRequested)
                {
                    activityReporter.ReportIdle();
                }

                await SendHeartbeatAsync(config.WorkerId, stoppingToken).ConfigureAwait(false);
                await SendSnapshotAsync(config, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                runtimeState.Status = "Ошибка";
                runtimeState.Detail = ex.Message;
            }

            await Task.Delay(TelemetryInterval, stoppingToken).ConfigureAwait(false);
        }

        if (monitoringService.IsActive)
        {
            await monitoringService.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task SyncAdsPowerProfilesAsync(WorkerConfigDto config, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.AdsPowerApiBaseUrl)
            ? "http://local.adspower.net:50325"
            : config.AdsPowerApiBaseUrl;
        var options = new AdsPowerConnectionOptions(baseUrl, config.AdsPowerApiKey);

        IReadOnlyList<AdsPowerProfileSummary> profiles;
        try
        {
            profiles = await adsPowerApi.ListProfilesAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var items = profiles
            .Select(p => new WorkerAccountSyncItemDto(p.UserId, p.Name))
            .ToList();

        await apiClient.SyncAccountsAsync(new WorkerAccountSyncRequest(items), ct).ConfigureAwait(false);
    }

    private async Task SendHeartbeatAsync(Guid workerId, CancellationToken ct)
    {
        var metrics = metricsCollector.Collect();
        var publicIp = await systemInfoCollector.ResolvePublicIpAsync(ct).ConfigureAwait(false);
        var agentVersion = ApplicationVersionProvider.GetVersion();
        var heartbeat = new WorkerHeartbeatRequest(
            workerId,
            credentials.DisplayName ?? Environment.MachineName,
            agentVersion,
            Environment.MachineName,
            monitoringService.IsActive ? "Running" : "Stopped",
            runtimeState.Detail,
            monitoringService.IsActive,
            DateTime.UtcNow.AddMinutes(5),
            metrics,
            updateStore.TryConsumePendingHeartbeatResult(),
            systemInfoCollector.OperatingSystem,
            systemInfoCollector.StartedAtUtc,
            publicIp,
            agentVersion);

        await apiClient.SendHeartbeatAsync(heartbeat, ct).ConfigureAwait(false);
    }

    private async Task SendSnapshotAsync(WorkerConfigDto config, CancellationToken ct)
    {
        var snapshot = await telemetryCollector.BuildSnapshotAsync(config, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        await apiClient.SendSnapshotAsync(snapshot, ct).ConfigureAwait(false);
    }
}