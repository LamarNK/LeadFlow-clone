using System.Runtime.CompilerServices;
using LeadFlow.Core.Logging.Audit;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.LocalChrome;

/// <summary>
/// Авторизация HTTP-прокси на всех вкладках сессии до навигации на Avito.
/// Логин и пароль не попадают в launch args — только <c>page.AuthenticateAsync</c>.
/// </summary>
public static class LocalChromeProxyAuth
{
    private static readonly ConditionalWeakTable<IBrowser, Credentials> CredentialsByBrowser = new();

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
        var credentials = new Credentials
        {
            Username = options.ProxyUsername ?? string.Empty,
            Password = options.ProxyPassword ?? string.Empty
        };
        if (!CredentialsByBrowser.TryGetValue(browser, out _))
        {
            CredentialsByBrowser.Add(browser, credentials);
            browser.TargetCreated += OnTargetCreated;
        }

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

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AuthenticatePageAsync(
                    page,
                    credentials,
                    throwOnError: true,
                    options.ProxyUsername,
                    options.ProxyPassword,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static bool IsPageTarget(TargetType type) => type == TargetType.Page;

    public static async Task HandleNewTargetAsync(
        IBrowser browser,
        ITarget target,
        CancellationToken cancellationToken = default)
    {
        if (!CredentialsByBrowser.TryGetValue(browser, out var credentials))
        {
            return;
        }

        if (!IsPageTarget(target.Type))
        {
            return;
        }

        IPage? page;
        try
        {
            page = await target.PageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (page is null)
        {
            return;
        }

        await AuthenticatePageAsync(
                page,
                credentials,
                throwOnError: false,
                secrets: null,
                extraSecret: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async void OnTargetCreated(object? sender, TargetChangedArgs args)
    {
        if (sender is not IBrowser browser || args.Target is null)
        {
            return;
        }

        try
        {
            await HandleNewTargetAsync(browser, args.Target).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                LocalChromeProxyRules.SanitizeError(
                    $"Обычный браузер: не удалось авторизовать прокси на новой вкладке: {ex.Message}"),
                DeskLinkAuditLogLevel.Warning);
        }
    }

    private static async Task AuthenticatePageAsync(
        IPage page,
        Credentials credentials,
        bool throwOnError,
        string? secrets,
        string? extraSecret,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.AuthenticateAsync(credentials).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (throwOnError)
            {
                throw new InvalidOperationException(
                    LocalChromeProxyRules.SanitizeError(
                        "Не удалось авторизовать прокси обычного браузера.",
                        secrets,
                        extraSecret),
                    ex);
            }

            _ = GlobalLogger.Instance.LogAsync(
                LocalChromeProxyRules.SanitizeError(
                    $"Обычный браузер: не удалось авторизовать прокси на новой вкладке: {ex.Message}",
                    secrets,
                    extraSecret),
                DeskLinkAuditLogLevel.Warning);
        }
    }
}
