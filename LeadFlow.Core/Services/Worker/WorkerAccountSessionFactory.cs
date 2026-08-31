using LeadFlow.Core.Models;
using LeadFlow.Core.Services.AdsPower;
using LeadFlow.Core.Services.LocalChrome;
using LeadFlow.Core.Services.Multilogin;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.Worker;

public sealed class WorkerOpenedAccountSession : IAsyncDisposable
{
    private readonly Func<ValueTask> _closeProviderAsync;

    internal WorkerOpenedAccountSession(
        IAdsPowerAccountSession session,
        WorkerAccountRuntimeKind runtime,
        Func<ValueTask> closeProviderAsync)
    {
        Session = session;
        Runtime = runtime;
        _closeProviderAsync = closeProviderAsync;
    }

    public IAdsPowerAccountSession Session { get; }

    public WorkerAccountRuntimeKind Runtime { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disconnect must never hide provider stop.
        }

        await _closeProviderAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Открывает AdsPower, Multilogin или обычный Chrome и гарантированно закрывает провайдерский браузер.
/// </summary>
public sealed class WorkerAccountSessionFactory(
    IAdsPowerAvitoAutomationService adsPowerAvitoAutomationService,
    IMultiloginCdpConnector? multiloginCdpConnector = null,
    ILocalChromeBrowserLauncher? localChromeLauncher = null)
{
    public async Task<WorkerOpenedAccountSession> OpenAsync(
        AvitoAccount account,
        AdsPowerConnectionOptions adsOptions,
        Action<string, TimeSpan>? reportStartupStage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        var kind = WorkerAccountRuntime.Resolve(account);
        if (kind == WorkerAccountRuntimeKind.Multilogin)
        {
            return await OpenMultiloginAsync(account, reportStartupStage, cancellationToken)
                .ConfigureAwait(false);
        }

        if (kind == WorkerAccountRuntimeKind.Local)
        {
            return await OpenLocalChromeAsync(account, reportStartupStage, cancellationToken)
                .ConfigureAwait(false);
        }

        if (kind != WorkerAccountRuntimeKind.AdsPower)
        {
            throw new InvalidOperationException("Worker runtime: нет browser-профиля AdsPower, Multilogin или обычного браузера.");
        }

        var session = await adsPowerAvitoAutomationService
            .OpenAccountSessionAsync(
                adsOptions,
                account.AdsPowerProfileId!,
                reportStartupStage,
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkerOpenedAccountSession(
            session,
            WorkerAccountRuntimeKind.AdsPower,
            async () =>
            {
                try
                {
                    await adsPowerAvitoAutomationService
                        .CloseBrowserAsync(adsOptions, account.AdsPowerProfileId!, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Same swallow as TryCloseAdsPowerBrowserForAccountAsync.
                }
            });
    }

    private async Task<WorkerOpenedAccountSession> OpenLocalChromeAsync(
        AvitoAccount account,
        Action<string, TimeSpan>? reportStartupStage,
        CancellationToken cancellationToken)
    {
        if (!WorkerAccountRuntime.IsLocal(account))
        {
            throw new InvalidOperationException(
                "Обычный браузер: не задан путь к отдельной папке профиля (User Data).");
        }

        if (localChromeLauncher is null)
        {
            throw new InvalidOperationException("Обычный браузер: launcher не зарегистрирован.");
        }

        IBrowser? browser = null;
        try
        {
            reportStartupStage?.Invoke("запуск Chrome", TimeSpan.Zero);
            browser = await localChromeLauncher
                .LaunchAsync(LocalChromeLaunchOptionsFactory.FromAccount(account), cancellationToken)
                .ConfigureAwait(false);

            var session = await adsPowerAvitoAutomationService
                .OpenAccountSessionOnConnectedBrowserAsync(
                    browser,
                    account.Id.ToString("D"),
                    reportStartupStage,
                    cancellationToken,
                    runtimeProvider: "Local")
                .ConfigureAwait(false);

            var owned = browser;
            browser = null;
            return new WorkerOpenedAccountSession(
                session,
                WorkerAccountRuntimeKind.Local,
                () => CloseOwnedBrowserAsync(owned));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (browser is not null)
            {
                await CloseOwnedBrowserAsync(browser).ConfigureAwait(false);
            }

            if (ex.Message.StartsWith("Обычный браузер", StringComparison.Ordinal)
                || ex.Message.StartsWith("Не найден установленный Chrome", StringComparison.Ordinal)
                || ex.Message.StartsWith("Файл браузера не найден", StringComparison.Ordinal)
                || ex.Message.StartsWith("Нельзя использовать стандартный профиль", StringComparison.Ordinal)
                || ex.Message.StartsWith("Укажите путь", StringComparison.Ordinal)
                || ex.Message.StartsWith("Путь к", StringComparison.Ordinal))
            {
                throw;
            }

            throw new InvalidOperationException($"Обычный браузер: {ex.Message}", ex);
        }
        catch
        {
            if (browser is not null)
            {
                await CloseOwnedBrowserAsync(browser).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<WorkerOpenedAccountSession> OpenMultiloginAsync(
        AvitoAccount account,
        Action<string, TimeSpan>? reportStartupStage,
        CancellationToken cancellationToken)
    {
        if (!WorkerAccountRuntime.IsMultilogin(account))
        {
            throw new InvalidOperationException(
                "Multilogin CDP: не заданы launcher URL, token, folder ID или profile ID.");
        }

        if (multiloginCdpConnector is null)
        {
            throw new InvalidOperationException("Multilogin CDP: connector не зарегистрирован.");
        }

        var options = new MultiloginConnectionOptions
        {
            LauncherUrl = account.MultiloginLauncherUrl,
            AutomationToken = account.MultiloginAutomationToken
        };

        IMultiloginCdpSession? mlx = null;
        try
        {
            mlx = await multiloginCdpConnector
                .OpenAsync(
                    options,
                    account.MultiloginFolderId!,
                    account.MultiloginProfileId!,
                    cancellationToken)
                .ConfigureAwait(false);

            var session = await adsPowerAvitoAutomationService
                .OpenAccountSessionOnConnectedBrowserAsync(
                    mlx.Connected.Browser,
                    account.MultiloginProfileId!,
                    reportStartupStage,
                    cancellationToken)
                .ConfigureAwait(false);

            var owned = mlx;
            mlx = null;
            return new WorkerOpenedAccountSession(
                session,
                WorkerAccountRuntimeKind.Multilogin,
                async () =>
                {
                    await owned.DisposeAsync().ConfigureAwait(false);
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (mlx is not null)
            {
                await mlx.DisposeAsync().ConfigureAwait(false);
            }

            if (ex.Message.StartsWith("Multilogin", StringComparison.Ordinal))
            {
                throw;
            }

            throw new InvalidOperationException($"Multilogin CDP: {ex.Message}", ex);
        }
        catch
        {
            if (mlx is not null)
            {
                await mlx.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async ValueTask CloseOwnedBrowserAsync(IBrowser browser)
    {
        try
        {
            if (browser.IsConnected)
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Close must never hide the original error.
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
