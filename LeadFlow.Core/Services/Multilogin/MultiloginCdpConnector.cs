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

    public async Task<T> RunAsync<T>(
        MultiloginConnectionOptions options,
        string folderId,
        string profileId,
        Func<IMultiloginConnectedBrowser, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(work);
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
            return await work(browser, cancellationToken).ConfigureAwait(false);
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

    private async Task<IMultiloginConnectedBrowser> ConnectAsync(
        string browserUrl,
        string? webSocketDebuggerUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _browserConnector
                .ConnectAsync(browserUrl, webSocketDebuggerUrl, _connectTimeout, cancellationToken)
                .WaitAsync(_connectTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Multilogin CDP: подключение не открылось за {_connectTimeout.TotalSeconds:0} с.",
                ex);
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
            // Stop must not hide the original start/connect/work error.
        }
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
}
