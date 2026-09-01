using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Открывает обычный Chrome для ручного входа в Avito. Не запускает мониторинг и Avito-automation.
/// </summary>
public sealed class LocalChromeLoginSessionRunner(
    ILocalChromeBrowserLauncher launcher,
    LocalChromeAccountLock accountLock)
{
    public async Task RunAsync(AvitoAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!WorkerAccountRuntime.IsLocalProvider(account))
        {
            throw new InvalidOperationException("Открыть браузер для входа можно только у аккаунта обычного браузера.");
        }

        if (!accountLock.TryAcquire(account.Id, LocalChromeAccountLock.Login, out var existing))
        {
            throw new InvalidOperationException(
                string.Equals(existing, LocalChromeAccountLock.Monitoring, StringComparison.Ordinal)
                    ? "Сначала дождитесь окончания мониторинга этого аккаунта."
                    : "Браузер для входа уже открыт.");
        }

        IBrowser? browser = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            browser = await launcher
                .LaunchAsync(LocalChromeLaunchOptionsFactory.FromAccount(account), cancellationToken)
                .ConfigureAwait(false);

            await NavigateToAvitoAsync(browser, cancellationToken).ConfigureAwait(false);
            await WaitUntilDisconnectedAsync(browser, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw SanitizeLaunchError(ex);
        }
        finally
        {
            await CloseOwnedBrowserAsync(browser).ConfigureAwait(false);
            accountLock.Release(account.Id, LocalChromeAccountLock.Login);
        }
    }

    internal static async Task NavigateToAvitoAsync(IBrowser browser, CancellationToken cancellationToken)
    {
        IPage[]? pages;
        try
        {
            pages = await browser.PagesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Не удалось открыть Avito в обычном браузере.", ex);
        }

        var page = pages is { Length: > 0 } ? pages[0] : null;
        if (page is null)
        {
            return;
        }

        try
        {
            await page
                .GoToAsync(
                    LocalChromeProxyRules.AvitoStartUrl,
                    new NavigationOptions
                    {
                        Timeout = 60_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("Не удалось открыть Avito в обычном браузере.", ex);
        }
    }

    internal static async Task WaitUntilDisconnectedAsync(IBrowser browser, CancellationToken cancellationToken)
    {
        while (browser.IsConnected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    private static InvalidOperationException SanitizeLaunchError(Exception ex)
    {
        if (ex is InvalidOperationException invalid
            && (invalid.Message.StartsWith("Обычный браузер", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Не найден установленный Chrome", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Файл браузера не найден", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Нельзя использовать стандартный профиль", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Укажите путь", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Не удалось открыть обычный браузер", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Не удалось авторизовать прокси", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Не удалось подготовить прокси", StringComparison.Ordinal)
                || invalid.Message.StartsWith("Не удалось открыть Avito", StringComparison.Ordinal)))
        {
            return invalid;
        }

        return new InvalidOperationException("Не удалось открыть обычный браузер для входа.", ex);
    }

    private static async Task CloseOwnedBrowserAsync(IBrowser? browser)
    {
        if (browser is null)
        {
            return;
        }

        try
        {
            if (browser.IsConnected)
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Login window must always dispose.
        }

        try
        {
            browser.Dispose();
        }
        catch
        {
            // Ignore.
        }
    }
}
