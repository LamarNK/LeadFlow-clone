using LeadFlow.Core.Services.Worker;
using Microsoft.AspNetCore.SignalR.Client;
using Orbita.Contracts;

namespace Orbita.Worker.Services;

public sealed class BrowserMonitorCoordinator(
    WorkerCredentials credentials,
    BrowserMonitorSource browserMonitorSource,
    OrbitaApiClient apiClient)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _sessionCts;
    private volatile bool _isRunning;
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

        browserMonitorSource.BeginSession();
        try
        {
            _isRunning = true;
            await using var hubConnection = BuildHubConnection(credentials.ApiKey);
            hubConnection.Reconnected += connectionId =>
            {
                _ = RejoinWorkerHubAsync(hubConnection, pending.SessionId, connectionId);
                return Task.CompletedTask;
            };

            await hubConnection.StartAsync(linkedCts.Token).ConfigureAwait(false);
            await hubConnection.InvokeAsync("JoinAsWorker", pending.SessionId, linkedCts.Token).ConfigureAwait(false);

            var lastFrameAtByAccount = new Dictionary<Guid, long>();
            await RunCaptureLoopAsync(
                hubConnection,
                pending,
                lastFrameAtByAccount,
                linkedCts.Token).ConfigureAwait(false);

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

            if (now >= nextCatalogAt)
            {
                nextCatalogAt = now + catalogIntervalMs;
                var browsers = BuildCatalog(pending.Browsers, browserMonitorSource, lastFrameAtByAccount, runtimeByAccount);
                try
                {
                    await hubConnection.InvokeAsync(
                        "SendCatalog",
                        new BrowserMonitorCatalogMessage(
                            pending.SessionId,
                            browsers,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }

            if (now >= nextFrameAt)
            {
                nextFrameAt = now + frameIntervalMs;
                foreach (var registration in browserMonitorSource.GetRegistrations())
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var capture = await registration.CaptureAsync(cancellationToken).ConfigureAwait(false);
                        if (capture?.JpegBytes is null || capture.JpegBytes.Length == 0)
                        {
                            continue;
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
                                pending.SessionId,
                                registration.AccountId,
                                Convert.ToBase64String(capture.JpegBytes),
                                CaptchaViewportDefaults.Width,
                                CaptchaViewportDefaults.Height,
                                timestampMs,
                                capture.PageUrl,
                                capture.SubProfileId,
                                capture.SubProfileName),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // best effort per browser tile
                    }
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

    private static IReadOnlyList<BrowserMonitorBrowserDto> BuildCatalog(
        IReadOnlyList<BrowserMonitorBrowserDto> seed,
        BrowserMonitorSource source,
        IReadOnlyDictionary<Guid, long> lastFrameAtByAccount,
        IReadOnlyDictionary<Guid, (string? SubProfileId, string? SubProfileName, string? PageUrl)> runtimeByAccount)
    {
        var runningIds = source.GetRegistrations()
            .Select(x => x.AccountId)
            .ToHashSet();

        return seed
            .Select(browser =>
            {
                var isRunning = runningIds.Contains(browser.AccountId);
                runtimeByAccount.TryGetValue(browser.AccountId, out var runtime);
                lastFrameAtByAccount.TryGetValue(browser.AccountId, out var lastFrameAtMs);

                return browser with
                {
                    Status = isRunning ? BrowserMonitorStatuses.Running : browser.Status,
                    PageUrl = runtime.PageUrl ?? browser.PageUrl,
                    SubProfileId = runtime.SubProfileId ?? browser.SubProfileId,
                    SubProfileName = runtime.SubProfileName ?? browser.SubProfileName,
                    LastFrameAtMs = lastFrameAtMs > 0 ? lastFrameAtMs : browser.LastFrameAtMs
                };
            })
            .ToList();
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