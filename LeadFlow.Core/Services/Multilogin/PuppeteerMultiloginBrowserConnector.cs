using PuppeteerSharp;

namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Подключение PuppeteerSharp к launcher Multilogin X по <c>BrowserURL</c>
/// (<c>http://127.0.0.1:{port}</c>), как в официальном puppeteer-примере.
/// </summary>
public sealed class PuppeteerMultiloginBrowserConnector : IMultiloginBrowserConnector
{
    public async Task<IMultiloginConnectedBrowser> ConnectAsync(
        string browserUrl,
        string? webSocketDebuggerUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserUrl);
        _ = webSocketDebuggerUrl;
        cancellationToken.ThrowIfCancellationRequested();

        var browser = await Puppeteer
            .ConnectAsync(CreateConnectOptions(browserUrl, timeout))
            .ConfigureAwait(false);
        return new PuppeteerMultiloginConnectedBrowser(browser);
    }

    public static ConnectOptions CreateConnectOptions(string browserUrl, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserUrl);
        var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        return new ConnectOptions
        {
            BrowserURL = browserUrl.Trim(),
            DefaultViewport = null,
            Timeout = milliseconds
        };
    }
}
