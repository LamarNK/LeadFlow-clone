using PuppeteerSharp;

namespace LeadFlow.Core.Services.Multilogin;

internal sealed class PuppeteerMultiloginConnectedBrowser(IBrowser browser) : IMultiloginConnectedBrowser
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    public bool IsConnected => browser.IsConnected;

    public IBrowser Browser => browser;

    public async Task EnsureResponsiveAsync(CancellationToken cancellationToken = default)
    {
        if (!browser.IsConnected)
        {
            throw new InvalidOperationException("Multilogin CDP: соединение не установлено.");
        }

        try
        {
            _ = await browser.PagesAsync().WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("Multilogin CDP: браузер не отвечает.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Multilogin CDP: браузер не отвечает.", ex);
        }

        if (!browser.IsConnected)
        {
            throw new InvalidOperationException("Multilogin CDP: соединение не установлено.");
        }
    }

    public void Disconnect()
    {
        try
        {
            browser.Disconnect();
        }
        catch
        {
            // Disconnect must never throw out of cleanup.
        }
    }
}
