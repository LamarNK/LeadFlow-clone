using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

public sealed class LocalChromeBrowserLauncher : ILocalChromeBrowserLauncher
{
    public async Task<IBrowser> LaunchAsync(
        LocalChromeLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var userDataDir = LocalChromePaths.NormalizeUserDataDir(options.UserDataDir, options.AccountId);
        LocalChromePaths.EnsureUserDataDir(userDataDir);
        var executable = LocalChromePaths.ResolveExecutable(options.ExecutablePath);
        var args = options.ChromiumArgs;

        var launchTask = Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            ExecutablePath = executable,
            UserDataDir = userDataDir,
            Args = args
        });

        IBrowser? browser = null;
        try
        {
            browser = await launchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await LocalChromeProxyAuth
                .ApplyAsync(browser, options, cancellationToken)
                .ConfigureAwait(false);
            return browser;
        }
        catch (OperationCanceledException)
        {
            if (browser is not null)
            {
                await CloseAbandonedBrowserAsync(browser).ConfigureAwait(false);
            }
            else
            {
                ObserveAbandonedLaunch(launchTask);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (browser is not null)
            {
                await CloseAbandonedBrowserAsync(browser).ConfigureAwait(false);
            }
            else
            {
                ObserveAbandonedLaunch(launchTask);
            }

            throw SanitizeLaunchError(ex, options);
        }
    }

    internal static Exception SanitizeLaunchError(Exception ex, LocalChromeLaunchOptions options)
    {
        var sanitized = LocalChromeProxyRules.SanitizeError(
            ex.Message,
            options.ProxyUsername,
            options.ProxyPassword);
        if (ex is InvalidOperationException
            && (sanitized.StartsWith("Обычный браузер", StringComparison.Ordinal)
                || sanitized.StartsWith("Не найден установленный Chrome", StringComparison.Ordinal)
                || sanitized.StartsWith("Файл браузера не найден", StringComparison.Ordinal)
                || sanitized.StartsWith("Нельзя использовать стандартный профиль", StringComparison.Ordinal)
                || sanitized.StartsWith("Укажите путь", StringComparison.Ordinal)
                || sanitized.StartsWith("Путь к", StringComparison.Ordinal)
                || sanitized.StartsWith("Не удалось авторизовать прокси", StringComparison.Ordinal)
                || sanitized.StartsWith("Не удалось подготовить прокси", StringComparison.Ordinal)))
        {
            return new InvalidOperationException(sanitized);
        }

        return new InvalidOperationException(
            options.ProxyEnabled
                ? LocalChromeProxyRules.SanitizeError(
                    "Не удалось открыть обычный браузер с прокси.",
                    options.ProxyUsername,
                    options.ProxyPassword)
                : "Не удалось открыть обычный браузер.",
            ex);
    }

    private static void ObserveAbandonedLaunch(Task<IBrowser> launchTask)
    {
        _ = launchTask.ContinueWith(
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

                _ = CloseAbandonedBrowserAsync(task.Result);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static async Task CloseAbandonedBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Abandoned launch must not throw on a background thread.
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
