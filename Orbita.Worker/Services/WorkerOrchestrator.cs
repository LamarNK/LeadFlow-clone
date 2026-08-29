using LeadFlow.Core.Models;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.Multilogin;
using LeadFlow.Core.Services.Worker;
using Microsoft.Extensions.Hosting;
using Orbita.Contracts;
using Orbita.Worker;

namespace Orbita.Worker.Services;

public sealed class WorkerOrchestrator(
    EphemeralDedupCache dedupCache,
    WorkerCandidateOutbox candidateOutbox,
    OrbitaApiClient apiClient,
    OrbitaConfigProvider configProvider,
    OrbitaCandidateSink candidateSink,
    WorkerEventSink eventSink,
    WorkerTelemetryCollector telemetryCollector,
    IAdsPowerApiClient adsPowerApi,
    IMultiloginApiClient multiloginApi,
    IWorkerMonitoringService monitoringService,
    WorkerCredentials credentials,
    WorkerRuntimeState runtimeState,
    SystemMetricsCollector metricsCollector,
    WorkerSystemInfoCollector systemInfoCollector,
    WorkerUpdateStore updateStore,
    WorkerUpdateOfferSource updateOfferSource,
    WorkerUpdateGate updateGate,
    WorkerShutdownService shutdownService,
    IWorkerPendingUpdateCoordinator pendingUpdateCoordinator,
    IWorkerActivityReporter activityReporter,
    CaptchaSessionCoordinator captchaCoordinator,
    BrowserMonitorCoordinator browserMonitorCoordinator,
    BrowserMonitorSource browserMonitorSource,
    IWorkerRealtimeChannel realtime) : BackgroundService
{
    private bool _monitoringRequested = true;
    private bool _pausedByPanel;
    private int _browserMonitorLaunching;

    public void RequestStartMonitoring() => _monitoringRequested = true;
    public void RequestStopMonitoring() => _monitoringRequested = false;

    private DateTime _lastAccountSyncUtc = DateTime.MinValue;
    private string? _lastSyncedAdsPowerGroupId;
    private string? _enabledAccountsFingerprint;
    private DateTime _enabledAccountsChangedAtUtc = DateTime.MinValue;
    private string? _pushedCommand;
    private WorkerPendingCaptchaSessionDto? _pushedCaptchaSession;
    private WorkerPendingBrowserMonitorSessionDto? _pushedBrowserMonitorSession;

    private static readonly TimeSpan AccountSyncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConnectedLoopInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectedLoopInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MonitoringStartDebounce =
        TimeSpan.FromSeconds(MonitoringTiming.MonitoringStartDebounceSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Environment.SetEnvironmentVariable("LOG_SERVICE_NAME", "Orbita.Worker");
        await dedupCache.InitializeAsync(stoppingToken).ConfigureAwait(false);
        await candidateOutbox.InitializeAsync(stoppingToken).ConfigureAwait(false);

        realtime.CommandReceived += OnCommandReceived;
        realtime.ConfigChanged += OnConfigChanged;
        realtime.CaptchaSessionReceived += OnCaptchaSessionReceived;
        realtime.BrowserMonitorSessionReceived += OnBrowserMonitorSessionReceived;

        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: оркестратор запущен",
            nameof(ExecuteAsync))
            .ConfigureAwait(false);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (TryConsumePushedCommand(out var pushedCommand))
                    {
                        if (await TryHandlePauseCommandAsync(pushedCommand, stoppingToken).ConfigureAwait(false))
                        {
                            await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
                            continue;
                        }

                        if (await TryHandleRestartCommandAsync(pushedCommand, null, stoppingToken).ConfigureAwait(false))
                        {
                            return;
                        }
                    }

                    var config = await apiClient.GetConfigAsync(stoppingToken).ConfigureAwait(false);
                    if (config is null)
                    {
                        if (apiClient.LastConfigWasUnauthorized)
                        {
                            await EnsureWorkerPausedAsync(stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            runtimeState.Status = WorkerConnectionErrors.ErrorStatus;
                            runtimeState.Detail = apiClient.LastConfigError ?? "Нет связи с API";
                        }

                        await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_pausedByPanel)
                    {
                        _pausedByPanel = false;
                        RequestStartMonitoring();
                    }

                    credentials.WorkerId ??= config.WorkerId;
                    credentials.DisplayName ??= Environment.MachineName;

                    updateOfferSource.SetOffer(config.UpdateOffer);

                    var command = TryConsumePushedCommand(out var pushed) ? pushed : config.PendingCommand;
                    if (await TryHandlePauseCommandAsync(command, stoppingToken).ConfigureAwait(false))
                    {
                        await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (await TryHandleRestartCommandAsync(command, config.WorkerId, stoppingToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    var enabledCount = config.Accounts.Count(a => a.IsEnabled && config.IsBrowserProviderEnabled(a));
                    runtimeState.Status = "Онлайн";

                    var groupChanged = !string.Equals(
                        _lastSyncedAdsPowerGroupId,
                        config.AdsPowerGroupId,
                        StringComparison.Ordinal);
                    if (groupChanged || DateTime.UtcNow - _lastAccountSyncUtc >= AccountSyncInterval)
                    {
                        if (config.ShouldSyncAdsPowerCatalog)
                        {
                            await SyncAdsPowerProfilesAsync(config, stoppingToken).ConfigureAwait(false);
                        }

                        if (config.ShouldSyncMultiloginCatalog)
                        {
                            await SyncMultiloginProfilesAsync(config, stoppingToken).ConfigureAwait(false);
                        }

                        _lastAccountSyncUtc = DateTime.UtcNow;
                        _lastSyncedAdsPowerGroupId = config.AdsPowerGroupId;
                    }

                    var pendingCaptcha = TryConsumePushedCaptchaSession() ?? config.PendingCaptchaSession;
                    if (pendingCaptcha is not null
                        && !captchaCoordinator.IsRunning)
                    {
                        runtimeState.Status = "Капча";
                        runtimeState.Detail = "Решение капчи оператором";
                        _ = await captchaCoordinator
                            .TryRunPendingSessionAsync(pendingCaptcha, stoppingToken)
                            .ConfigureAwait(false);
                        configProvider.InvalidateCache();
                        await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    TryLaunchBrowserMonitor(config.PendingBrowserMonitorSession, stoppingToken);

                    await SyncMonitoringStateAsync(config, enabledCount, stoppingToken).ConfigureAwait(false);

                    if (pendingUpdateCoordinator.HasPendingInstall
                        && pendingUpdateCoordinator.TryApplyPendingInstallAtPause())
                    {
                        return;
                    }

                    if (updateStore.TryGetPendingMsi() is { } pendingMsi
                        && !string.Equals(runtimeState.Status, "Обновление", StringComparison.Ordinal))
                    {
                        runtimeState.Detail = $"Обновление {pendingMsi.Version} скачано, ожидание паузы";
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

                await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            realtime.CommandReceived -= OnCommandReceived;
            realtime.ConfigChanged -= OnConfigChanged;
            realtime.CaptchaSessionReceived -= OnCaptchaSessionReceived;
            realtime.BrowserMonitorSessionReceived -= OnBrowserMonitorSessionReceived;
        }

        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: оркестратор остановлен",
            nameof(ExecuteAsync))
            .ConfigureAwait(false);

        if (monitoringService.IsActive)
        {
            await monitoringService.StopAsync().ConfigureAwait(false);
        }
    }

    private void OnCommandReceived(string command) => _pushedCommand = command;

    private void OnConfigChanged() => configProvider.InvalidateCache();

    private void OnCaptchaSessionReceived(WorkerPendingCaptchaSessionDto session) =>
        _pushedCaptchaSession = session;

    private void OnBrowserMonitorSessionReceived(WorkerPendingBrowserMonitorSessionDto session)
    {
        _pushedBrowserMonitorSession = session;
        configProvider.InvalidateCache();
        browserMonitorCoordinator.CancelCurrentSession();
        realtime.RequestWake();
    }

    private bool TryConsumePushedCommand(out string? command)
    {
        command = _pushedCommand;
        _pushedCommand = null;
        return !string.IsNullOrWhiteSpace(command);
    }

    private WorkerPendingCaptchaSessionDto? TryConsumePushedCaptchaSession()
    {
        var session = _pushedCaptchaSession;
        _pushedCaptchaSession = null;
        return session;
    }

    private void TryLaunchBrowserMonitor(
        WorkerPendingBrowserMonitorSessionDto? configPending,
        CancellationToken stoppingToken)
    {
        var pending = _pushedBrowserMonitorSession ?? configPending;
        if (pending is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _browserMonitorLaunching, 1, 0) != 0)
        {
            return;
        }

        if (ReferenceEquals(pending, _pushedBrowserMonitorSession))
        {
            _pushedBrowserMonitorSession = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await BrowserMonitorWorkerLog.InfoAsync(
                    $"Browser monitor: оркестратор запускает стрим сессии {pending.SessionId:D}, браузеров в pending: {pending.Browsers.Count}, регистраций на воркере: {browserMonitorSource.GetRegistrations().Count}.",
                    nameof(TryLaunchBrowserMonitor),
                    new Dictionary<string, object?>
                    {
                        ["browserMonitor.sessionId"] = pending.SessionId,
                        ["browserMonitor.pendingBrowserCount"] = pending.Browsers.Count,
                        ["browserMonitor.registrationCount"] = browserMonitorSource.GetRegistrations().Count
                    }).ConfigureAwait(false);

                await browserMonitorCoordinator
                    .TryRunSessionAsync(pending, stoppingToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _browserMonitorLaunching, 0);
                realtime.RequestWake();
            }
        }, stoppingToken);
    }

    private async Task<bool> TryHandlePauseCommandAsync(string? command, CancellationToken stoppingToken)
    {
        if (!string.Equals(command, WorkerCommands.Pause, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await EnsureWorkerPausedAsync(stoppingToken).ConfigureAwait(false);
        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: получена команда приостановки из панели",
            nameof(TryHandlePauseCommandAsync),
            new Dictionary<string, object?>
            {
                ["worker.realtime"] = realtime.IsConnected
            })
            .ConfigureAwait(false);

        if (realtime.IsConnected)
        {
            _ = realtime.TryAckCommandAsync(WorkerCommands.Pause, stoppingToken);
        }

        return true;
    }

    private async Task EnsureWorkerPausedAsync(CancellationToken stoppingToken)
    {
        _pausedByPanel = true;
        RequestStopMonitoring();
        captchaCoordinator.CancelCurrentSession();
        browserMonitorCoordinator.CancelCurrentSession();
        configProvider.InvalidateCache();

        if (monitoringService.IsActive)
        {
            await monitoringService.StopAsync().ConfigureAwait(false);
            await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
            await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
        }

        runtimeState.IsMonitoring = false;
        updateGate.SetMonitoringActive(false);
        WorkerConnectionErrors.TryApplyUnauthorized(runtimeState);
    }

    private async Task<bool> TryHandleRestartCommandAsync(
        string? command,
        Guid? workerId,
        CancellationToken stoppingToken)
    {
        if (!string.Equals(command, WorkerCommands.Restart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        runtimeState.Status = "Перезапуск";
        runtimeState.Detail = "По команде из панели";
        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: получена команда перезапуска из панели",
            nameof(ExecuteAsync),
            new Dictionary<string, object?>
            {
                ["worker.id"] = workerId,
                ["worker.realtime"] = realtime.IsConnected
            })
            .ConfigureAwait(false);

        if (!shutdownService.RequestRestart())
        {
            await WorkerLifecycleLog.WarningAsync(
                "Worker lifecycle: команда перезапуска не применена",
                nameof(ExecuteAsync),
                new Dictionary<string, object?> { ["worker.id"] = workerId })
                .ConfigureAwait(false);
        }
        else if (realtime.IsConnected)
        {
            _ = realtime.TryAckCommandAsync(WorkerCommands.Restart, stoppingToken);
        }

        return true;
    }

    private async Task WaitNextIterationAsync(CancellationToken stoppingToken)
    {
        var timeout = realtime.IsConnected ? ConnectedLoopInterval : DisconnectedLoopInterval;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await realtime.WakeReader.ReadAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Timeout elapsed — regular fallback poll cycle.
        }
    }

    private async Task SyncMonitoringStateAsync(
        WorkerConfigDto config,
        int enabledCount,
        CancellationToken stoppingToken)
    {
        var fingerprint = BuildEnabledAccountsFingerprint(config);
        if (!string.Equals(fingerprint, _enabledAccountsFingerprint, StringComparison.Ordinal))
        {
            _enabledAccountsFingerprint = fingerprint;
            _enabledAccountsChangedAtUtc = DateTime.UtcNow;
            configProvider.InvalidateCache();
        }

        var hasEnabledAccounts = enabledCount > 0;
        var debounceElapsed = DateTime.UtcNow - _enabledAccountsChangedAtUtc >= MonitoringStartDebounce;

        if (!hasEnabledAccounts)
        {
            if (monitoringService.IsActive)
            {
                await monitoringService.StopAsync().ConfigureAwait(false);
                await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
            }

            runtimeState.IsMonitoring = false;
            updateGate.SetMonitoringActive(false);
            runtimeState.Detail = "Нет активных аккаунтов";
            activityReporter.ReportNoEnabledAccounts();
            return;
        }

        if (!_monitoringRequested)
        {
            if (monitoringService.IsActive)
            {
                await monitoringService.StopAsync().ConfigureAwait(false);
                await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
                await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
            }

            runtimeState.IsMonitoring = false;
            updateGate.SetMonitoringActive(false);
            runtimeState.Detail = $"{enabledCount} акк.";
            activityReporter.ReportIdle();
            return;
        }

        if (!monitoringService.IsActive)
        {
            if (!debounceElapsed)
            {
                var waitSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling((MonitoringStartDebounce - (DateTime.UtcNow - _enabledAccountsChangedAtUtc)).TotalSeconds));
                runtimeState.IsMonitoring = false;
                updateGate.SetMonitoringActive(false);
                runtimeState.Detail = $"{enabledCount} акк. · старт через ~{waitSeconds} с";
                activityReporter.ReportWaiting(
                    DateTime.UtcNow.AddSeconds(waitSeconds),
                    $"Ожидание старта · {enabledCount} акк.");
                return;
            }

            await monitoringService.StartAsync(stoppingToken).ConfigureAwait(false);
            runtimeState.IsMonitoring = true;
            updateGate.SetMonitoringActive(true);
            runtimeState.Detail = $"{enabledCount} акк.";
            return;
        }

        updateGate.SetMonitoringActive(true);
        runtimeState.Detail = $"{enabledCount} акк.";
    }

    private static string BuildEnabledAccountsFingerprint(WorkerConfigDto config)
    {
        var enabledIds = config.Accounts
            .Where(a => a.IsEnabled && config.IsBrowserProviderEnabled(a))
            .Select(static a => a.AccountId)
            .OrderBy(static x => x);
        return string.Join(',', enabledIds)
            + $"|ads:{config.AdsPowerEnabled}|mlx:{config.MultiloginEnabled}|local:{config.LocalChromeEnabled}";
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
            profiles = await adsPowerApi.ListProfilesAsync(options, ct, config.AdsPowerGroupId)
                .ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        IReadOnlyList<AdsPowerGroupDto>? groups = null;
        try
        {
            groups = (await adsPowerApi.ListGroupsAsync(options, ct).ConfigureAwait(false))
                .Select(g => new AdsPowerGroupDto(g.GroupId, g.GroupName))
                .ToList();
        }
        catch
        {
            // Группы — справочник для панели; без них всё равно синхронизируем профили.
        }

        var items = profiles
            .Select(p => new WorkerAccountSyncItemDto(p.UserId, p.Name, p.GroupId, p.GroupName))
            .ToList();

        await apiClient.SyncAccountsAsync(new WorkerAccountSyncRequest(items, groups), ct)
            .ConfigureAwait(false);
    }

    private async Task SyncMultiloginProfilesAsync(WorkerConfigDto config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.MultiloginAutomationToken))
        {
            return;
        }

        var options = new MultiloginConnectionOptions
        {
            CloudApiUrl = string.IsNullOrWhiteSpace(config.MultiloginCloudApiUrl)
                ? MultiloginWorkerSettings.DefaultCloudApiUrl
                : config.MultiloginCloudApiUrl,
            AutomationToken = config.MultiloginAutomationToken,
            LauncherUrl = config.MultiloginLauncherUrl
        };

        MultiloginProfileSearchResult catalog;
        try
        {
            catalog = await multiloginApi.SearchProfilesAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (!catalog.IsComplete && catalog.Profiles.Count == 0)
        {
            return;
        }

        var items = catalog.Profiles
            .Select(static p => new WorkerAccountSyncItemDto(
                AdsPowerProfileId: string.Empty,
                DisplayName: p.Name,
                MultiloginProfileId: p.ProfileId,
                MultiloginFolderId: p.FolderId,
                MultiloginProfileName: p.Name))
            .ToList();

        try
        {
            await apiClient.SyncAccountsAsync(
                    new WorkerAccountSyncRequest(
                        items,
                        Multilogin: true,
                        ReplaceMultiloginCatalog: catalog.IsComplete),
                    ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // AdsPower sync already completed; Multilogin catalog is best-effort.
        }
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

        if (!await realtime.TrySendHeartbeatAsync(heartbeat, ct).ConfigureAwait(false))
        {
            await apiClient.SendHeartbeatAsync(heartbeat, ct).ConfigureAwait(false);
        }
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