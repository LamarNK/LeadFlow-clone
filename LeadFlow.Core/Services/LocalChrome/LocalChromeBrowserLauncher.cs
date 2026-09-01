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
        var args = ResolveLaunchArgs(options);

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

            throw SanitizeLaunchError(ex, options, executable, userDataDir);
        }
    }

    /// <summary>
    /// PuppeteerSharp 24 не принимает <c>LaunchOptions.Args = null</c> (LINQ <c>source</c>).
    /// Без прокси — пустой массив, не <c>null</c>. Прокси-аргумент без изменений.
    /// </summary>
    public static string[] ResolveLaunchArgs(LocalChromeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ChromiumArgs ?? [];
    }

    internal static Exception SanitizeLaunchError(
        Exception ex,
        LocalChromeLaunchOptions options,
        string? executable = null,
        string? userDataDir = null)
    {
        return LocalChromeLaunchDiagnostics.Wrap(
            ex,
            executable ?? options.ExecutablePath,
            userDataDir ?? options.UserDataDir,
            options.ProxyEnabled,
            options.ProxyUsername,
            options.ProxyPassword);
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
