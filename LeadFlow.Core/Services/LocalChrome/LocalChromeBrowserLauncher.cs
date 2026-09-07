using LeadFlow.Core.Logging.Audit;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

public sealed class LocalChromeBrowserLauncher : ILocalChromeBrowserLauncher
{
    public static readonly string[] AutomationArgs =
    [
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-session-crashed-bubble",
        "--hide-crash-restore-bubble"
    ];

    private readonly LocalChromeProfileReclaimer _reclaimer;
    private readonly Func<LaunchOptions, Task<IBrowser>> _launch;
    private static int _chromeVersionLogged;

    public LocalChromeBrowserLauncher()
        : this(new LocalChromeProfileReclaimer(), static options => Puppeteer.LaunchAsync(options))
    {
    }

    public LocalChromeBrowserLauncher(LocalChromeProfileReclaimer reclaimer)
        : this(reclaimer, static options => Puppeteer.LaunchAsync(options))
    {
    }

    internal LocalChromeBrowserLauncher(
        LocalChromeProfileReclaimer reclaimer,
        Func<LaunchOptions, Task<IBrowser>> launch)
    {
        _reclaimer = reclaimer;
        _launch = launch;
    }

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

        await ReclaimAndLogAsync(userDataDir, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                await ReclaimAndLogAsync(userDataDir, cancellationToken).ConfigureAwait(false);
            }

            var launchOptions = new LaunchOptions
            {
                Headless = false,
                DefaultViewport = null,
                ExecutablePath = executable,
                UserDataDir = userDataDir,
                Args = args
            };
            var launchTask = _launch(launchOptions);
            IBrowser? browser = null;
            try
            {
                browser = await launchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                await LocalChromeProxyAuth
                    .ApplyAsync(browser, options, cancellationToken)
                    .ConfigureAwait(false);
                await LogChromeVersionOnceAsync(browser).ConfigureAwait(false);
                return browser;
            }
            catch (OperationCanceledException)
            {
                await AbandonAsync(browser, launchTask, userDataDir).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await AbandonAsync(browser, launchTask, userDataDir).ConfigureAwait(false);
                lastError = ex;
                if (attempt == 0 && LocalChromeLaunchDiagnostics.IsProfileBusy(ex))
                {
                    continue;
                }

                throw SanitizeLaunchError(ex, options, executable, userDataDir);
            }
        }

        throw SanitizeLaunchError(
            lastError ?? new InvalidOperationException("Failed to launch browser!"),
            options,
            executable,
            userDataDir);
    }

    public async Task StopAsync(
        IBrowser? browser,
        string userDataDir,
        CancellationToken cancellationToken = default)
    {
        if (browser is not null)
        {
            await CloseOwnedBrowserAsync(browser).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(userDataDir))
        {
            return;
        }

        string dir;
        try
        {
            dir = LocalChromePaths.NormalizeUserDataDir(userDataDir);
        }
        catch
        {
            return;
        }

        await ReclaimAndLogAsync(dir, cancellationToken).ConfigureAwait(false);
    }

    public Task<LocalChromeProfileReclaimResult> ReclaimAsync(
        string userDataDir,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userDataDir))
        {
            return Task.FromResult(LocalChromeProfileReclaimResult.Empty);
        }

        string dir;
        try
        {
            dir = LocalChromePaths.NormalizeUserDataDir(userDataDir);
        }
        catch
        {
            return Task.FromResult(LocalChromeProfileReclaimResult.Empty);
        }

        return ReclaimAndLogAsync(dir, cancellationToken);
    }

    /// <summary>
    /// Стабильные флаги автоматизации плюс прокси. PuppeteerSharp 24 не принимает <c>Args = null</c>.
    /// </summary>
    public static string[] ResolveLaunchArgs(LocalChromeLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var proxy = options.ChromiumArgs ?? [];
        if (proxy.Length == 0)
        {
            return AutomationArgs;
        }

        var args = new string[AutomationArgs.Length + proxy.Length];
        AutomationArgs.CopyTo(args, 0);
        proxy.CopyTo(args, AutomationArgs.Length);
        return args;
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

    private static async Task LogChromeVersionOnceAsync(IBrowser browser)
    {
        if (Interlocked.Exchange(ref _chromeVersionLogged, 1) != 0)
        {
            return;
        }

        try
        {
            var version = await browser.GetVersionAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(version))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Обычный браузер: {version.Trim()}",
                    DeskLinkAuditLogLevel.Info);
            }
        }
        catch
        {
            // Version is diagnostics only.
        }
    }

    private async Task<LocalChromeProfileReclaimResult> ReclaimAndLogAsync(
        string userDataDir,
        CancellationToken cancellationToken)
    {
        var result = await _reclaimer.ReclaimAsync(userDataDir, cancellationToken).ConfigureAwait(false);
        if (result.KilledProcessCount > 0)
        {
            var pids = string.Join(", ", result.KilledProcessIds);
            _ = GlobalLogger.Instance.LogAsync(
                $"Обычный браузер: сняли зависший Chrome (pid {pids}) для профиля {userDataDir}.",
                DeskLinkAuditLogLevel.Warning);
        }

        if (result.StillOccupied)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Обычный браузер: профиль всё ещё занят после reclaim ({userDataDir}).",
                DeskLinkAuditLogLevel.Warning);
        }

        return result;
    }

    private Task AbandonAsync(IBrowser? browser, Task<IBrowser> launchTask, string userDataDir)
    {
        if (browser is not null)
        {
            return StopAsync(browser, userDataDir, CancellationToken.None);
        }

        ObserveAbandonedLaunch(launchTask, userDataDir);
        return Task.CompletedTask;
    }

    private void ObserveAbandonedLaunch(Task<IBrowser> launchTask, string userDataDir)
    {
        _ = launchTask.ContinueWith(
            async task =>
            {
                if (task.IsFaulted)
                {
                    _ = task.Exception;
                    await _reclaimer.ReclaimAsync(userDataDir, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (!task.IsCompletedSuccessfully)
                {
                    return;
                }

                await StopAsync(task.Result, userDataDir, CancellationToken.None).ConfigureAwait(false);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static async Task CloseOwnedBrowserAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Close must never hide the original error.
        }

        try
        {
            var process = browser.Process;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may already be gone.
        }

        try
        {
            browser.Dispose();
        }
        catch
        {
            // Dispose must never throw out of cleanup.
        }
    }
}
