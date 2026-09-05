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
    LocalChromeLoginCoordinator localChromeLoginCoordinator,
    TopUpSessionCoordinator topUpSessionCoordinator,
    IWorkerRealtimeChannel realtime) : BackgroundService
{
    private bool _monitoringRequested = true;
    private bool _pausedByUnauthorized;
    private bool _pauseCommandThisIteration;
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
    private WorkerPendingLocalChromeLoginDto? _pushedLocalChromeLogin;
    private WorkerPendingTopUpSessionDto? _pushedTopUpSession;

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
        realtime.LocalChromeLoginSessionReceived += OnLocalChromeLoginSessionReceived;
        realtime.TopUpSessionReceived += OnTopUpSessionReceived;

        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: оркестратор запущен",
            nameof(ExecuteAsync))
            .ConfigureAwait(false);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _pauseCommandThisIteration = false;
                try
                {
                    if (TryConsumePushedCommand(out var pushedCommand))
                    {
                        await TryHandlePauseCommandAsync(pushedCommand, stoppingToken).ConfigureAwait(false);
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
                            await EnsureWorkerUnauthorizedAsync(stoppingToken).ConfigureAwait(false);
                        }
                        else
                        {
                            runtimeState.Status = WorkerConnectionErrors.ErrorStatus;
                            runtimeState.Detail = apiClient.LastConfigError ?? "Нет связи с API";
                        }

                        await WaitNextIterationAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    if (_pausedByUnauthorized)
                    {
                        _pausedByUnauthorized = false;
                        RequestStartMonitoring();
                    }

                    credentials.WorkerId ??= config.WorkerId;
                    credentials.DisplayName ??= Environment.MachineName;

                    updateOfferSource.SetOffer(config.UpdateOffer);

                    var command = TryConsumePushedCommand(out var pushed) ? pushed : config.PendingCommand;
                    await TryHandlePauseCommandAsync(command, stoppingToken).ConfigureAwait(false);

                    if (await TryHandleRestartCommandAsync(command, config.WorkerId, stoppingToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    var monitoringPaused = config.IsMonitoringPaused || _pauseCommandThisIteration;
                    var enabledCount = config.Accounts.Count(a => a.IsEnabled && config.IsBrowserProviderEnabled(a));
                    runtimeState.Status = "Онлайн";

                    await TryHandleRunMonitoringPassCommandAsync(
                            command,
                            config.WorkerId,
                            monitoringPaused,
                            stoppingToken)
                        .ConfigureAwait(false);

                    var forcedCatalogSync = await RunPendingProviderJobsAsync(config, stoppingToken)
                        .ConfigureAwait(false);

                    var groupChanged = !string.Equals(
                        _lastSyncedAdsPowerGroupId,
                        config.AdsPowerGroupId,
                        StringComparison.Ordinal);
                    if (!forcedCatalogSync
                        && (groupChanged || DateTime.UtcNow - _lastAccountSyncUtc >= AccountSyncInterval))
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
                    TryLaunchLocalChromeLogin(
                        config.PendingLocalChromeLogin,
                        config,
                        stoppingToken);
                    TryLaunchTopUpSession(config, stoppingToken);

                    await SyncMonitoringStateAsync(config, enabledCount, monitoringPaused, stoppingToken)
                        .ConfigureAwait(false);

                    if (!monitoringPaused
                        && pendingUpdateCoordinator.HasPendingInstall
                        && pendingUpdateCoordinator.TryApplyPendingInstallAtPause())
                    {
                        return;
                    }

                    if (!monitoringPaused
                        && updateStore.TryGetPendingMsi() is { } pendingMsi
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
            realtime.LocalChromeLoginSessionReceived -= OnLocalChromeLoginSessionReceived;
            realtime.TopUpSessionReceived -= OnTopUpSessionReceived;
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

    private void OnLocalChromeLoginSessionReceived(WorkerPendingLocalChromeLoginDto session)
    {
        _pushedLocalChromeLogin = session;
        configProvider.InvalidateCache();
        realtime.RequestWake();
    }

    private void OnTopUpSessionReceived(WorkerPendingTopUpSessionDto session)
    {
        _pushedTopUpSession = session;
        configProvider.InvalidateCache();
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

    private void TryLaunchLocalChromeLogin(
        WorkerPendingLocalChromeLoginDto? configPending,
        WorkerConfigDto config,
        CancellationToken stoppingToken)
    {
        var pending = _pushedLocalChromeLogin ?? configPending;
        if (pending is null || localChromeLoginCoordinator.IsRunning)
        {
            return;
        }

        if (ReferenceEquals(pending, _pushedLocalChromeLogin))
        {
            _pushedLocalChromeLogin = null;
        }

        _ = Task.Run(async () =>
        {
            await localChromeLoginCoordinator
                .TryRunPendingSessionAsync(pending, config, stoppingToken)
                .ConfigureAwait(false);
        }, stoppingToken);
    }

    private void TryLaunchTopUpSession(WorkerConfigDto config, CancellationToken stoppingToken)
    {
        var pending = _pushedTopUpSession ?? config.PendingTopUpSession;
        if (pending is null || topUpSessionCoordinator.IsRunning)
        {
            return;
        }

        if (ReferenceEquals(pending, _pushedTopUpSession))
        {
            _pushedTopUpSession = null;
        }

        _ = Task.Run(async () =>
        {
            await topUpSessionCoordinator
                .TryRunPendingSessionAsync(pending, config, stoppingToken)
                .ConfigureAwait(false);
        }, stoppingToken);
    }

    private async Task TryHandlePauseCommandAsync(string? command, CancellationToken stoppingToken)
    {
        if (!string.Equals(command, WorkerCommands.Pause, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pauseCommandThisIteration = true;
        await StopPrimaryMonitoringAsync(stoppingToken).ConfigureAwait(false);
        await WorkerLifecycleLog.InfoAsync(
            "Worker lifecycle: получена команда паузы мониторинга из панели",
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
    }

    private async Task EnsureWorkerUnauthorizedAsync(CancellationToken stoppingToken)
    {
        _pausedByUnauthorized = true;
        RequestStopMonitoring();
        captchaCoordinator.CancelCurrentSession();
        browserMonitorCoordinator.CancelCurrentSession();
        configProvider.InvalidateCache();
        await StopPrimaryMonitoringAsync(stoppingToken).ConfigureAwait(false);
        WorkerConnectionErrors.TryApplyUnauthorized(runtimeState);
    }

    private async Task StopPrimaryMonitoringAsync(CancellationToken stoppingToken)
    {
        if (monitoringService.IsActive)
        {
            await monitoringService.StopAsync().ConfigureAwait(false);
            await candidateSink.FlushAsync(stoppingToken).ConfigureAwait(false);
            await eventSink.FlushAsync(stoppingToken).ConfigureAwait(false);
        }

        runtimeState.IsMonitoring = false;
        updateGate.SetMonitoringActive(false);
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

    private async Task TryHandleRunMonitoringPassCommandAsync(
        string? command,
        Guid workerId,
        bool monitoringPaused,
        CancellationToken stoppingToken)
    {
        if (!string.Equals(command, WorkerCommands.RunMonitoringPass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (monitoringPaused)
        {
            runtimeState.Detail = "Проход не запущен: мониторинг на паузе";
            await WorkerLifecycleLog.WarningAsync(
                "Worker lifecycle: команда немедленного прохода отклонена — мониторинг на паузе",
                nameof(TryHandleRunMonitoringPassCommandAsync),
                new Dictionary<string, object?> { ["worker.id"] = workerId })
                .ConfigureAwait(false);
        }
        else
        {
            monitoringService.RequestImmediatePass();
            runtimeState.Detail = "Запуск прохода по команде из панели";
            await WorkerLifecycleLog.InfoAsync(
                "Worker lifecycle: получена команда немедленного прохода из панели",
                nameof(TryHandleRunMonitoringPassCommandAsync),
                new Dictionary<string, object?> { ["worker.id"] = workerId })
                .ConfigureAwait(false);
        }

        if (realtime.IsConnected)
        {
            _ = realtime.TryAckCommandAsync(WorkerCommands.RunMonitoringPass, stoppingToken);
        }
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
        bool monitoringPaused,
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

        if (monitoringPaused)
        {
            await StopPrimaryMonitoringAsync(stoppingToken).ConfigureAwait(false);
            runtimeState.Detail = "Пауза мониторинга";
            activityReporter.ReportStopped();
            return;
        }

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

    private async Task<bool> RunPendingProviderJobsAsync(WorkerConfigDto config, CancellationToken ct)
    {
        var probe = new BrowserProviderConnectionProbe(adsPowerApi, multiloginApi);
        if (config.PendingProviderCheck is { } pendingCheck)
        {
            await RunProviderCheckAsync(config, probe, pendingCheck.Provider, ct).ConfigureAwait(false);
        }

        if (config.PendingProviderSync is { } pendingSync)
        {
            await RunProviderSyncAsync(config, probe, pendingSync.Provider, ct).ConfigureAwait(false);
            _lastAccountSyncUtc = DateTime.UtcNow;
            _lastSyncedAdsPowerGroupId = config.AdsPowerGroupId;
            return true;
        }

        return false;
    }

    private async Task RunProviderCheckAsync(
        WorkerConfigDto config,
        BrowserProviderConnectionProbe probe,
        string provider,
        CancellationToken ct)
    {
        BrowserProviderProbeResult result;
        if (string.Equals(provider, WorkerBrowserProviderKinds.AdsPower, StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = string.IsNullOrWhiteSpace(config.AdsPowerApiBaseUrl)
                ? "http://local.adspower.net:50325"
                : config.AdsPowerApiBaseUrl;
            result = await probe
                .CheckAdsPowerAsync(new AdsPowerConnectionOptions(baseUrl, config.AdsPowerApiKey), config.AdsPowerGroupId, ct)
                .ConfigureAwait(false);
        }
        else if (string.Equals(provider, WorkerBrowserProviderKinds.Multilogin, StringComparison.OrdinalIgnoreCase))
        {
            result = await probe
                .CheckMultiloginAsync(
                    new MultiloginConnectionOptions
                    {
                        CloudApiUrl = config.MultiloginCloudApiUrl,
                        AutomationToken = config.MultiloginAutomationToken,
                        LauncherUrl = config.MultiloginLauncherUrl
                    },
                    ct)
                .ConfigureAwait(false);
        }
        else if (string.Equals(provider, WorkerBrowserProviderKinds.Local, StringComparison.OrdinalIgnoreCase))
        {
            result = probe.CheckLocalChrome(config.LocalChromeExecutablePath);
        }
        else
        {
            return;
        }

        await apiClient
            .ReportProviderCheckAsync(
                new ReportWorkerBrowserProviderCheckRequest(
                    provider,
                    result.Success,
                    result.Message,
                    result.ProfileCount,
                    result.GroupCount,
                    result.ResolvedExecutablePath,
                    Groups: result.Groups),
                ct)
            .ConfigureAwait(false);
        configProvider.InvalidateCache();
    }

    private async Task RunProviderSyncAsync(
        WorkerConfigDto config,
        BrowserProviderConnectionProbe probe,
        string provider,
        CancellationToken ct)
    {
        bool synced;
        BrowserProviderProbeResult? check = null;
        if (string.Equals(provider, WorkerBrowserProviderKinds.AdsPower, StringComparison.OrdinalIgnoreCase))
        {
            synced = await SyncAdsPowerProfilesAsync(config, ct).ConfigureAwait(false);
            if (synced)
            {
                var baseUrl = string.IsNullOrWhiteSpace(config.AdsPowerApiBaseUrl)
                    ? "http://local.adspower.net:50325"
                    : config.AdsPowerApiBaseUrl;
                check = await probe
                    .CheckAdsPowerAsync(new AdsPowerConnectionOptions(baseUrl, config.AdsPowerApiKey), config.AdsPowerGroupId, ct)
                    .ConfigureAwait(false);
            }
        }
        else if (string.Equals(provider, WorkerBrowserProviderKinds.Multilogin, StringComparison.OrdinalIgnoreCase))
        {
            synced = await SyncMultiloginProfilesAsync(config, ct).ConfigureAwait(false);
            if (synced)
            {
                check = await probe
                    .CheckMultiloginAsync(
                        new MultiloginConnectionOptions
                        {
                            CloudApiUrl = config.MultiloginCloudApiUrl,
                            AutomationToken = config.MultiloginAutomationToken,
                            LauncherUrl = config.MultiloginLauncherUrl
                        },
                        ct)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            return;
        }

        var message = synced
            ? check?.Message ?? "Каталог синхронизирован."
            : "Не удалось синхронизировать каталог.";
        await apiClient
            .ReportProviderCheckAsync(
                new ReportWorkerBrowserProviderCheckRequest(
                    provider,
                    synced && (check?.Success ?? true),
                    message,
                    check?.ProfileCount,
                    check?.GroupCount,
                    CompletesSync: true,
                    Groups: check?.Groups),
                ct)
            .ConfigureAwait(false);
        configProvider.InvalidateCache();
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

    private async Task<bool> SyncAdsPowerProfilesAsync(WorkerConfigDto config, CancellationToken ct)
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
            return false;
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
        return true;
    }

    private async Task<bool> SyncMultiloginProfilesAsync(WorkerConfigDto config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.MultiloginAutomationToken))
        {
            return false;
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
            return false;
        }

        if (!catalog.IsComplete && catalog.Profiles.Count == 0)
        {
            return false;
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
            return true;
        }
        catch
        {
            return false;
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
