namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Start → CDP connect по BrowserURL → work → stop в finally.
/// Ошибки не прогоняются через AdsPower-классификаторы.
/// </summary>
public sealed class MultiloginCdpConnector : IMultiloginCdpConnector
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

    private readonly IMultiloginApiClient _apiClient;
    private readonly IMultiloginBrowserConnector _browserConnector;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _stopTimeout;

    public MultiloginCdpConnector(
        IMultiloginApiClient apiClient,
        IMultiloginBrowserConnector? browserConnector = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? stopTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        _apiClient = apiClient;
        _browserConnector = browserConnector ?? new PuppeteerMultiloginBrowserConnector();
        _connectTimeout = connectTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout
            : DefaultConnectTimeout;
        _stopTimeout = stopTimeout is { } stop && stop > TimeSpan.Zero
            ? stop
            : DefaultStopTimeout;
    }

    public async Task<IMultiloginCdpSession> OpenAsync(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var normalized = options.Normalized();
        var started = false;
        IMultiloginConnectedBrowser? browser = null;
        try
        {
            var start = await _apiClient
                .StartProfileAsync(normalized, folderId.Trim(), profileId.Trim(), cancellationToken)
                .ConfigureAwait(false);
            started = true;

            var browserUrl = ResolveBrowserUrl(start);
            browser = await ConnectAsync(browserUrl, start.WebSocketDebuggerUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!browser.IsConnected)
            {
                throw new InvalidOperationException("Multilogin CDP: соединение не установлено.");
            }

            await browser.EnsureResponsiveAsync(cancellationToken).ConfigureAwait(false);
            var session = new OpenedSession(browser, () => TryStopAsync(normalized, profileId.Trim()));
            browser = null;
            started = false;
            return session;
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    browser.Disconnect();
                }
                catch
                {
                    // Disconnect must never throw out of finally.
                }
            }

            if (started)
            {
                await TryStopAsync(normalized, profileId.Trim()).ConfigureAwait(false);
            }
        }
    }

    public async Task<T> RunAsync<T>(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        Func<IMultiloginConnectedBrowser, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var session = await OpenAsync(options, folderId, profileId, cancellationToken)
            .ConfigureAwait(false);
        return await work(session.Connected, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMultiloginConnectedBrowser> ConnectAsync(
        string browserUrl,
        string? webSocketDebuggerUrl,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_connectTimeout);
        var connectTask = _browserConnector.ConnectAsync(
            browserUrl,
            webSocketDebuggerUrl,
            _connectTimeout,
            linkedCts.Token);
        try
        {
            return await connectTask.ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            ObserveAbandonedConnect(connectTask);
            throw new TimeoutException(
                $"Multilogin CDP: подключение не открылось за {_connectTimeout.TotalSeconds:0} с.",
                ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveAbandonedConnect(connectTask);
            throw new TimeoutException(
                $"Multilogin CDP: подключение не открылось за {_connectTimeout.TotalSeconds:0} с.",
                ex);
        }
        catch
        {
            ObserveAbandonedConnect(connectTask);
            throw;
        }
    }

    private async Task TryStopAsync(MultiloginConnectionOptions options, string profileId)
    {
        try
        {
            using var stopCts = new CancellationTokenSource(_stopTimeout);
            await _apiClient
                .StopProfileAsync(options, profileId, stopCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Stop must not hide the original start/connect/work error
            // and must still run after the caller cancels the outer token.
        }
    }

    private static void ObserveAbandonedConnect(Task<IMultiloginConnectedBrowser> connectTask)
    {
        _ = connectTask.ContinueWith(
            static task =>
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                    return;
                }

                if (!task.IsCompletedSuccessfully)
                {
                    return;
                }

                try
                {
                    task.Result.Disconnect();
                }
                catch
                {
                    // Abandoned connect must not throw on a background thread.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static string ResolveBrowserUrl(MultiloginBrowserStartResult start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Port is not int port || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Multilogin profile/start: нет valid port.");
        }

        if (!string.IsNullOrWhiteSpace(start.BrowserUrl))
        {
            return start.BrowserUrl.Trim();
        }

        return $"http://127.0.0.1:{port}";
    }

    private sealed class OpenedSession(
        IMultiloginConnectedBrowser connected,
        Func<Task> stopAsync) : IMultiloginCdpSession
    {
        public IMultiloginConnectedBrowser Connected { get; } = connected;

        public async ValueTask DisposeAsync()
        {
            try
            {
                Connected.Disconnect();
            }
            catch
            {
                // Disconnect must never throw out of Dispose.
            }

            await stopAsync().ConfigureAwait(false);
        }
    }
}
