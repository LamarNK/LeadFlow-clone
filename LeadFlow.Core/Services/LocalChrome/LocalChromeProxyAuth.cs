using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Авторизация HTTP-прокси на рабочей вкладке до навигации на Avito.
/// Логин и пароль не попадают в launch args — только <c>page.AuthenticateAsync</c>.
/// </summary>
public static class LocalChromeProxyAuth
{
    public static async Task ApplyAsync(
        IBrowser browser,
        LocalChromeLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.HasProxyCredentials)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IPage[] pages;
        try
        {
            pages = await browser.PagesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                LocalChromeProxyRules.SanitizeError(
                    "Не удалось подготовить прокси обычного браузера.",
                    options.ProxyUsername,
                    options.ProxyPassword),
                ex);
        }

        var credentials = new Credentials
        {
            Username = options.ProxyUsername ?? string.Empty,
            Password = options.ProxyPassword ?? string.Empty
        };

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await page.AuthenticateAsync(credentials).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    LocalChromeProxyRules.SanitizeError(
                        "Не удалось авторизовать прокси обычного браузера.",
                        options.ProxyUsername,
                        options.ProxyPassword),
                    ex);
            }
        }
    }
}
