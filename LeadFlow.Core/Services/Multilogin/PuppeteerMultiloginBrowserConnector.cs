using PuppeteerSharp;

namespace LeadFlow.Core.Services.Multilogin;

/// <summary>
/// Подключение PuppeteerSharp к launcher Multilogin X по <c>BrowserURL</c>
/// (<c>http://127.0.0.1:{port}</c>). В PuppeteerSharp 24.42.0 у <see cref="ConnectOptions"/>
/// нет <c>Timeout</c> — лимит задаётся через <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>.
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

        var connectTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(15);
        var connectTask = Puppeteer.ConnectAsync(CreateConnectOptions(browserUrl));
        try
        {
            var browser = await connectTask
                .WaitAsync(connectTimeout, cancellationToken)
                .ConfigureAwait(false);
            return new PuppeteerMultiloginConnectedBrowser(browser);
        }
        catch
        {
            ObserveAbandonedConnect(connectTask);
            throw;
        }
    }

    public static ConnectOptions CreateConnectOptions(string browserUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(browserUrl);
        return new ConnectOptions
        {
            BrowserURL = browserUrl.Trim(),
            DefaultViewport = null
        };
    }

    private static void ObserveAbandonedConnect(Task<IBrowser> connectTask)
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
}
