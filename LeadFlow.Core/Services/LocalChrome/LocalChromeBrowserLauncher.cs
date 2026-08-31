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

        var launchTask = Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            ExecutablePath = executable,
            UserDataDir = userDataDir
        });

        try
        {
            return await launchTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ObserveAbandonedLaunch(launchTask);
            throw;
        }
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
