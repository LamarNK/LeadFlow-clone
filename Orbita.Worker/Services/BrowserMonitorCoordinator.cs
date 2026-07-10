using LeadFlow.Core.Services.Worker;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class BrowserMonitorCoordinator(
    WorkerCredentials credentials,
    BrowserMonitorSource browserMonitorSource,
    OrbitaApiClient apiClient,
    ILogger<BrowserMonitorCoordinator> logger)
{
    private const int MaxMonitoredBrowsers = 24;
    private const int HubReconnectDelayMs = 2_000;

    private readonly object _sync = new();
    private CancellationTokenSource? _sessionCts;
    private volatile bool _isRunning;
    private int _framesSent;
    private Guid _activeSessionId;

    public bool IsRunning => _isRunning;

    public void CancelCurrentSession()
    {
        lock (_sync)
        {
            _sessionCts?.Cancel();
        }
    }

    public async Task<bool> TryRunSessionAsync(
        WorkerPendingBrowserMonitorSessionDto pending,
        CancellationToken cancellationToken)
    {
        CancelCurrentSession();

        CancellationTokenSource linkedCts;
        lock (_sync)
        {
            _sessionCts?.Dispose();
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _sessionCts = linkedCts;
            _activeSessionId = pending.SessionId;
        }

        await BrowserMonitorWorkerLog.InfoAsync(
            $"Browser monitor: запуск стрима сессии {pending.SessionId:D}, ожидаемых браузеров в API: {pending.Browsers.Count}.",
            nameof(TryRunSessionAsync),
            new Dictionary<string, object?>
            {
                ["browserMonitor.sessionId"] = pending.SessionId,
                ["browserMonitor.pendingBrowserCount"] = pending.Browsers.Count
            }).ConfigureAwait(false);

        Interlocked.Exchange(ref _framesSent, 0);
        browserMonitorSource.BeginSession();
        try
        {
            _isRunning = true;
            while (!linkedCts.Token.IsCancellationRequested)
            {
                if (!await apiClient.IsBrowserMonitorSessionAliveAsync(pending.SessionId, linkedCts.Token)
                        .ConfigureAwait(false))
                {
                    logger.LogInformation(
                        "Browser monitor session {SessionId} is no longer alive; stopping worker stream.",
                        pending.SessionId);
                    break;
                }

                try
                {
                    await using var hubConnection = BuildHubConnection(credentials.ApiKey);
                    hubConnection.Reconnected += connectionId =>
                    {
                        _ = RejoinWorkerHubAsync(hubConnection, pending.SessionId, connectionId);
                        return Task.CompletedTask;
                    };

                    var hubUrl = $"{credentials.ApiBaseUrl.TrimEnd('/')}/hubs/browser-monitor";
                    await hubConnection.StartAsync(linkedCts.Token).ConfigureAwait(false);
                    await hubConnection.InvokeAsync("JoinAsWorker", pending.SessionId, linkedCts.Token)
                        .ConfigureAwait(false);

                    await BrowserMonitorWorkerLog.InfoAsync(
                        $"Browser monitor: hub подключён ({hubUrl}), JoinAsWorker OK для {pending.SessionId:D}.",
                        nameof(TryRunSessionAsync),
                        new Dictionary<string, object?>
                        {
                            ["browserMonitor.sessionId"] = pending.SessionId,
                            ["browserMonitor.hubUrl"] = hubUrl,
                            ["browserMonitor.registrationCount"] = browserMonitorSource.GetRegistrations().Count
                        }).ConfigureAwait(false);

                    var lastFrameAtByAccount = new Dictionary<Guid, long>();
                    await RunCaptureLoopAsync(
                        hubConnection,
                        pending,
                        lastFrameAtByAccount,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Browser monitor hub disconnected for session {SessionId}; retrying.",
                        pending.SessionId);
                    await BrowserMonitorWorkerLog.WarningAsync(
                        $"Browser monitor: ошибка hub для сессии {pending.SessionId:D}: {ex.Message}",
                        nameof(TryRunSessionAsync),
                        new Dictionary<string, object?>
                        {
                            ["browserMonitor.sessionId"] = pending.SessionId,
                            ["browserMonitor.hubUrl"] = $"{credentials.ApiBaseUrl.TrimEnd('/')}/hubs/browser-monitor"
                        }).ConfigureAwait(false);
                }

                if (linkedCts.Token.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(HubReconnectDelayMs, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            return true;
        }
        finally
        {
            _isRunning = false;
            browserMonitorSource.EndSession();
            lock (_sync)
            {
                if (ReferenceEquals(_sessionCts, linkedCts))
                {
                    _sessionCts.Dispose();
                    _sessionCts = null;
                }
            }
        }
    }

    private static async Task RejoinWorkerHubAsync(
        HubConnection hubConnection,
        Guid sessionId,
        string? connectionId)
    {
        try
        {
            await hubConnection.InvokeAsync("JoinAsWorker", sessionId).ConfigureAwait(false);
        }
        catch
        {
            // best effort rejoin after reconnect
        }
    }

    private async Task RunCaptureLoopAsync(
        HubConnection hubConnection,
        WorkerPendingBrowserMonitorSessionDto pending,
        Dictionary<Guid, long> lastFrameAtByAccount,
        CancellationToken cancellationToken)
    {
        const int catalogIntervalMs = 2_000;
        const int frameIntervalMs = 900;
        const int aliveIntervalMs = 5_000;
        var nextCatalogAt = 0L;
        var nextFrameAt = 0L;
        var nextAliveAt = 0L;
        var runtimeByAccount = new Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)>();
        string? lastRegistrationFingerprint = null;
        var emptyRegistrationWarnings = 0;
        var framesSentTotal = 0;

        while (!cancellationToken.IsCancellationRequested
               && hubConnection.State == HubConnectionState.Connected)
        {
            var now = Environment.TickCount64;

            if (now >= nextAliveAt)
            {
                nextAliveAt = now + aliveIntervalMs;
                if (!await apiClient.IsBrowserMonitorSessionAliveAsync(pending.SessionId, cancellationToken)
                        .ConfigureAwait(false))
                {
                    break;
                }
            }

            var registrations = GetMonitoredRegistrations();
            var registrationFingerprint = BuildRegistrationFingerprint(registrations);
            var registrationsChanged = !string.Equals(
                registrationFingerprint,
                lastRegistrationFingerprint,
                StringComparison.Ordinal);

            if (registrationsChanged || now >= nextCatalogAt)
            {
                if (!registrationsChanged)
                {
                    nextCatalogAt = now + catalogIntervalMs;
                }

                PruneRuntimeState(runtimeByAccount, lastFrameAtByAccount, registrations);
                lastRegistrationFingerprint = registrationFingerprint;

                if (registrations.Count == 0 && emptyRegistrationWarnings < 5)
                {
                    emptyRegistrationWarnings++;
                    await BrowserMonitorWorkerLog.WarningAsync(
                        $"Browser monitor: нет зарегистрированных браузеров для сессии {pending.SessionId:D} (ожидание открытых AdsPower-сессий).",
                        nameof(RunCaptureLoopAsync),
                        new Dictionary<string, object?>
                        {
                            ["browserMonitor.sessionId"] = pending.SessionId,
                            ["browserMonitor.warningIndex"] = emptyRegistrationWarnings
                        }).ConfigureAwait(false);
                }

                if (!await TrySendCatalogAsync(
                        hubConnection,
                        pending.SessionId,
                        registrations,
                        lastFrameAtByAccount,
                        runtimeByAccount,
                        emptyRegistrationWarnings,
                        cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }

            if (now >= nextFrameAt)
            {
                nextFrameAt = now + frameIntervalMs;
                framesSentTotal += await SendFramesForRegistrationsAsync(
                    hubConnection,
                    pending.SessionId,
                    GetMonitoredRegistrations(),
                    runtimeByAccount,
                    lastFrameAtByAccount,
                    cancellationToken).ConfigureAwait(false);

                if (framesSentTotal > 0 && framesSentTotal % 30 == 0)
                {
                    await BrowserMonitorWorkerLog.InfoAsync(
                        $"Browser monitor: отправлено {framesSentTotal} кадров для сессии {pending.SessionId:D}.",
                        nameof(RunCaptureLoopAsync),
                        new Dictionary<string, object?>
                        {
                            ["browserMonitor.sessionId"] = pending.SessionId,
                            ["browserMonitor.framesSentTotal"] = framesSentTotal
                        }).ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> SendFramesForRegistrationsAsync(
        HubConnection hubConnection,
        Guid sessionId,
        IReadOnlyList<BrowserMonitorSource.BrowserRegistration> registrations,
        Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)> runtimeByAccount,
        Dictionary<Guid, long> lastFrameAtByAccount,
        CancellationToken cancellationToken)
    {
        if (registrations.Count == 0)
        {
            return 0;
        }

        var sentCount = 0;
        var frameTasks = registrations.Select(async registration =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var capture = await registration.CaptureAsync(cancellationToken).ConfigureAwait(false);
                if (capture?.JpegBytes is null || capture.JpegBytes.Length == 0)
                {
                    logger.LogDebug(
                        "Browser monitor empty capture for account {AccountId} ({AccountName}).",
                        registration.AccountId,
                        registration.AccountName);
                    return;
                }

                var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                runtimeByAccount[registration.AccountId] = (
                    capture.SubProfileId,
                    capture.SubProfileName,
                    capture.PageUrl);
                lastFrameAtByAccount[registration.AccountId] = timestampMs;

                await hubConnection.InvokeAsync(
                    "SendFrame",
                    new BrowserMonitorFrameMessage(
                        sessionId,
                        registration.AccountId,
                        Convert.ToBase64String(capture.JpegBytes),
                        CaptchaViewportDefaults.Width,
                        CaptchaViewportDefaults.Height,
                        timestampMs,
                        capture.PageUrl,
                        capture.SubProfileId,
                        capture.SubProfileName,
                        AccountName: registration.AccountName),
                    cancellationToken).ConfigureAwait(false);

                Interlocked.Increment(ref sentCount);
                var totalSent = Interlocked.Increment(ref _framesSent);
                if (totalSent == 1 || totalSent % 30 == 0)
                {
                    await BrowserMonitorWorkerLog.InfoAsync(
                        $"Отправлен кадр #{totalSent} для {registration.AccountName} ({registration.AccountId:D}), {capture.JpegBytes.Length} байт.",
                        nameof(SendFramesForRegistrationsAsync),
                        new Dictionary<string, object?>
                        {
                            ["browserMonitor.sessionId"] = sessionId,
                            ["browserMonitor.accountId"] = registration.AccountId,
                            ["browserMonitor.frameBytes"] = capture.JpegBytes.Length,
                            ["browserMonitor.framesSent"] = totalSent
                        }).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Browser monitor frame capture failed for account {AccountId} ({AccountName}) and AdsPower profile {AdsPowerProfileId}.",
                    registration.AccountId,
                    registration.AccountName,
                    registration.AdsPowerProfileId);
            }
        });

        await Task.WhenAll(frameTasks).ConfigureAwait(false);
        return sentCount;
    }

    private async Task<bool> TrySendCatalogAsync(
        HubConnection hubConnection,
        Guid sessionId,
        IReadOnlyList<BrowserMonitorSource.BrowserRegistration> registrations,
        IReadOnlyDictionary<Guid, long> lastFrameAtByAccount,
        IReadOnlyDictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)> runtimeByAccount,
        int emptyRegistrationWarnings,
        CancellationToken cancellationToken)
    {
        var browsers = BrowserMonitorCatalogBuilder.BuildActiveCatalog(
            registrations
                .Select(static registration => new BrowserMonitorCatalogBuilder.ActiveBrowserSeed(
                    registration.AccountId,
                    registration.AccountName,
                    registration.AdsPowerProfileId))
                .ToList(),
            lastFrameAtByAccount,
            runtimeByAccount);

        try
        {
            await hubConnection.InvokeAsync(
                "SendCatalog",
                new BrowserMonitorCatalogMessage(
                    sessionId,
                    browsers,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                cancellationToken).ConfigureAwait(false);

            if (browsers.Count > 0 || emptyRegistrationWarnings <= 1)
            {
                await BrowserMonitorWorkerLog.InfoAsync(
                    browsers.Count == 0
                        ? $"Browser monitor: отправлен пустой каталог для сессии {sessionId:D}."
                        : $"Browser monitor: каталог {browsers.Count} браузер(ов) для сессии {sessionId:D}.",
                    nameof(TrySendCatalogAsync),
                    new Dictionary<string, object?>
                    {
                        ["browserMonitor.sessionId"] = sessionId,
                        ["browserMonitor.catalogCount"] = browsers.Count,
                        ["browserMonitor.registrationCount"] = registrations.Count
                    }).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Browser monitor catalog send failed for session {SessionId}.", sessionId);
            await BrowserMonitorWorkerLog.WarningAsync(
                $"Browser monitor: не удалось отправить каталог для сессии {sessionId:D}: {ex.Message}",
                nameof(TrySendCatalogAsync),
                new Dictionary<string, object?>
                {
                    ["browserMonitor.sessionId"] = sessionId,
                    ["browserMonitor.catalogCount"] = browsers.Count
                }).ConfigureAwait(false);
            return false;
        }
    }

    private IReadOnlyList<BrowserMonitorSource.BrowserRegistration> GetMonitoredRegistrations()
    {
        var registrations = browserMonitorSource.GetRegistrations();
        if (registrations.Count <= MaxMonitoredBrowsers)
        {
            return registrations;
        }

        logger.LogWarning(
            "Browser monitor session {SessionId} has {RegistrationCount} registrations; streaming only the first {MaxMonitoredBrowsers}.",
            _activeSessionId,
            registrations.Count,
            MaxMonitoredBrowsers);

        return registrations.Take(MaxMonitoredBrowsers).ToList();
    }

    private static string BuildRegistrationFingerprint(
        IReadOnlyList<BrowserMonitorSource.BrowserRegistration> registrations) =>
        string.Join(
            '|',
            registrations
                .Select(static registration => registration.AccountId.ToString("D"))
                .OrderBy(static id => id, StringComparer.Ordinal));

    private static void PruneRuntimeState(
        Dictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)> runtimeByAccount,
        Dictionary<Guid, long> lastFrameAtByAccount,
        IReadOnlyList<BrowserMonitorSource.BrowserRegistration> registrations)
    {
        var activeIds = registrations
            .Select(static registration => registration.AccountId)
            .ToHashSet();

        foreach (var accountId in runtimeByAccount.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            runtimeByAccount.Remove(accountId);
        }

        foreach (var accountId in lastFrameAtByAccount.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            lastFrameAtByAccount.Remove(accountId);
        }
    }

    private HubConnection BuildHubConnection(string apiKey)
    {
        var baseUrl = credentials.ApiBaseUrl.TrimEnd('/');
        return new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/browser-monitor", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(apiKey);
                options.Headers["Authorization"] = $"Bearer {apiKey}";
            })
            .WithAutomaticReconnect()
            .Build();
    }
}