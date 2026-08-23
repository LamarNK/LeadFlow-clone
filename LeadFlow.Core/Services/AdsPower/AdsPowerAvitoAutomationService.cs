using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadFlow.Core.Data;
using LeadFlow.Core.Models;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
using LeadFlow.Core.Services.Captcha;
using LeadFlow.Core.Services.Worker;
using Orbita.Contracts;
using PuppeteerSharp;

namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// CDP-операции в AdsPower-браузере для Avito. Между шагами (суб-профили, страницы) браузер
/// не закрываем — один <c>user_id</c> = одна сессия. Закрытие — через
/// <see cref="CloseBrowserAsync"/> после полного прохода аккаунта в мониторинге.
/// </summary>
public sealed partial class AdsPowerAvitoAutomationService(
    IAdsPowerApiClient adsPowerApiClient,
    ICandidateDuplicateRepository duplicateRepository,
    IPhoneNormalizer phoneNormalizer,
    IAvitoGeeTestSolver? geeTestSolver = null) : IAdsPowerAvitoAutomationService
{
    private static readonly TimeSpan CdpConnectTimeout = TimeSpan.FromSeconds(15);
    // Видимая вкладка может оставаться исправной, когда отдельная CDP-команда уже не отвечает.
    // Эти операции нужны только для выбора/проверки вкладки, поэтому не должны удерживать слот
    // мониторинга минутами.
    private static readonly TimeSpan CdpPageDiscoveryTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CdpPageReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CdpSwitchActionTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CdpSwitchEffectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CdpNavigationGuardTimeout = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan CdpEvaluateHangTimeout = TimeSpan.FromSeconds(30);

    private const string CandidatesPageUrl = AvitoCandidatesPageUrls.LegacyCandidates;
    private const string JobResponsesPageUrl = AvitoCandidatesPageUrls.JobResponsesCrm;
    private const string ProfileItemsPageUrl = "https://www.avito.ru/profile/pro/items";
    private const string ProfileBlockedItemsPageUrl = "https://www.avito.ru/profile/pro/items?filters=%7B%22tabs%22%3A%22rejected%22%7D";
    private const string ProfileDashboardPageUrl = "https://www.avito.ru/profile/dashboard";
    /// <summary>Модалка «Выбор профиля» через дашборд — надёжнее, чем с <c>/profile/pro/items</c>.</summary>
    private const string ProfileSwitchPageUrl = "https://www.avito.ru/profile/dashboard#profile/switch?withEntities=true";
    /// <summary>Мини-чат Avito не открывается в углу при узком viewport — расширяем перед сбором переписки.</summary>
    private const int MessengerEnrichmentViewportWidth = 1440;
    private const int MessengerEnrichmentViewportHeight = 900;
    private const int MessengerEnrichmentViewportResizeDelayMs = 800;

    public async Task<string> ExtractCandidatesJsonAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null)
    {
        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var connectOptions = new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        };

        IBrowser? browser = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            var page = await AcquireAutomationPageAsync(
                    browser,
                    CandidatesPageUrl,
                    nameof(ExtractCandidatesJsonAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);

            var executeScript = (string script, CancellationToken ct) =>
                EvaluateWithRetryAsync<string>(page, script, ct);

            if (IsOnActiveProfileItemsPage(page.Url)
                && AvitoHumanVariation.RollPermille(MonitoringTiming.ItemsLingerChancePermille))
            {
                await HumanDelay.AfterItemsLingerAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureOnCandidatesPageAsync(page, adsPowerUserId, cancellationToken).ConfigureAwait(false);

            await AvitoCandidatesListPreparer.PrepareAsync(
                executeScript,
                $"AdsPower:{adsPowerUserId}",
                cancellationToken,
                async ct =>
                {
                    try
                    {
                        return await page.GetContentAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        return null;
                    }
                },
                page.Url,
                BuildResolveExistingSourceResponseIdsCallback(messengerEnrichmentHints),
                BuildResolveExistingCardFingerprintsCallback(messengerEnrichmentHints),
                BuildResolveExistingPhonesCallback(messengerEnrichmentHints),
                BuildResolveExistingMatchedProfileIndicesCallback(messengerEnrichmentHints),
                messengerEnrichmentHints?.ResponseFilters,
                messengerEnrichmentHints?.IsOpenPhoneWatchAsync,
                skipDetailEnrich: true,
                CreateCaptchaSolveCallback(page)).ConfigureAwait(false);

            var raw = await EvaluateWithRetryAsync<string>(page, ExtractionScript, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("AdsPower CDP: скрипт извлечения вернул пустой результат.");
            }

            raw = await TryEnrichCandidatesJsonMessengerUrlsAsync(page, raw, messengerEnrichmentHints, cancellationToken)
                .ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower CDP candidates extraction completed.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(ExtractCandidatesJsonAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["page.url"] = page.Url
                });

            return raw;
        }
        finally
        {
            await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowser: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<string> LoadProfileItemsHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-items load started for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(LoadProfileItemsHtmlAsync),
            
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileItemsPageUrl
            });

        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var connectOptions = new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        };

        IBrowser? browser = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower profile-items: connected via CDP.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadProfileItemsHtmlAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "cdp_connected",
                    ["adsPower.userId"] = adsPowerUserId
                });

            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileItemsPageUrl,
                    nameof(LoadProfileItemsHtmlAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);

            if (!IsOnActiveProfileItemsPage(page.Url))
            {
                try
                {
                    await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                    {
                        Timeout = 60_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRecoverableNavigationError(ex))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower profile-items navigation transient error, retrying after delay: {ex.Message}",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(LoadProfileItemsHtmlAsync));
                    await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                }
            }

            // Шаг 1: ждём, что отрисовалась хотя бы оболочка списка — тулбар сортировки или сами карточки.
            try
            {
                await page.WaitForSelectorAsync(
                    "[data-marker='sorting-control'], [data-marker^='item-snippet/']",
                    new WaitForSelectorOptions { Timeout = 30_000 }).ConfigureAwait(false);

                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower profile-items: list shell selector ready.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(LoadProfileItemsHtmlAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "shell_ready",
                        ["page.url"] = page.Url
                    });
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-items: shell selector wait timed out: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadProfileItemsHtmlAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "shell_timeout",
                        ["page.url"] = page.Url
                    });
            }

            // Шаг 2: ждём, что спиннер #personal-items-root-element .styles-loader-* исчез и появились карточки объявлений
            // (либо явно отрисовалось пустое состояние «нет объявлений»). Это ключевой момент: без этого мы успеваем
            // снять HTML на этапе spinner-only и парсер возвращает 0 объявлений.
            // Эвристики пустого состояния: ссылка [data-marker='additem'] внутри лоадера, эмпти-стейт картинка
            // emptystate_personal_items_*.png, либо текст с обоими словами «активн…» и «нет» в любом порядке
            // (Avito показывает «Активных объявлений нет», старая регулярка «нет объявлений» не ловила).
            const string itemsReadyExpression = """
                (() => {
                    const root = document.querySelector('#personal-items-root-element') || document.body;
                    const hasLoader = !!root.querySelector("[class*='styles-loader'], [class*='style-loader']");
                    if (hasLoader) return false;
                    const hasItems = !!document.querySelector("[data-marker^='item-snippet/']");
                    if (hasItems) return true;
                    const hasAddItemEmpty = !!root.querySelector("[data-marker='additem']");
                    const hasEmptyStateImg = !!root.querySelector("img[src*='emptystate_personal_items']");
                    if (hasAddItemEmpty || hasEmptyStateImg) return true;
                    const text = (root.innerText || '').toLowerCase();
                    const looksEmpty =
                        /активн[а-я]*\s+объявлен[а-я]*\s+нет/.test(text) ||
                        /нет\s+(активных\s+)?объявлен/.test(text) ||
                        /у\s+вас\s+нет\s+активных/.test(text) ||
                        /объявлен[а-я]*\s+не\s+найден/.test(text) ||
                        /пока\s+пусто/.test(text) ||
                        /можно\s+создать\s+новое/.test(text);
                    return looksEmpty;
                })
                """;

            try
            {
                await page.WaitForFunctionAsync(
                        itemsReadyExpression,
                        new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 750 })
                    .ConfigureAwait(false);

                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower profile-items: loader gone and items rendered.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(LoadProfileItemsHtmlAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "items_ready",
                        ["page.url"] = page.Url
                    });
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-items: items wait timed out, capturing whatever is on the page: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadProfileItemsHtmlAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "items_timeout",
                        ["page.url"] = page.Url
                    });
            }

            // Шаг 3: настройщик SPA подтягивает счётчики просмотров/контактов и позицию в поиске уже после первого рендера.
            // Используем «человеческую» рандомную задержку (см. MonitoringTiming.HumanDelayAfterItemsRender*), чтобы не палить ботскую частоту запросов.
            await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

            var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(html))
            {
                throw new InvalidOperationException("AdsPower CDP: страница объявлений Avito вернула пустой HTML.");
            }

            await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-items: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadProfileItemsHtmlAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captured",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["html.length"] = html.Length
                });

            return html;
        }
        finally
        {
            await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowser: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<string> LoadBlockedItemsHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower blocked-items load started for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(LoadBlockedItemsHtmlAsync),
            
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileBlockedItemsPageUrl
            });

        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var connectOptions = new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        };

        IBrowser? browser = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileBlockedItemsPageUrl,
                    nameof(LoadBlockedItemsHtmlAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);

            // Гарантируем, что мы на rejected-вкладке: даже если хеш/фильтр сбросились — переходим явно.
            if (!IsOnRejectedTab(page.Url))
            {
                try
                {
                    await page.GoToAsync(ProfileBlockedItemsPageUrl, new NavigationOptions
                    {
                        Timeout = 60_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRecoverableNavigationError(ex))
                {
                    await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                await page.WaitForSelectorAsync(
                    "[data-marker='profile-items-tab/tab(rejected)'], [data-marker^='item-snippet/']",
                    new WaitForSelectorOptions { Timeout = 30_000 }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower blocked-items: shell wait timed out: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadBlockedItemsHtmlAsync));
            }

            // Ждём: лоадер исчез + либо есть карточки, либо явный эмпти-стейт «нет … объявлений / можно создать».
            const string blockedReadyExpression = """
                (() => {
                    const root = document.querySelector('#personal-items-root-element') || document.body;
                    const hasLoader = !!root.querySelector("[class*='styles-loader'], [class*='style-loader']");
                    if (hasLoader) return false;
                    const hasItems = !!document.querySelector("[data-marker^='item-snippet/']");
                    if (hasItems) return true;
                    const hasAddItemEmpty = !!root.querySelector("[data-marker='additem']");
                    const hasEmptyStateImg = !!root.querySelector("img[src*='emptystate_personal_items']");
                    if (hasAddItemEmpty || hasEmptyStateImg) return true;
                    const text = (root.innerText || '').toLowerCase();
                    const looksEmpty =
                        /объявлен[а-я]*\s+с\s+ошибк/.test(text) ||
                        /нет\s+объявлен/.test(text) ||
                        /объявлен[а-я]*\s+нет/.test(text) ||
                        /пока\s+пусто/.test(text);
                    return looksEmpty;
                })
                """;

            try
            {
                await page.WaitForFunctionAsync(
                        blockedReadyExpression,
                        new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 750 })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower blocked-items: items wait timed out, capturing whatever is on the page: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadBlockedItemsHtmlAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "items_timeout",
                        ["page.url"] = page.Url
                    });
            }

            await HumanDelay.AfterItemsRenderAsync(cancellationToken).ConfigureAwait(false);

            var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(html))
            {
                throw new InvalidOperationException("AdsPower CDP: вкладка «С ошибками» вернула пустой HTML.");
            }

            await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower blocked-items: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadBlockedItemsHtmlAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captured",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["html.length"] = html.Length
                });

            return html;
        }
        finally
        {
            await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowser: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsOnRejectedTab(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("/profile/pro/items", StringComparison.OrdinalIgnoreCase)
            && url.Contains("rejected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Если в HTML обнаружена капча/firewall — пробуем GeeTest v4 через RuCaptcha,
    /// иначе бросаем <see cref="AvitoCaptchaDetectedException"/>.
    /// </summary>
    private async Task ThrowIfCaptchaAsync(IPage page, string html, CancellationToken cancellationToken)
    {
        var kind = AvitoCaptchaDetector.Classify(html);
        if (kind is null)
        {
            return;
        }

        var isIpBlock = AvitoCaptchaDetector.HasIpBlockChallenge(html);
        if (!isIpBlock
            && await TrySolveGeeTestAsync(page, html, page.Url, kind, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var screenshot = await BrowserDiagnosticsCapture
            .CapturePageScreenshotAsync(page, cancellationToken)
            .ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower {(isIpBlock ? "IP block" : "captcha")} detected ({kind}) on {page.Url ?? "<unknown>"}.",
            DeskLinkAuditLogLevel.Warning,
            properties: new Dictionary<string, object?>
            {
                ["step"] = isIpBlock ? "ip_block_detected" : "captcha_detected",
                ["page.url"] = page.Url,
                ["issue.kind"] = isIpBlock ? "ip_block" : kind,
                ["screenshot.bytes"] = screenshot?.Length ?? 0
            });

        if (!isIpBlock)
        {
            AvitoCaptchaTaskContext.NoteUnsolved();
        }
        throw new AvitoCaptchaDetectedException(kind, page.Url, html, screenshot);
    }

    private Func<AvitoFirewallProbe.Detection, string?, CancellationToken, Task<bool>>? CreateCaptchaSolveCallback(
        IPage page)
    {
        if (geeTestSolver is null)
        {
            return null;
        }

        return (detection, html, ct) => TrySolveGeeTestAsync(page, html, detection.Url ?? page.Url, detection.Kind, ct);
    }

    private async Task<GeeTestV4TaskOptions> ResolveCaptchaTaskOptionsAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        var current = AvitoCaptchaTaskContext.Options ?? new GeeTestV4TaskOptions();
        try
        {
            var profileProxy = await adsPowerApiClient
                .GetProfileProxyAsync(options, adsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
            if (profileProxy is null)
            {
                return current;
            }

            var profileParsed = GeeTestV4Proxy.TryCreate(
                profileProxy.Type,
                profileProxy.Address,
                profileProxy.Username,
                profileProxy.Password);
            var merged = profileParsed is null ? current : current.WithProxy(profileParsed);
            _ = GlobalLogger.Instance.LogAsync(
                profileParsed is not null
                    ? "Captcha: RuCaptcha получит прокси профиля AdsPower."
                    : "Captcha: прокси профиля AdsPower не разобран, оставляем текущий режим RuCaptcha.",
                profileParsed is not null ? DeskLinkAuditLogLevel.Info : DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = profileParsed is not null
                        ? "captcha_profile_proxy_applied"
                        : "captcha_profile_proxy_unparsed",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["captcha.proxyType"] = profileProxy.Type,
                    ["captcha.proxyMode"] = merged.UsesSuppliedProxy ? "profile" : "proxyless"
                });
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: не удалось прочитать прокси профиля AdsPower — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_profile_proxy_read_failed",
                    ["adsPower.userId"] = adsPowerUserId
                });
            return current;
        }
    }

    private async Task<IDisposable> UseProfileCaptchaContextAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        var captchaOptions = await ResolveCaptchaTaskOptionsAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        return AvitoCaptchaTaskContext.Use(captchaOptions);
    }

    private async Task ThrowIfCaptchaOnPageAsync(IPage page, CancellationToken cancellationToken)
    {
        string? html = null;
        try
        {
            html = await AdsPowerCdpGuard.WaitAsync(
                    page.GetContentAsync(),
                    CdpEvaluateHangTimeout,
                    "чтение HTML для капчи",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(html)
            || (!AvitoCaptchaDetector.IsCaptchaHtml(html)
                && !AvitoCaptchaDetector.CanAttemptGeeTestSolve(html)))
        {
            return;
        }

        await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryClearGeeTestCaptchaAsync(IPage page, CancellationToken cancellationToken)
    {
        string? html = null;
        try
        {
            html = await page.GetContentAsync().ConfigureAwait(false);
        }
        catch
        {
            // солвер снимет HTML сам
        }

        return await TrySolveGeeTestAsync(
                page,
                html,
                page.Url,
                AvitoCaptchaDetector.Classify(html),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> TrySolveGeeTestAsync(
        IPage page,
        string? html,
        string? pageUrl,
        string? kind,
        CancellationToken cancellationToken)
    {
        if (html is not null && AvitoCaptchaDetector.HasIpBlockChallenge(html))
        {
            return false;
        }

        if (geeTestSolver is null)
        {
            return false;
        }

        if (html is not null
            && !AvitoCaptchaRedirectRecovery.RequiresRecovery(html)
            && !AvitoCaptchaDetector.CanAttemptGeeTestSolve(html)
            && !AvitoCaptchaDetector.HasSolvableCaptchaChallenge(html)
            && !string.Equals(kind, "geetest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return await geeTestSolver
                .TrySolveOnPageAsync(page, html, pageUrl, AvitoCaptchaTaskContext.Options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"Captcha: автопроход GeeTest v4 не удался — {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captcha_solve_failed",
                    ["page.url"] = pageUrl ?? page.Url,
                    ["captcha.kind"] = kind
                });
            return false;
        }
    }

    public async Task<string> LoadProfileSwitchHtmlAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch load started for user {adsPowerUserId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(LoadProfileSwitchHtmlAsync),
            
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileSwitchPageUrl
            });

        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var connectOptions = new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        };

        IBrowser? browser = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileSwitchPageUrl,
                    nameof(LoadProfileSwitchHtmlAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);
            return await CaptureProfileSwitchHtmlInSessionAsync(page, adsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowser: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<string> CaptureProfileSwitchHtmlInSessionAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsOnCandidatesResponsesPage(page.Url))
            {
                await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
                    .ConfigureAwait(false);
            }

            var preSwitchState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (AvitoAutomationFailureFormatter.SuggestsLogin(preSwitchState))
            {
                if (!await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false))
                {
                    throw new AvitoLoginRequiredException(preSwitchState?.Url, preSwitchState?.Title);
                }

                if (attempt < maxAttempts)
                {
                    continue;
                }
            }

            await EnsureSwitchModalAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
                .ConfigureAwait(false);
            if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
                    .ConfigureAwait(false))
            {
                var modalFailState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (attempt < maxAttempts)
                {
                    if (AvitoAutomationFailureFormatter.SuggestsLogin(modalFailState))
                    {
                        _ = await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await AvitoAutoLoginRecovery.TryRefreshSessionAsync(page, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    _ = GlobalLogger.Instance.LogAsync(
                        "AdsPower profile-switch: modal not ready — retry after session refresh.",
                        DeskLinkAuditLogLevel.Info,
                        memberName: nameof(CaptureProfileSwitchHtmlInSessionAsync),
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "switch_modal_refresh_retry",
                            ["attempt"] = attempt,
                            ["page.url"] = page.Url,
                            ["pageState"] = modalFailState?.DescribeForDiagnostics()
                        });
                    continue;
                }

                if (AvitoAutomationFailureFormatter.SuggestsLogin(modalFailState))
                {
                    throw new AvitoLoginRequiredException(modalFailState?.Url, modalFailState?.Title);
                }

                throw new InvalidOperationException("AdsPower CDP: модалка переключения профилей не загрузилась.");
            }

            var html = await EvaluateWithRetryAsync<string>(
                    page,
                    "(() => document.documentElement?.outerHTML || '')()",
                    cancellationToken,
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(html))
            {
                throw new InvalidOperationException("AdsPower CDP: страница переключения профилей вернула пустой HTML.");
            }

            await ThrowIfCaptchaAsync(page, html, cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(CaptureProfileSwitchHtmlInSessionAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "captured",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["html.length"] = html.Length,
                    ["attempt"] = attempt
                });

            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            var postDismiss = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (postDismiss?.ProfileSwitchModalOpen == true)
            {
                throw new AvitoPageMismatchException(
                    "закрытие модалки субпрофилей",
                    AvitoPageKind.Dashboard,
                    postDismiss,
                    ["закрыть модалку Escape/навигация"]);
            }

            return html;
        }

        throw new InvalidOperationException("AdsPower CDP: не удалось снять HTML модалки переключения профилей.");
    }

    public async Task<bool> SwitchActiveProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string subProfileId,
        CancellationToken cancellationToken = default,
        bool closeBrowserAfter = false)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return false;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch click started: subProfile={subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(SwitchActiveProfileAsync),
            
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["avito.subProfileId"] = subProfileId
            });

        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        var connectOptions = new ConnectOptions
        {
            BrowserWSEndpoint = start.WebSocketDebuggerUrl,
            DefaultViewport = null
        };

        IBrowser? browser = null;
        ExceptionDispatchInfo? originalEdi = null;
        ExceptionDispatchInfo? cleanupEdi = null;
        bool result = false;
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            var page = await AcquireAutomationPageAsync(
                    browser,
                    ProfileSwitchPageUrl,
                    nameof(SwitchActiveProfileAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);

            if (IsOnCandidatesResponsesPage(page.Url))
            {
                await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(SwitchActiveProfileAsync))
                    .ConfigureAwait(false);
            }

            await EnsureSwitchModalAsync(page, cancellationToken, nameof(SwitchActiveProfileAsync))
                .ConfigureAwait(false);
            if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(SwitchActiveProfileAsync))
                    .ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId, cancellationToken).ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: subProfile {subProfileId} already current — closed modal, no click.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(SwitchActiveProfileAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "already_current",
                        ["adsPower.userId"] = adsPowerUserId,
                        ["avito.subProfileId"] = subProfileId
                    });
                result = true;
                return result;
            }

            result = await TryClickSubProfileCardAndWaitCloseAsync(page, subProfileId, adsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            originalEdi = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            try
            {
                await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowserAfter, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupEdi = ExceptionDispatchInfo.Capture(ex);
            }
        }

        originalEdi?.Throw();
        cleanupEdi?.Throw();
        return result;
    }

    private static async Task<bool> IsTargetSubProfileAlreadyCurrentAsync(
        IPage page,
        string subProfileId,
        CancellationToken cancellationToken)
    {
        var snapshot = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
        return AvitoSubProfileSwitchEffect.IsAlreadyCurrent(snapshot, subProfileId);
    }

    private static async Task<AvitoSubProfileSwitchSnapshot> ProbeSwitchSnapshotAsync(
        IPage page,
        string subProfileId,
        CancellationToken cancellationToken)
    {
        var raw = await EvaluateWithRetryAsync<string>(
                page,
                AvitoSubProfileSwitchEffect.BuildSnapshotScript(subProfileId),
                cancellationToken,
                CdpPageReadTimeout)
            .ConfigureAwait(false);
        return AvitoSubProfileSwitchEffect.ParseSnapshot(raw) ?? AvitoSubProfileSwitchSnapshot.Empty;
    }

    private static void LogSwitchTrace(
        string message,
        DeskLinkAuditLogLevel level,
        string memberName,
        string? adsPowerUserId,
        string subProfileId,
        AvitoSubProfileSwitchSnapshot? before,
        AvitoSubProfileSwitchSnapshot? after,
        string step,
        bool? pointerClick = null,
        bool? domClick = null,
        long? elapsedMs = null,
        string? exception = null)
    {
        _ = GlobalLogger.Instance.LogAsync(
            message,
            level,
            memberName: memberName,
            properties: new Dictionary<string, object?>
            {
                ["step"] = step,
                ["adsPower.userId"] = adsPowerUserId,
                ["avito.subProfileId"] = subProfileId,
                ["avito.subProfileName"] = after?.CurrentSubProfileName ?? before?.CurrentSubProfileName,
                ["page.url"] = after?.Url ?? before?.Url,
                ["switch.cardsCount"] = after?.CardsCount ?? before?.CardsCount,
                ["switch.targetFound"] = after?.TargetCardFound ?? before?.TargetCardFound,
                ["switch.currentIdBefore"] = before?.CurrentSubProfileId,
                ["switch.currentIdAfter"] = after?.CurrentSubProfileId,
                ["switch.modalBefore"] = before?.ModalOpen,
                ["switch.modalAfter"] = after?.ModalOpen,
                ["switch.pointerClick"] = pointerClick,
                ["switch.domClick"] = domClick,
                ["switch.elapsedMs"] = elapsedMs,
                ["error"] = exception
            });
    }

    /// <summary>
    /// Закрывает всплывающие окна Avito (настройка звонков, промо и т.п.), которые перекрывают навигацию и модалку субпрофилей.
    /// </summary>
    private static async Task DismissAvitoBlockingOverlaysAsync(IPage page, CancellationToken cancellationToken)
    {
        const string dismissScript = """
            (() => {
              const tryClick = (el) => {
                if (!el) return false;
                try { el.click(); return true; } catch { return false; }
              };
              const isCloseButton = (btn) => {
                const label = (btn.getAttribute("aria-label") || "").toLowerCase();
                const text = (btn.textContent || "").trim();
                return label.includes("закры") || /^[×✕xX]$/.test(text);
              };
              const containers = Array.from(document.querySelectorAll(
                '[role="dialog"], [class*="modal" i], [class*="Modal"], [class*="popup" i], [class*="Popup"]'));
              for (const box of containers) {
                if (box.closest("[data-marker='component-profile-switch/root']")
                    || box.matches("[data-marker='component-profile-switch/root']")) {
                  continue;
                }
                const close = Array.from(box.querySelectorAll("button")).find(isCloseButton)
                  || box.querySelector('[data-marker*="close" i]');
                if (tryClick(close)) return true;
              }
              const promo = Array.from(document.querySelectorAll("h1,h2,h3,h4,p,div"))
                .find((el) => /звонить|рабочие часы/i.test((el.textContent || "").trim()));
              if (promo) {
                const box = promo.closest("div");
                if (box) {
                  const close = Array.from(box.querySelectorAll("button")).find(isCloseButton);
                  if (tryClick(close)) return true;
                }
              }
              return false;
            })()
            """;

        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                        page,
                        dismissScript,
                        cancellationToken,
                        CdpSwitchActionTimeout)
                    .ConfigureAwait(false))
            {
                await Task.Delay(450, cancellationToken).ConfigureAwait(false);
            }

            var switchModalOpen = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                    page,
                    "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))",
                    cancellationToken,
                    CdpPageReadTimeout)
                .ConfigureAwait(false);
            if (switchModalOpen)
            {
                return;
            }

            try
            {
                await AdsPowerCdpGuard.WaitAsync(
                        page.Keyboard.PressAsync("Escape"),
                        CdpSwitchActionTimeout,
                        "Escape для оверлея Avito",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch
            {
                // best effort
            }

            await Task.Delay(280, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Закрывает модалку «Выбор профиля», если она открыта (Escape, затем уход на /profile/pro/items).</summary>
    private static async Task DismissProfileSwitchModalAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var open = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                    page,
                    "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))",
                    cancellationToken,
                    CdpPageReadTimeout)
                .ConfigureAwait(false);
            if (!open)
            {
                return;
            }

            try
            {
                await AdsPowerCdpGuard.WaitAsync(
                        page.Keyboard.PressAsync("Escape"),
                        CdpSwitchActionTimeout,
                        "Escape для модалки субпрофилей",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Dismiss profile-switch modal: Escape press failed: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(DismissProfileSwitchModalAsync));
            }

            await Task.Delay(450, cancellationToken).ConfigureAwait(false);
        }

        var stillOpen = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                page,
                "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))",
                cancellationToken,
                CdpPageReadTimeout)
            .ConfigureAwait(false);
        if (!stillOpen)
        {
            return;
        }

        try
        {
            await AdsPowerCdpGuard.WaitAsync(
                    page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
                    {
                        Timeout = 45_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    }),
                    CdpNavigationGuardTimeout,
                    "уход с модалки субпрофилей на объявления",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"DismissProfileSwitchModalAsync: navigation failed: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(DismissProfileSwitchModalAsync));
        }
    }

    private static async Task<bool> TryClickSubProfileCardAndWaitCloseAsync(
        IPage page,
        string subProfileId,
        string? adsPowerUserId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var before = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
        LogSwitchTrace(
            $"AdsPower profile-switch: snapshot before click subProfile={subProfileId}, cards={before.CardsCount}, current={before.CurrentSubProfileId}, targetFound={before.TargetCardFound}.",
            DeskLinkAuditLogLevel.Info,
            nameof(TryClickSubProfileCardAndWaitCloseAsync),
            adsPowerUserId,
            subProfileId,
            before,
            after: null,
            step: "switch_snapshot_before");

        if (!before.TargetCardFound)
        {
            var selector = AvitoSubProfileSwitchEffect.BuildCardSelector(subProfileId);
            try
            {
                await AdsPowerCdpGuard.WaitAsync(
                        page.WaitForSelectorAsync(
                            selector,
                            new WaitForSelectorOptions { Timeout = 12_000 }),
                        TimeSpan.FromSeconds(14),
                        "ожидание карточки субпрофиля",
                        cancellationToken)
                    .ConfigureAwait(false);
                before = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex) when (AdsPowerCdpGuard.IsCdpTimeout(ex))
            {
                LogSwitchTrace(
                    $"AdsPower profile-switch: CDP hung waiting for target card {subProfileId}: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(TryClickSubProfileCardAndWaitCloseAsync),
                    adsPowerUserId,
                    subProfileId,
                    before,
                    after: null,
                    step: "card_cdp_timeout",
                    elapsedMs: started.ElapsedMilliseconds,
                    exception: ex.Message);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSwitchTrace(
                    $"AdsPower profile-switch: target card not found in time: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(TryClickSubProfileCardAndWaitCloseAsync),
                    adsPowerUserId,
                    subProfileId,
                    before,
                    after: null,
                    step: "card_timeout",
                    elapsedMs: started.ElapsedMilliseconds,
                    exception: ex.Message);
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (!before.TargetCardFound)
        {
            LogSwitchTrace(
                $"AdsPower profile-switch: target selector missing for subProfile {subProfileId}.",
                DeskLinkAuditLogLevel.Warning,
                nameof(TryClickSubProfileCardAndWaitCloseAsync),
                adsPowerUserId,
                subProfileId,
                before,
                after: null,
                step: "card_missing",
                elapsedMs: started.ElapsedMilliseconds);
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var selectorToClick = AvitoSubProfileSwitchEffect.BuildCardSelector(subProfileId);
        var pointerClick = false;
        var domClick = false;
        try
        {
            pointerClick = await AdsPowerCdpGuard.WaitAsync(
                    AvitoHumanPointer.TryClickSelectorAsync(page, selectorToClick, cancellationToken),
                    CdpSwitchActionTimeout,
                    "pointer-click по карточке субпрофиля",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSwitchTrace(
                $"AdsPower profile-switch: pointer-click threw for {subProfileId}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                nameof(TryClickSubProfileCardAndWaitCloseAsync),
                adsPowerUserId,
                subProfileId,
                before,
                after: null,
                step: "pointer_click_error",
                pointerClick: false,
                elapsedMs: started.ElapsedMilliseconds,
                exception: ex.Message);
        }

        if (!pointerClick)
        {
            domClick = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                    page,
                    BuildClickSubProfileJs(subProfileId),
                    cancellationToken,
                    CdpSwitchActionTimeout)
                .ConfigureAwait(false);
        }

        LogSwitchTrace(
            $"AdsPower profile-switch: click result pointer={pointerClick}, dom={domClick} for subProfile {subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            nameof(TryClickSubProfileCardAndWaitCloseAsync),
            adsPowerUserId,
            subProfileId,
            before,
            after: null,
            step: "click_result",
            pointerClick: pointerClick,
            domClick: domClick,
            elapsedMs: started.ElapsedMilliseconds);

        if (!pointerClick && !domClick)
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var deadline = DateTime.UtcNow + CdpSwitchEffectTimeout;
        AvitoSubProfileSwitchSnapshot after = before;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            after = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
            if (AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(after, subProfileId))
            {
                break;
            }

            if (AvitoSubProfileSwitchEffect.TargetBecameCurrent(after, subProfileId) && after.ModalOpen)
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                after = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
                if (AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(after, subProfileId)
                    || AvitoSubProfileSwitchEffect.TargetBecameCurrent(after, subProfileId))
                {
                    break;
                }
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        if (!AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(after, subProfileId)
            && AvitoSubProfileSwitchEffect.TargetBecameCurrent(after, subProfileId))
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            after = await ProbeSwitchSnapshotAsync(page, subProfileId, cancellationToken).ConfigureAwait(false);
        }

        if (!AvitoSubProfileSwitchEffect.IsSuccessfulSwitch(after, subProfileId)
            && !AvitoSubProfileSwitchEffect.TargetBecameCurrent(after, subProfileId))
        {
            if (AvitoSubProfileSwitchEffect.ClickReportedSuccessWithoutDomChange(
                    before, after, subProfileId, pointerClick || domClick))
            {
                LogSwitchTrace(
                    $"AdsPower profile-switch: click reported success but DOM did not change for subProfile {subProfileId}.",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(TryClickSubProfileCardAndWaitCloseAsync),
                    adsPowerUserId,
                    subProfileId,
                    before,
                    after,
                    step: "click_no_dom_change",
                    pointerClick: pointerClick,
                    domClick: domClick,
                    elapsedMs: started.ElapsedMilliseconds);
            }
            else
            {
                LogSwitchTrace(
                    $"AdsPower profile-switch: effect wait timed out for subProfile {subProfileId}.",
                    DeskLinkAuditLogLevel.Warning,
                    nameof(TryClickSubProfileCardAndWaitCloseAsync),
                    adsPowerUserId,
                    subProfileId,
                    before,
                    after,
                    step: "modal_close_timeout",
                    pointerClick: pointerClick,
                    domClick: domClick,
                    elapsedMs: started.ElapsedMilliseconds);
            }

            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await HumanDelay.AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);

        LogSwitchTrace(
            $"AdsPower profile-switch: subProfile {subProfileId} activated.",
            DeskLinkAuditLogLevel.Info,
            nameof(TryClickSubProfileCardAndWaitCloseAsync),
            adsPowerUserId,
            subProfileId,
            before,
            after,
            step: "switched",
            pointerClick: pointerClick,
            domClick: domClick,
            elapsedMs: started.ElapsedMilliseconds);

        return true;
    }

    public async Task OpenUrlInRunningProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string url,
        CancellationToken cancellationToken = default,
        bool closeBrowserAfter = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adsPowerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var target = url.Trim();

        using var captchaScope = await UseProfileCaptchaContextAsync(options, adsPowerUserId, cancellationToken)
            .ConfigureAwait(false);
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, openUrl: null, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            throw new InvalidOperationException(
                "AdsPower не вернул ws.puppeteer endpoint. Проверьте Local API и версию клиента AdsPower.");
        }

        IBrowser? browser = null;
        try
        {
            browser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserWSEndpoint = start.WebSocketDebuggerUrl,
                DefaultViewport = null
            }).ConfigureAwait(false);

            var page = await AcquireAutomationPageAsync(
                    browser,
                    target,
                    nameof(OpenUrlInRunningProfileAsync),
                    cancellationToken,
                    waitForStartupNavigation: true)
                .ConfigureAwait(false);
            try
            {
                await page.GoToAsync(target, new NavigationOptions
                {
                    Timeout = 90_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                await page.GoToAsync(target, new NavigationOptions
                {
                    Timeout = 90_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);
            }

            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower: URL открыт через CDP.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(OpenUrlInRunningProfileAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["targetUrl"] = target
                });
        }
        finally
        {
            await ReleaseAdsPowerSessionAsync(browser, options, adsPowerUserId, closeBrowserAfter, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task CloseBrowserAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default) =>
        adsPowerApiClient.StopBrowserAsync(options, adsPowerUserId, cancellationToken);

    private async Task ReleaseAdsPowerSessionAsync(
        IBrowser? browser,
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        bool closeBrowser,
        CancellationToken cancellationToken)
    {
        try
        {
            browser?.Disconnect();
        }
        catch
        {
            // Disconnect must never throw out of the finally.
        }

        if (!closeBrowser)
        {
            return;
        }

        try
        {
            await adsPowerApiClient
                .StopBrowserAsync(options, adsPowerUserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower browser/stop failed after automation for profile {adsPowerUserId}: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(ReleaseAdsPowerSessionAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["adsPower.baseUrl"] = options.BaseUrl,
                    ["error.type"] = ex.GetType().FullName
                });
        }
    }

    /// <summary>
    /// Всегда открывает модалку «Выбор профиля» через <c>/profile/dashboard#profile/switch?withEntities=true</c>,
    /// чтобы прочитать актуальный <c>isCurrent</c>, а не состояние с другой страницы Avito.
    /// </summary>
    private async Task EnsureSwitchModalAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName = nameof(EnsureSwitchModalAsync))
    {
        var alreadyOpen = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (alreadyOpen is { ProfileSwitchModalOpen: true, ProfileCardsCount: > 0 })
        {
            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower profile-switch: modal already open with cards, skip navigation.",
                DeskLinkAuditLogLevel.Info,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switch_modal_already_open",
                    ["page.url"] = page.Url,
                    ["switch.cardsCount"] = alreadyOpen.ProfileCardsCount,
                    ["switch.currentId"] = alreadyOpen.CurrentSubProfileId
                });
            return;
        }

        if (IsOnProfileSwitchPage(page.Url))
        {
            await BounceToDashboardBeforeSwitchAsync(page, cancellationToken, callerMemberName)
                .ConfigureAwait(false);
        }

        for (var i = 0; i <= 2; i++)
        {
            try
            {
                await AdsPowerCdpGuard.WaitAsync(
                        page.GoToAsync(ProfileSwitchPageUrl, new NavigationOptions
                        {
                            Timeout = 45_000,
                            WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                        }),
                        CdpNavigationGuardTimeout,
                        "навигация на dashboard#profile/switch",
                        cancellationToken)
                    .ConfigureAwait(false);

                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower profile-switch: switch page navigation completed.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: callerMemberName,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "switch_nav_ok",
                        ["page.url"] = page.Url,
                        ["attempt"] = i + 1
                    });
                await ThrowIfCaptchaOnPageAsync(page, cancellationToken).ConfigureAwait(false);
                var switchNavState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (switchNavState?.IsTransientPageError == true)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "AdsPower profile-switch: Avito error page after switch navigation, refreshing.",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: callerMemberName,
                        properties: new Dictionary<string, object?>
                        {
                            ["step"] = "switch_nav_transient_error",
                            ["page.url"] = page.Url,
                            ["attempt"] = i + 1
                        });

                    var recovered = await TryRecoverTransientAvitoErrorAsync(
                            page,
                            cancellationToken,
                            callerMemberName)
                        .ConfigureAwait(false);
                    if (!recovered)
                    {
                        if (i < 2)
                        {
                            continue;
                        }

                        break;
                    }
                }

                if (IsOnProfileSwitchPage(page.Url))
                {
                    return;
                }
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: navigation retry {i + 1}/3: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: callerMemberName,
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "switch_nav_retry",
                        ["page.url"] = page.Url,
                        ["attempt"] = i + 1
                    });

                if (i < 2)
                {
                    await Task.Delay((i + 1) * 1400, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new TimeoutException(
            $"{AdsPowerCdpGuard.TimeoutPrefix} не удалось открыть страницу переключения суб-профилей Avito (навигация на dashboard#profile/switch).");
    }

    private static bool IsOnProfileSwitchPage(string? url) =>
        !string.IsNullOrEmpty(url)
        && (url.Contains("profile/switch", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dashboard#profile", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// SPA Avito не переоткрывает модалку при повторном GoTo на тот же hash — сначала уходим на чистый dashboard.
    /// </summary>
    private static async Task BounceToDashboardBeforeSwitchAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        _ = GlobalLogger.Instance.LogAsync(
            "AdsPower profile-switch: bounce to dashboard before reopening switch modal.",
            DeskLinkAuditLogLevel.Info,
            memberName: callerMemberName,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "switch_dashboard_bounce",
                ["page.url"] = page.Url
            });

        try
        {
            await AdsPowerCdpGuard.WaitAsync(
                    page.GoToAsync(ProfileDashboardPageUrl, new NavigationOptions
                    {
                        Timeout = 45_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    }),
                    CdpNavigationGuardTimeout,
                    "bounce на dashboard перед модалкой субпрофилей",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            await AdsPowerCdpGuard.WaitAsync(
                    page.GoToAsync(ProfileDashboardPageUrl, new NavigationOptions
                    {
                        Timeout = 45_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    }),
                    CdpNavigationGuardTimeout,
                    "повторный bounce на dashboard",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ждём корень модалки и карточки профилей перед чтением <c>isCurrent</c> или кликом.</summary>
    private async Task<bool> AwaitProfileSwitchModalContentAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        if (await TryAwaitProfileSwitchModalContentOnceAsync(page, cancellationToken, callerMemberName)
                .ConfigureAwait(false))
        {
            return true;
        }

        _ = GlobalLogger.Instance.LogAsync(
            "AdsPower profile-switch: modal/cards not ready — retry via dashboard bounce.",
            DeskLinkAuditLogLevel.Warning,
            memberName: callerMemberName,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "switch_modal_retry",
                ["page.url"] = page.Url
            });

        await BounceToDashboardBeforeSwitchAsync(page, cancellationToken, callerMemberName)
            .ConfigureAwait(false);

        try
        {
            await page.GoToAsync(ProfileSwitchPageUrl, new NavigationOptions
            {
                Timeout = 45_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
        }

        return await TryAwaitProfileSwitchModalContentOnceAsync(page, cancellationToken, callerMemberName)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryAwaitProfileSwitchModalContentOnceAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        await ThrowIfCaptchaOnPageAsync(page, cancellationToken).ConfigureAwait(false);
        var probe = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (probe is { ProfileSwitchModalOpen: true, ProfileCardsCount: > 0 })
        {
            await HumanDelay.AfterSwitchModalAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!IsOnProfileSwitchPage(page.Url) && probe?.ProfileSwitchModalOpen != true)
        {
            return false;
        }

        var modalReady = false;
        try
        {
            await AdsPowerCdpGuard.WaitAsync(
                    page.WaitForSelectorAsync(
                        "[data-marker='component-profile-switch/root']",
                        new WaitForSelectorOptions { Timeout = 18_000 }),
                    TimeSpan.FromSeconds(20),
                    "ожидание корня модалки субпрофилей",
                    cancellationToken)
                .ConfigureAwait(false);
            modalReady = true;
        }
        catch (TimeoutException ex) when (AdsPowerCdpGuard.IsCdpTimeout(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: modal selector wait timed out: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "modal_timeout",
                    ["page.url"] = page.Url
                });
        }

        if (!modalReady)
        {
            await ThrowIfCaptchaOnPageAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            await AdsPowerCdpGuard.WaitAsync(
                    page.WaitForFunctionAsync(
                        "() => !!document.querySelector(\"[data-marker^='component-profile-switch/profile-']\")",
                        new WaitForFunctionOptions { Timeout = 12_000, PollingInterval = 650 }),
                    TimeSpan.FromSeconds(14),
                    "ожидание карточек субпрофилей",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex) when (AdsPowerCdpGuard.IsCdpTimeout(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: profile cards not detected in time: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "cards_timeout",
                    ["page.url"] = page.Url
                });
            return false;
        }

        await HumanDelay.AfterSwitchModalAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static string BuildClickSubProfileJs(string subProfileId) =>
        $@"(() => {{
            const el = document.querySelector('[data-marker=""component-profile-switch/profile-{Escape(subProfileId)}""]');
            if (!el) return false;
            const rect = el.getBoundingClientRect();
            const x = rect.left + Math.max(rect.width, 1) * (0.32 + Math.random() * 0.36);
            const y = rect.top + Math.max(rect.height, 1) * (0.32 + Math.random() * 0.36);
            const base = {{ bubbles: true, cancelable: true, view: window, clientX: x, clientY: y, button: 0, buttons: 1 }};
            try {{ el.scrollIntoView({{ block: 'center', inline: 'nearest' }}); }} catch {{}}
            if (typeof PointerEvent === 'function') {{
                el.dispatchEvent(new PointerEvent('pointerdown', Object.assign({{ pointerType: 'mouse', isPrimary: true, pointerId: 1 }}, base)));
            }}
            el.dispatchEvent(new MouseEvent('mousedown', base));
            if (typeof PointerEvent === 'function') {{
                el.dispatchEvent(new PointerEvent('pointerup', Object.assign({{ pointerType: 'mouse', isPrimary: true, pointerId: 1 }}, base)));
            }}
            el.dispatchEvent(new MouseEvent('mouseup', Object.assign({{}}, base, {{ buttons: 0 }})));
            el.dispatchEvent(new MouseEvent('click', Object.assign({{}}, base, {{ buttons: 0 }})));
            return true;
        }})()";

    private static Task<AvitoPageState?> ProbePageStateAsync(IPage page, CancellationToken cancellationToken) =>
        AvitoPageStateProbe.TryProbeAsync(
            (script, ct) => EvaluateWithRetryAsync<string>(page, script, ct, CdpPageReadTimeout),
            cancellationToken);

    private static async Task<bool> TryRecoverAvitoLoginAsync(IPage page, CancellationToken cancellationToken)
    {
        var recovery = await AvitoAutoLoginRecovery.TryRecoverAsync(page, cancellationToken).ConfigureAwait(false);
        return recovery.Recovered;
    }

    private async Task EnsureOnCandidatesPageAsync(
        IPage page,
        string adsPowerUserId,
        CancellationToken cancellationToken)
    {
        var executeScript = (string script, CancellationToken ct) =>
            EvaluateWithRetryAsync<string>(page, script, ct);

        var recoveryAttempts = new List<string>();
        const int maxAttemptsPerUrl = 2;

        var initialState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (initialState?.IsOnCandidates == true && initialState.CandidatesItemCount > 0)
        {
            return;
        }

        foreach (var targetUrl in AvitoCandidatesPageUrls.NavigationOrder)
        {
            for (var attempt = 1; attempt <= maxAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (state?.IsTransientPageError == true)
                {
                    recoveryAttempts.Add($"попытка {attempt}: обновить страницу (ошибка Avito, прокси мог подвиснуть)");
                    if (await TryRecoverTransientAvitoErrorAsync(
                            page,
                            cancellationToken,
                            nameof(EnsureOnCandidatesPageAsync)).ConfigureAwait(false))
                    {
                        continue;
                    }
                }

                if (AvitoAutomationFailureFormatter.SuggestsLogin(state))
                {
                    if (await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    throw new AvitoLoginRequiredException(state?.Url, state?.Title);
                }

                if (state?.IsOnCandidates == true && state.CandidatesItemCount > 0)
                {
                    return;
                }

                if (state?.ProfileSwitchModalOpen == true)
                {
                    recoveryAttempts.Add($"попытка {attempt}: закрыть модалку субпрофилей");
                    await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                    state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                    if (state?.IsOnCandidates == true && state.CandidatesItemCount > 0)
                    {
                        return;
                    }
                }

                var alreadyOnTarget = IsOnUrl(page.Url, targetUrl);
                _ = GlobalLogger.Instance.LogAsync(
                    alreadyOnTarget
                        ? $"AdsPower candidates: hard navigation attempt {attempt}/{maxAttemptsPerUrl} ({targetUrl})."
                        : $"AdsPower candidates: navigating to responses page (attempt {attempt}/{maxAttemptsPerUrl}).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(EnsureOnCandidatesPageAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = alreadyOnTarget ? "candidates_hard_nav" : "candidates_goto",
                        ["attempt"] = attempt,
                        ["page.url"] = page.Url,
                        ["avito.url"] = targetUrl,
                        ["pageState"] = state?.DescribeForDiagnostics()
                    });

                recoveryAttempts.Add($"попытка {attempt}: переход на {targetUrl}");

                var navigationOptions = new NavigationOptions
                {
                    Timeout = 60_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                };

                try
                {
                    await page.GoToAsync(targetUrl, navigationOptions).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRecoverableNavigationError(ex))
                {
                    await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                    await page.GoToAsync(targetUrl, navigationOptions).ConfigureAwait(false);
                }

                state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);

                string? staleListSignature = null;
                if (AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(page.Url))
                {
                    staleListSignature = await AvitoCandidatesPageWaiter
                        .TryCaptureListSignatureAsync(executeScript, cancellationToken)
                        .ConfigureAwait(false);
                }

                await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken, page)
                    .ConfigureAwait(false);

                await AvitoCandidatesPageWaiter
                    .WaitForCandidatesOrThrowFirewallAsync(
                        executeScript,
                        async ct =>
                        {
                            try
                            {
                                return await page.GetContentAsync().ConfigureAwait(false);
                            }
                            catch
                            {
                                return null;
                            }
                        },
                        page.Url,
                        cancellationToken,
                        staleListSignature,
                        page,
                        CreateCaptchaSolveCallback(page))
                    .ConfigureAwait(false);

                state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (state?.IsOnCandidates == true && state.CandidatesItemCount > 0)
                {
                    return;
                }

                if (state?.IsOnCandidates == true && state.CandidatesItemCount == 0)
                {
                    return;
                }
            }
        }

        await WaitForPageContentOrLoginAsync(page, cancellationToken).ConfigureAwait(false);

        await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken, page)
            .ConfigureAwait(false);

        var finalState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (finalState?.IsOnCandidates == true)
        {
            return;
        }

        if (AvitoAutomationFailureFormatter.SuggestsLogin(finalState))
        {
            if (await TryRecoverAvitoLoginAsync(page, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            throw new AvitoLoginRequiredException(finalState?.Url, finalState?.Title);
        }

        throw new AvitoPageMismatchException(
            "отклики",
            AvitoPageKind.Candidates,
            finalState,
            recoveryAttempts);
    }

    private static async Task WaitForPageContentOrLoginAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForFunctionAsync(
                    """
                    () => {
                        const bodyLen = (document.body?.innerText ?? '').trim().length;
                        if (bodyLen > 80) return true;
                        if (document.querySelector("[data-marker='login-form'], [data-marker='auth-app-root']")) return true;
                        if (document.querySelector("[data-marker='job-application/item']")) return true;
                        if (/\/profile\/login|\/profile\/auth|avito\.ru\/login|#login\b/i.test(location.href)) return true;
                        return document.readyState === 'complete' && bodyLen > 0;
                    }
                    """,
                    new WaitForFunctionOptions
                    {
                        Timeout = 10_000,
                        PollingInterval = 500
                    })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best effort — дальше сработает probe/login-detector.
        }
    }

    /// <summary>
    /// Со страницы откликов SPA часто зависает прямой переход на модалку switch — сначала уходим на «Мои объявления».
    /// </summary>
    private static async Task NavigateAwayFromCandidatesForSwitchAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        _ = GlobalLogger.Instance.LogAsync(
            "AdsPower profile-switch: leaving candidates page before opening switch modal.",
            DeskLinkAuditLogLevel.Info,
            memberName: callerMemberName,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "switch_leave_candidates",
                ["page.url"] = page.Url
            });

        try
        {
            await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
            {
                Timeout = 45_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            await page.GoToAsync(ProfileItemsPageUrl, new NavigationOptions
            {
                Timeout = 45_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }
    }

    private enum AvitoAutomationPageKind
    {
        Candidates,
        ActiveItems,
        BlockedItems,
        ProfileSwitch,
        Other
    }

    /// <summary>
    /// Одна рабочая вкладка на сессию CDP: берём уже открытую вкладку AdsPower (в том числе стартовый
    /// about:blank) и навигируем её. Новую вкладку через CDP не создаём, если есть хоть одна существующая —
    /// новая about:blank в AdsPower часто остаётся мёртвой.
    /// </summary>
    private static async Task<IPage> AcquireAutomationPageAsync(
        IBrowser browser,
        string preferredUrl,
        string callerMemberName,
        CancellationToken cancellationToken,
        bool waitForStartupNavigation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (waitForStartupNavigation)
        {
            await WaitForAdsPowerStartupNavigationAsync(browser, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
            await EnsureAdsPowerProxyReadyAsync(browser, callerMemberName, cancellationToken)
                .ConfigureAwait(false);
        }

        var targetKind = ClassifyAutomationPageKind(preferredUrl);
        var existingPages = (await GetBrowserPagesAsync(
                browser,
                "выбор рабочей вкладки",
                cancellationToken)
            .ConfigureAwait(false)).ToList();
        var existingWorkerPageIndex = SelectExistingAutomationPageIndex(
            existingPages.Select(static page => page.Url).ToArray(),
            preferredUrl);
        var worker = existingWorkerPageIndex >= 0 ? existingPages[existingWorkerPageIndex] : null;
        // Новая about:blank через NewPageAsync в AdsPower часто мёртвая. Берём уже открытую вкладку.
        worker ??= existingPages.Count > 0
            ? existingPages[0]
            : await browser.NewPageAsync().ConfigureAwait(false);
        var closed = 0;
        if (ShouldCloseNonWorkerPages(worker.Url))
        {
            var pagesToClose = existingPages
                .Where(page => !ReferenceEquals(page, worker))
                .ToArray();
            closed = await CloseBrowserPagesAsync(pagesToClose, callerMemberName).ConfigureAwait(false);
        }

        try
        {
            await worker.BringToFrontAsync().ConfigureAwait(false);
        }
        catch
        {
            // Не критично для парсинга.
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower CDP: рабочая вкладка выбрана (закрыто лишних: {closed}, было: {existingPages.Count}).",
            DeskLinkAuditLogLevel.Info,
            memberName: callerMemberName,
            properties: new Dictionary<string, object?>
            {
                ["automation.targetKind"] = targetKind.ToString(),
                ["automation.preferredUrl"] = preferredUrl,
                ["automation.workerUrl"] = worker.Url,
                ["automation.tabsClosed"] = closed,
                ["automation.tabsBefore"] = existingPages.Count
            });

        return worker;
    }

    internal static async Task WaitForAdsPowerStartupNavigationAsync(
        IBrowser browser,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartupNavigationMaxWaitMs);
        var poll = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartupNavigationPollMs);
        var elapsed = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var urls = (await GetBrowserPagesAsync(
                    browser,
                    "ожидание стартовой навигации",
                    cancellationToken)
                .ConfigureAwait(false))
                .Select(static page => page.Url)
                .ToArray();

            if (!ShouldKeepWaitingForStartupNavigation(urls, elapsed, timeout))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower CDP: ожидание стартовой навигации завершено ({elapsed.TotalMilliseconds:F0} мс).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: callerMemberName,
                    properties: new Dictionary<string, object?>
                    {
                        ["automation.startupWaitMs"] = elapsed.TotalMilliseconds,
                        ["automation.startupUrls"] = string.Join(" | ", urls)
                    });
                return;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            elapsed += poll;
        }
    }

    /// <summary>
    /// Если AdsPower открыл стартовую вкладку с проверкой прокси — ждём результат.
    /// «Proxy failure» означает, что на Avito идти незачем.
    /// </summary>
    internal static async Task EnsureAdsPowerProxyReadyAsync(
        IBrowser browser,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartPageProxyCheckMaxWaitMs);
        var poll = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartupNavigationPollMs);
        var elapsed = TimeSpan.Zero;
        string? lastStartUrl = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pages = (await GetBrowserPagesAsync(
                    browser,
                    "проверка стартовой вкладки",
                    cancellationToken)
                .ConfigureAwait(false)).ToList();
            var urls = new List<string>(pages.Count);
            IPage? startPage = null;

            foreach (var page in pages)
            {
                var url = await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false);
                urls.Add(url);
                if (startPage is null && AdsPowerStartPage.IsUrl(url))
                {
                    startPage = page;
                    lastStartUrl = url;
                }
            }

            if (urls.Any(IsUsableAvitoPageUrl))
            {
                return;
            }

            if (startPage is not null)
            {
                var status = await ProbeStartPageProxyStatusAsync(startPage, cancellationToken)
                    .ConfigureAwait(false);
                if (status == AdsPowerStartPageProxyStatus.Failed)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower: стартовая страница показала отказ прокси ({startPage.Url}).",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: callerMemberName,
                        errorKey: AdsPowerProxyFailureException.ErrorKey,
                        properties: new Dictionary<string, object?>
                        {
                            ["automation.startPageUrl"] = startPage.Url,
                            ["automation.startupUrls"] = string.Join(" | ", urls)
                        });
                    throw new AdsPowerProxyFailureException(startPage.Url);
                }

                if (status == AdsPowerStartPageProxyStatus.Ok)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        "AdsPower: проверка прокси на стартовой странице прошла.",
                        DeskLinkAuditLogLevel.Info,
                        memberName: callerMemberName,
                        properties: new Dictionary<string, object?>
                        {
                            ["automation.startPageUrl"] = startPage.Url,
                            ["automation.proxyCheckWaitMs"] = elapsed.TotalMilliseconds
                        });
                    return;
                }
            }

            if (elapsed >= timeout)
            {
                if (startPage is not null || AdsPowerStartPage.IsUrl(lastStartUrl))
                {
                    throw new AdsPowerProxyFailureException(
                        startPage?.Url ?? lastStartUrl,
                        "проверка прокси на стартовой странице AdsPower не завершилась.");
                }

                return;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            elapsed += poll;
        }
    }

    private static async Task<AdsPowerStartPageProxyStatus> ProbeStartPageProxyStatusAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await page.GetContentAsync()
                .WaitAsync(CdpPageReadTimeout, cancellationToken)
                .ConfigureAwait(false);
            var fromHtml = AdsPowerStartPage.Parse(html);
            if (fromHtml != AdsPowerStartPageProxyStatus.Unknown)
            {
                return fromHtml;
            }
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"AdsPower CDP: не прочитал стартовую вкладку за {CdpPageReadTimeout.TotalSeconds:0} с.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        try
        {
            var text = await page.EvaluateExpressionAsync<string>(
                    "(() => (document.body && document.body.innerText) || '')()")
                .WaitAsync(CdpPageReadTimeout, cancellationToken)
                .ConfigureAwait(false);
            return AdsPowerStartPage.Parse(text);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"AdsPower CDP: не прочитал текст стартовой вкладки за {CdpPageReadTimeout.TotalSeconds:0} с.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return AdsPowerStartPageProxyStatus.Unknown;
        }
    }

    internal enum AdsPowerStartupNavigationStep
    {
        None = 0,
        PageNavigate = 1,
        LocationAssign = 2,
        Done = 3,
        Failed = 4
    }

    internal static AdsPowerStartupNavigationStep NextStartupNavigationStep(
        string? currentUrl,
        AdsPowerStartupNavigationStep lastAttempt)
    {
        if (IsUsableWorkerPageUrl(currentUrl))
        {
            return AdsPowerStartupNavigationStep.Done;
        }

        return lastAttempt switch
        {
            AdsPowerStartupNavigationStep.None => AdsPowerStartupNavigationStep.PageNavigate,
            AdsPowerStartupNavigationStep.PageNavigate => AdsPowerStartupNavigationStep.LocationAssign,
            _ => AdsPowerStartupNavigationStep.Failed
        };
    }

    internal static async Task<IPage> NavigateOffStartupPlaceholderAsync(
        IPage page,
        string targetUrl,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUrl);

        var current = page;
        var lastAttempt = AdsPowerStartupNavigationStep.None;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var avito = await FindUsableAvitoPageAsync(current.Browser, cancellationToken).ConfigureAwait(false);
            if (avito is not null)
            {
                return avito;
            }

            var currentUrl = await ReadPageUrlAsync(current, cancellationToken).ConfigureAwait(false);
            var next = NextStartupNavigationStep(currentUrl, lastAttempt);
            if (next == AdsPowerStartupNavigationStep.Done)
            {
                return current;
            }

            if (next == AdsPowerStartupNavigationStep.Failed)
            {
                throw new InvalidOperationException(
                    $"AdsPower: вкладка осталась на «{currentUrl}», страница Avito не открылась.");
            }

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower CDP: навигация {next} → {targetUrl} (сейчас {currentUrl}).",
                DeskLinkAuditLogLevel.Info,
                memberName: callerMemberName,
                properties: new Dictionary<string, object?>
                {
                    ["automation.navStep"] = next.ToString(),
                    ["automation.targetUrl"] = targetUrl,
                    ["page.url"] = currentUrl
                });

            lastAttempt = next;
            current = await ExecuteStartupNavigationStepAsync(current, targetUrl, next, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IPage> ExecuteStartupNavigationStepAsync(
        IPage page,
        string targetUrl,
        AdsPowerStartupNavigationStep step,
        CancellationToken cancellationToken)
    {
        switch (step)
        {
            case AdsPowerStartupNavigationStep.PageNavigate:
                await TryCdpPageNavigateAsync(page, targetUrl, cancellationToken).ConfigureAwait(false);
                return await PollUntilLeftPlaceholderAsync(page, cancellationToken).ConfigureAwait(false);

            case AdsPowerStartupNavigationStep.LocationAssign:
                await TryAssignLocationAsync(page, targetUrl, cancellationToken).ConfigureAwait(false);
                return await PollUntilLeftPlaceholderAsync(page, cancellationToken).ConfigureAwait(false);

            default:
                return page;
        }
    }

    private static async Task TryCdpPageNavigateAsync(
        IPage page,
        string targetUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await page.CreateCDPSessionAsync().ConfigureAwait(false);
            try
            {
                await client.SendAsync(
                        "Page.navigate",
                        new Dictionary<string, object> { ["url"] = targetUrl })
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await client.DetachAsync().ConfigureAwait(false);
                }
                catch
                {
                    // сессия CDP могла уже закрыться
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private static async Task TryAssignLocationAsync(
        IPage page,
        string targetUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var jsUrl = JsonSerializer.Serialize(targetUrl);
            await page.EvaluateExpressionAsync($"window.location.assign({jsUrl})").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private static async Task<IPage> PollUntilLeftPlaceholderAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerForcedNavigationMaxWaitMs);
        var poll = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartupNavigationPollMs);
        var elapsed = TimeSpan.Zero;
        while (elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var avito = await FindUsableAvitoPageAsync(page.Browser, cancellationToken).ConfigureAwait(false);
            if (avito is not null)
            {
                return avito;
            }

            if (IsUsableWorkerPageUrl(await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false)))
            {
                return page;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            elapsed += poll;
        }

        return await FindUsableAvitoPageAsync(page.Browser, cancellationToken).ConfigureAwait(false) ?? page;
    }

    private static async Task<IPage> PollUntilAvitoPageAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerForcedNavigationMaxWaitMs);
        var poll = TimeSpan.FromMilliseconds(MonitoringTiming.AdsPowerStartupNavigationPollMs);
        var elapsed = TimeSpan.Zero;
        while (elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var avito = await FindUsableAvitoPageAsync(page.Browser, cancellationToken).ConfigureAwait(false);
            if (avito is not null)
            {
                return avito;
            }

            if (IsUsableAvitoPageUrl(await ReadPageUrlAsync(page, cancellationToken).ConfigureAwait(false)))
            {
                return page;
            }

            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            elapsed += poll;
        }

        return await FindUsableAvitoPageAsync(page.Browser, cancellationToken).ConfigureAwait(false) ?? page;
    }

    private static async Task<IPage?> FindUsableAvitoPageAsync(
        IBrowser browser,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in await GetBrowserPagesAsync(
                     browser,
                     "поиск вкладки Avito",
                     cancellationToken).ConfigureAwait(false))
        {
            if (IsUsableAvitoPageUrl(candidate.Url))
            {
                return candidate;
            }

            if (IsReusableStartupPlaceholderUrl(candidate.Url) || !IsUsableWorkerPageUrl(candidate.Url))
            {
                if (IsUsableAvitoPageUrl(await ReadPageUrlAsync(candidate, cancellationToken).ConfigureAwait(false)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static async Task<IReadOnlyList<IPage>> GetBrowserPagesAsync(
        IBrowser browser,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await browser.PagesAsync()
                .WaitAsync(CdpPageDiscoveryTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"AdsPower CDP: {operation} не получил список вкладок за {CdpPageDiscoveryTimeout.TotalSeconds:0} с.",
                ex);
        }
    }

    private static async Task<string> ReadPageUrlAsync(IPage page, CancellationToken cancellationToken)
    {
        // Page.Url приходит из target metadata и не требует выполнения JavaScript в вкладке.
        // Для исправной вкладки это сразу даёт настоящий URL, в том числе Avito Pro.
        var cdpUrl = page.Url;
        if (!string.IsNullOrWhiteSpace(cdpUrl))
        {
            return cdpUrl;
        }

        try
        {
            var href = await page.EvaluateExpressionAsync<string>("window.location.href")
                .WaitAsync(CdpPageReadTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(href))
            {
                return href;
            }
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"AdsPower CDP: не прочитал адрес вкладки за {CdpPageReadTimeout.TotalSeconds:0} с.",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // нет JS-контекста — берём URL из CDP
        }

        return string.Empty;
    }

    internal static bool ShouldKeepWaitingForStartupNavigation(
        IReadOnlyList<string?> pageUrls,
        TimeSpan elapsed,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(pageUrls);
        if (elapsed >= timeout)
        {
            return false;
        }

        return pageUrls.Count == 0
               || pageUrls.All(static url => !IsUsableWorkerPageUrl(url));
    }

    internal static int SelectExistingAutomationPageIndex(
        IReadOnlyList<string?> pageUrls,
        string preferredUrl)
    {
        ArgumentNullException.ThrowIfNull(pageUrls);
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredUrl);

        var targetKind = ClassifyAutomationPageKind(preferredUrl);
        for (var index = 0; index < pageUrls.Count; index++)
        {
            if (PageMatchesAutomationKind(pageUrls[index], targetKind))
            {
                return index;
            }
        }

        // AdsPower может открыть Avito не на целевом маршруте (например, на последней странице сессии).
        // Такую вкладку навигируем сами — это надёжнее новой CDP-вкладки.
        for (var index = 0; index < pageUrls.Count; index++)
        {
            if (IsUsableAvitoPageUrl(pageUrls[index]))
            {
                return index;
            }
        }

        // Стартовая вкладка AdsPower почти всегда about:blank / «:». Её оставляем и ведём на URL.
        // Новая about:blank через NewPageAsync часто остаётся мёртвой.
        for (var index = 0; index < pageUrls.Count; index++)
        {
            if (IsReusableStartupPlaceholderUrl(pageUrls[index]))
            {
                return index;
            }
        }

        for (var index = 0; index < pageUrls.Count; index++)
        {
            if (IsChromeNewTabUrl(pageUrls[index]))
            {
                return index;
            }
        }

        // Любая уже открытая вкладка лучше новой CDP-about:blank.
        return pageUrls.Count > 0 ? 0 : -1;
    }

    internal static bool ShouldCloseNonWorkerPages(string? workerUrl) =>
        IsUsableWorkerPageUrl(workerUrl);

    internal static bool IsRetryableAdsPowerStartupFailure(Exception ex) =>
        ex is not OperationCanceledException
        and not AdsPowerDailyOpenLimitExceededException
        and not AdsPowerProfileInUseException
        and not AdsPowerRateLimitExceededException
        and not AdsPowerProxyFailureException;

    private static async Task<IBrowser> ConnectAdsPowerBrowserAsync(
        ConnectOptions connectOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Puppeteer
                .ConnectAsync(connectOptions)
                .WaitAsync(CdpConnectTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"AdsPower: CDP-подключение не открылось за {CdpConnectTimeout.TotalSeconds:0} с.",
                ex);
        }
    }

    internal static bool IsReusableStartupPlaceholderUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var t = url.Trim();
        return t.Length <= 1
               || string.Equals(t, "about:blank", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsChromeNewTabUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var t = url.Trim();
        return string.Equals(t, "chrome://new-tab-page", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "chrome://newtab", StringComparison.OrdinalIgnoreCase)
               || string.Equals(t, "chrome://new-tab-page-third-party", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> CloseBrowserPagesAsync(
        IReadOnlyList<IPage> pages,
        string callerMemberName)
    {
        var closed = 0;
        foreach (var page in pages)
        {
            try
            {
                await page.CloseAsync().ConfigureAwait(false);
                closed++;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower CDP: не удалось закрыть лишнюю вкладку ({page.Url}): {ex.Message}",
                    DeskLinkAuditLogLevel.Debug,
                    memberName: callerMemberName,
                    properties: new Dictionary<string, object?>
                    {
                        ["page.url"] = page.Url,
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }

        return closed;
    }

    private static AvitoAutomationPageKind ClassifyAutomationPageKind(string preferredUrl)
    {
        if (AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(preferredUrl))
        {
            return AvitoAutomationPageKind.Candidates;
        }

        if (preferredUrl.Contains("rejected", StringComparison.OrdinalIgnoreCase))
        {
            return AvitoAutomationPageKind.BlockedItems;
        }

        if (preferredUrl.Contains("profile/switch", StringComparison.OrdinalIgnoreCase)
            || preferredUrl.Contains("dashboard#profile", StringComparison.OrdinalIgnoreCase))
        {
            return AvitoAutomationPageKind.ProfileSwitch;
        }

        if (preferredUrl.Contains("/profile/pro/items", StringComparison.OrdinalIgnoreCase))
        {
            return AvitoAutomationPageKind.ActiveItems;
        }

        return AvitoAutomationPageKind.Other;
    }

    private static bool PageMatchesAutomationKind(string? url, AvitoAutomationPageKind kind) => kind switch
    {
        AvitoAutomationPageKind.Candidates =>
            !string.IsNullOrEmpty(url) && AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(url),
        AvitoAutomationPageKind.ActiveItems => IsOnActiveProfileItemsPage(url),
        AvitoAutomationPageKind.BlockedItems => IsOnRejectedTab(url),
        AvitoAutomationPageKind.ProfileSwitch =>
            !string.IsNullOrEmpty(url) &&
            (url.Contains("profile/switch", StringComparison.OrdinalIgnoreCase)
             || url.Contains("dashboard#profile", StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    private static bool IsAvitoProfileAutomationTab(string? url)
    {
        if (!IsUsableWorkerPageUrl(url) || !url!.Contains("avito.ru", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return url.Contains("/profile/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("dashboard#profile", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableAvitoPageUrl(string? url)
    {
        if (!IsUsableWorkerPageUrl(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("avito.ru", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".avito.ru", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// URL, на котором уже можно работать (не стартовый about:blank / chrome://).
    /// Стартовый blank не «мёртвый»: его нужно навигировать, а не открывать новую вкладку.
    /// </summary>
    private static bool IsUsableWorkerPageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var t = url.Trim();
        if (t.Length <= 1
            || string.Equals(t, "about:blank", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("chrome://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("devtools://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsOnActiveProfileItemsPage(string? url)
    {
        if (string.IsNullOrEmpty(url)
            || !url.Contains("/profile/pro/items", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !url.Contains("rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOnCandidatesResponsesPage(string? url) =>
        AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(url);

    private static bool IsOnUrl(string? url, string target)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        return url.StartsWith(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverableNavigationError(Exception ex) =>
        ex is PuppeteerException &&
        (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("frame got detached", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Response body is unavailable for redirect responses", StringComparison.OrdinalIgnoreCase));

    private Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? BuildResolveExistingSourceResponseIdsCallback(
        CandidatesMessengerEnrichmentHints? enrichmentHints)
    {
        if (enrichmentHints is null)
        {
            return null;
        }

        return async (sourceResponseIdCandidates, cancellationToken) =>
            (IReadOnlySet<string>)await duplicateRepository
                .GetExistingSourceResponseIdsAsync(
                    enrichmentHints.AccountId,
                    sourceResponseIdCandidates,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    private Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? BuildResolveExistingCardFingerprintsCallback(
        CandidatesMessengerEnrichmentHints? enrichmentHints)
    {
        if (enrichmentHints is null)
        {
            return null;
        }

        return async (cardFingerprintCandidates, cancellationToken) =>
            (IReadOnlySet<string>)await duplicateRepository
                .GetExistingCardFingerprintsAsync(
                    cardFingerprintCandidates,
                    enrichmentHints.DuplicateScope,
                    enrichmentHints.AccountId,
                    cancellationToken,
                    enrichmentHints.AvitoSubProfileId)
                .ConfigureAwait(false);
    }

    private Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? BuildResolveExistingPhonesCallback(
        CandidatesMessengerEnrichmentHints? enrichmentHints)
    {
        if (enrichmentHints is null)
        {
            return null;
        }

        return async (phoneCandidates, cancellationToken) =>
        {
            var normalizedToRaw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in phoneCandidates)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var normalized = phoneNormalizer.Normalize(raw);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                normalizedToRaw.TryAdd(normalized, raw.Trim());
            }

            if (normalizedToRaw.Count == 0)
            {
                return (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
            }

            var existing = await duplicateRepository
                .GetExistingNormalizedPhonesAsync(
                    normalizedToRaw.Keys,
                    enrichmentHints.DuplicateScope,
                    enrichmentHints.AccountId,
                    cancellationToken,
                    enrichmentHints.AvitoSubProfileId)
                .ConfigureAwait(false);

            var matchedRaw = new HashSet<string>(StringComparer.Ordinal);
            foreach (var normalized in existing)
            {
                if (normalizedToRaw.TryGetValue(normalized, out var raw))
                {
                    matchedRaw.Add(raw);
                }
            }

            return matchedRaw;
        };
    }

    private Func<IReadOnlyList<CandidateLookupProfileDto>, CancellationToken, Task<IReadOnlySet<int>>>? BuildResolveExistingMatchedProfileIndicesCallback(
        CandidatesMessengerEnrichmentHints? enrichmentHints)
    {
        if (enrichmentHints is null)
        {
            return null;
        }

        return async (profiles, cancellationToken) =>
            (IReadOnlySet<int>)await duplicateRepository
                .GetMatchedProfileIndicesAsync(
                    profiles,
                    enrichmentHints.AccountId,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// На странице откликов кнопка «в чат» часто без href; ссылка канала появляется в шапке мини-мессенджера
    /// (<c>mini-messenger/messenger-page-link</c>) только после клика — дополняем JSON для AdsPower CDP.
    /// </summary>
    /// <summary>Номер уже полностью на карточке (не «узнать в чате»): после <see cref="IPhoneNormalizer.Normalize"/> — типичный РФ-мобильный.</summary>
    private static bool LooksLikeCompleteRussianMobile(string normalized) =>
        normalized.Length == 11 && normalized.StartsWith("7", StringComparison.Ordinal);

    private bool CandidateJsonHasCompletePhone(JsonObject item)
    {
        var raw = item["phone"]?.GetValue<string>()
                  ?? item["phoneDigits"]?.GetValue<string>()
                  ?? string.Empty;
        var normalized = phoneNormalizer.Normalize(raw) ?? string.Empty;
        return LooksLikeCompleteRussianMobile(normalized);
    }

    private static int ReadCandidateDomIndex(JsonObject item, int jsonIndex)
    {
        if (item.TryGetPropertyValue("domIndex", out var domIndexNode)
            && domIndexNode is JsonValue domIndexValue
            && domIndexValue.TryGetValue<int>(out var domIndex)
            && domIndex >= 0)
        {
            return domIndex;
        }

        return jsonIndex;
    }

    private async Task<string> TryEnrichCandidatesJsonMessengerUrlsAsync(
        IPage page,
        string rawJson,
        CandidatesMessengerEnrichmentHints? enrichmentHints,
        CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch
        {
            return rawJson;
        }

        if (root is null)
        {
            return rawJson;
        }

        var candidates = root["candidates"]?.AsArray();
        if (candidates is null || candidates.Count == 0)
        {
            return rawJson;
        }

        IReadOnlyDictionary<string, WorkerKnownSourceResponseDto>? existingSourceResponsesFromOrbita = null;
        if (enrichmentHints is not null)
        {
            var sourceIdsToQuery = new List<string>();
            foreach (var node in candidates)
            {
                var o = node?.AsObject();
                if (o is null)
                {
                    continue;
                }

                var sourceResponseId = o["sourceResponseId"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(sourceResponseId))
                {
                    sourceIdsToQuery.Add(sourceResponseId.Trim());
                }

                var fullNameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(
                    o["fullName"]?.GetValue<string>());
                if (!string.IsNullOrWhiteSpace(fullNameKey))
                {
                    sourceIdsToQuery.Add(ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(
                        enrichmentHints.AvitoSubProfileId,
                        fullNameKey));
                }
            }

            if (sourceIdsToQuery.Count > 0)
            {
                var knownResponses = await duplicateRepository
                    .GetExistingSourceResponsesAsync(
                        enrichmentHints.AccountId,
                        sourceIdsToQuery,
                        cancellationToken)
                    .ConfigureAwait(false);
                existingSourceResponsesFromOrbita = knownResponses
                    .GroupBy(static x => x.SourceResponseId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        static group => group.Key,
                        static group => group.OrderByDescending(x => x.CollectedAt).First(),
                        StringComparer.OrdinalIgnoreCase);
            }
        }

        await EnsureMessengerEnrichmentViewportAsync(page, cancellationToken)
            .ConfigureAwait(false);

        _ = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildDismissCandidateDetailPanelScript(),
                cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        var candidatesReturnUrl = AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(page.Url)
            ? page.Url
            : CandidatesPageUrl;

        var isJobCrm = await TryDetectJobCrmResponsesPageAsync(page, cancellationToken).ConfigureAwait(false);
        var autoReplyBudget = AvitoHumanVariation.NextAutoReplyBudget();
        var autoRepliesSent = 0;

        const int maxEnrich = 80;
        var candidateIndexes = Enumerable.Range(0, candidates.Count)
            .OrderByDescending(index =>
            {
                var item = candidates[index]?.AsObject();
                var fullNameKey = ResponsePhoneWatchEvaluator.BuildFullNameKey(
                    item?["fullName"]?.GetValue<string>());
                if (string.IsNullOrWhiteSpace(fullNameKey)
                    || existingSourceResponsesFromOrbita is null)
                {
                    return DateTime.MinValue;
                }

                var sourceId = ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(
                    enrichmentHints?.AvitoSubProfileId,
                    fullNameKey);
                return existingSourceResponsesFromOrbita.TryGetValue(sourceId, out var stored)
                    ? stored.CollectedAt
                    : DateTime.MinValue;
            })
            .ThenBy(static index => index)
            .Take(maxEnrich);
        foreach (var i in candidateIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = candidates[i]?.AsObject();
            if (item is null)
            {
                continue;
            }

            var domIndex = ReadCandidateDomIndex(item, i);
            var fullNameForWatch = item["fullName"]?.GetValue<string>() ?? string.Empty;
            var fullNameKeyForWatch = ResponsePhoneWatchEvaluator.BuildFullNameKey(fullNameForWatch);
            var phoneWatchSourceId = string.IsNullOrWhiteSpace(fullNameKeyForWatch)
                ? string.Empty
                : ResponsePhoneWatchEvaluator.BuildPublishedSourceResponseId(
                    enrichmentHints?.AvitoSubProfileId,
                    fullNameKeyForWatch);
            WorkerKnownSourceResponseDto? storedPhoneWatch = null;
            if (existingSourceResponsesFromOrbita is not null)
            {
                existingSourceResponsesFromOrbita.TryGetValue(phoneWatchSourceId, out storedPhoneWatch);
            }
            if (storedPhoneWatch is not null)
            {
                item["sourceResponseId"] = storedPhoneWatch.SourceResponseId;
            }

            var sourceResponseIdForSkip = item["sourceResponseId"]?.GetValue<string>() ?? string.Empty;
            var isKnownSourceId = existingSourceResponsesFromOrbita is not null
                && !string.IsNullOrWhiteSpace(sourceResponseIdForSkip)
                && existingSourceResponsesFromOrbita.ContainsKey(sourceResponseIdForSkip.Trim());
            var pendingForCandidate = ResolvePendingForCandidate(
                enrichmentHints?.PendingBySourceResponseId,
                sourceResponseIdForSkip);
            var hasCompletePhone = CandidateJsonHasCompletePhone(item);
            var hasPendingOutbound = pendingForCandidate.Count > 0;
            var restoredPhoneWatch = ResponsePhoneWatchOrbitaState.RestoreObservation(
                storedPhoneWatch,
                enrichmentHints?.AvitoSubProfileId,
                fullNameKeyForWatch,
                enrichmentHints?.PhoneWatchHours ?? ResponsePhoneWatchRules.DefaultUnchangedHours,
                DateTime.UtcNow);
            var openPhoneWatch = ResponsePhoneObservationWatch.IsOpen(restoredPhoneWatch);
            if (!hasPendingOutbound
                && !openPhoneWatch
                && enrichmentHints?.IsOpenPhoneWatchAsync is not null
                && !string.IsNullOrWhiteSpace(fullNameForWatch)
                && (isKnownSourceId || !hasCompletePhone))
            {
                openPhoneWatch = await enrichmentHints
                    .IsOpenPhoneWatchAsync(fullNameForWatch, cancellationToken)
                    .ConfigureAwait(false);
            }

            var hasUnread = false;
            if (isKnownSourceId && !hasPendingOutbound && !openPhoneWatch)
            {
                hasUnread = await TryReadCandidateChatUnreadAsync(page, domIndex, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (MessengerEnrichmentSkip.ShouldSkipOpeningCard(
                    isKnownSourceId,
                    hasUnread,
                    openPhoneWatch,
                    hasPendingOutbound,
                    hasCompletePhone))
            {
                continue;
            }

            if (!isJobCrm && CandidateJsonNeedsDetail(item))
            {
                await TryApplyDetailPanelToCandidateAsync(page, item, domIndex, cancellationToken)
                    .ConfigureAwait(false);
            }

            var enrichment = await TryEnrichMessengerForCandidateCardAsync(
                    page,
                    domIndex,
                    candidatesReturnUrl,
                    enrichmentHints?.MessengerAutoReply,
                    pendingForCandidate,
                    enrichmentHints?.ClaimOutboundChatForDeliveryAsync,
                    enrichmentHints?.AckOutboundChatSentAsync,
                    candidateAlreadyKnown: isKnownSourceId,
                    autoRepliesRemaining: autoReplyBudget - autoRepliesSent,
                    cancellationToken)
                .ConfigureAwait(false);
            if (enrichment.AutoReplySent)
            {
                autoRepliesSent++;
            }
            if (!string.IsNullOrWhiteSpace(enrichment.ChannelUrl) || enrichment.ChatMessages.Count > 0)
            {
                await HumanDelay.AfterMessengerCardAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);
            }

            if (enrichment.ChatMessages.Count == 0)
            {
                var collection = enrichment.Collection ?? MiniMessengerCollectionResult.NotCollected;
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower messenger enrich: no chat messages for candidate index {domIndex}; " +
                    $"click={enrichment.ClickMethod}; uiConfirmed={enrichment.UiConfirmed}; " +
                    $"channelUrlFound={!string.IsNullOrWhiteSpace(enrichment.ChannelUrl)}; " +
                    $"waitConfirmed={collection.WaitConfirmed}; attempts={collection.Attempts}; " +
                    $"reason={collection.Reason}; miniLink={collection.HasMiniLink}; miniRoot={collection.HasMiniRoot}; " +
                    $"markedHistoryFallback={collection.UsedMarkedHistoryFallback}; channelPage={collection.IsChannelPage}; " +
                    $"histories={collection.HistoryCount}; visibleHistories={collection.VisibleHistoryCount}; " +
                    $"messageNodes={collection.RootMessageNodeCount}; messagesList={collection.HasMessagesList}.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(TryEnrichCandidatesJsonMessengerUrlsAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["candidate.index"] = i,
                        ["candidate.domIndex"] = domIndex,
                        ["page.url"] = page.Url,
                        ["page.innerWidth"] = await TryReadInnerWidthAsync(page).ConfigureAwait(false),
                        ["page.innerHeight"] = await TryReadInnerHeightAsync(page).ConfigureAwait(false),
                        ["messenger.clickMethod"] = enrichment.ClickMethod,
                        ["messenger.uiConfirmed"] = enrichment.UiConfirmed,
                        ["messenger.channelUrlFound"] = !string.IsNullOrWhiteSpace(enrichment.ChannelUrl),
                        ["messenger.collect.waitConfirmed"] = collection.WaitConfirmed,
                        ["messenger.collect.attempts"] = collection.Attempts,
                        ["messenger.collect.reason"] = collection.Reason,
                        ["messenger.collect.hasMiniLink"] = collection.HasMiniLink,
                        ["messenger.collect.hasMiniRoot"] = collection.HasMiniRoot,
                        ["messenger.collect.usedMarkedHistoryFallback"] = collection.UsedMarkedHistoryFallback,
                        ["messenger.collect.isChannelPage"] = collection.IsChannelPage,
                        ["messenger.collect.historyCount"] = collection.HistoryCount,
                        ["messenger.collect.visibleHistoryCount"] = collection.VisibleHistoryCount,
                        ["messenger.collect.rootMessageNodeCount"] = collection.RootMessageNodeCount,
                        ["messenger.collect.hasMessagesList"] = collection.HasMessagesList
                    });
            }

            if (string.IsNullOrWhiteSpace(enrichment.ChannelUrl) && enrichment.ChatMessages.Count == 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(enrichment.ChannelUrl))
            {
                item["messengerUrl"] = enrichment.ChannelUrl;
            }

            if (enrichment.ChatMessages.Count > 0)
            {
                item["chatMessages"] = enrichment.ChatMessages;
            }
        }

        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);

        return root.ToJsonString();
    }

    private static async Task CloseMiniMessengerPanelIfOpenAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var hasPanel = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                    page,
                    "(!!document.querySelector(\"a[data-marker='mini-messenger/messenger-page-link']\"))")
                .ConfigureAwait(false);
            if (!hasPanel)
            {
                return;
            }

            await page.EvaluateExpressionAsync(@"(() => {
                const link = document.querySelector(""a[data-marker='mini-messenger/messenger-page-link']"");
                if (!link) {
                    return;
                }
                const mini = link.closest('[class*=""channel-module-root""]');
                const back = mini?.querySelector('[data-marker=""navigation/back""]');
                if (back) {
                    const rect = back.getBoundingClientRect();
                    const x = rect.left + Math.max(rect.width, 1) * 0.5;
                    const y = rect.top + Math.max(rect.height, 1) * 0.5;
                    const base = { bubbles: true, cancelable: true, view: window, clientX: x, clientY: y, button: 0 };
                    back.dispatchEvent(new PointerEvent('pointerdown', Object.assign({ pointerType: 'mouse', isPrimary: true, pointerId: 1 }, base)));
                    back.dispatchEvent(new MouseEvent('mousedown', base));
                    back.dispatchEvent(new PointerEvent('pointerup', Object.assign({ pointerType: 'mouse', isPrimary: true, pointerId: 1 }, base)));
                    back.dispatchEvent(new MouseEvent('mouseup', base));
                    back.dispatchEvent(new MouseEvent('click', base));
                }
            })()").ConfigureAwait(false);

            await page.WaitForFunctionAsync(
                    "() => !document.querySelector(\"a[data-marker='mini-messenger/messenger-page-link']\")",
                    new WaitForFunctionOptions { Timeout = 6000 })
                .ConfigureAwait(false);
        }
        catch
        {
            // DOM мог измениться; не прерываем выдачу списка кандидатов.
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
    }

    private sealed record MessengerCardEnrichmentResult(
        string? ChannelUrl,
        JsonArray ChatMessages,
        bool UiConfirmed = false,
        bool AutoReplySent = false,
        string ClickMethod = "not_clicked",
        MiniMessengerCollectionResult? Collection = null);

    private sealed record MiniMessengerCollectionResult(
        JsonArray Messages,
        string Reason,
        bool WaitConfirmed,
        int Attempts,
        bool HasMiniLink,
        bool HasMiniRoot,
        bool UsedMarkedHistoryFallback,
        bool IsChannelPage,
        int HistoryCount,
        int VisibleHistoryCount,
        int RootMessageNodeCount,
        bool HasMessagesList)
    {
        public static MiniMessengerCollectionResult NotCollected { get; } = new(
            new JsonArray(),
            "not_collected",
            WaitConfirmed: false,
            Attempts: 0,
            HasMiniLink: false,
            HasMiniRoot: false,
            UsedMarkedHistoryFallback: false,
            IsChannelPage: false,
            HistoryCount: 0,
            VisibleHistoryCount: 0,
            RootMessageNodeCount: 0,
            HasMessagesList: false);
    }

    private static async Task<bool> TryReadCandidateChatUnreadAsync(
        IPage page,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        var raw = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildReadCandidateChatUnreadScript(candidateIndex),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            return doc.RootElement.TryGetProperty("unread", out var unreadProp)
                && unreadProp.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<WorkerPendingChatMessageDto> ResolvePendingForCandidate(
        IReadOnlyDictionary<string, IReadOnlyList<WorkerPendingChatMessageDto>>? pendingBySource,
        string? sourceResponseId)
    {
        if (pendingBySource is null || string.IsNullOrWhiteSpace(sourceResponseId))
        {
            return [];
        }

        return pendingBySource.TryGetValue(sourceResponseId.Trim(), out var pending)
            ? pending
            : [];
    }

    private async Task<MessengerCardEnrichmentResult> TryEnrichMessengerForCandidateCardAsync(
        IPage page,
        int candidateIndex,
        string candidatesReturnUrl,
        AvitoMessengerAutoReplySettings? autoReply,
        IReadOnlyList<WorkerPendingChatMessageDto> pendingOutbound,
        Func<Guid, CancellationToken, Task<bool>>? claimOutboundChatForDeliveryAsync,
        Func<IReadOnlyList<Guid>, CancellationToken, Task>? ackOutboundChatSentAsync,
        bool candidateAlreadyKnown,
        int autoRepliesRemaining,
        CancellationToken cancellationToken)
    {
        var autoReplySent = false;
        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);
        try
        {
            await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildMarkMessengerRootsBeforeOpenScript(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Без метки сбор всё ещё умеет работать с полноэкранным каналом, но не должен ломать проход.
        }

        await HumanDelay.BeforeCandidateClickAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await page.BringToFrontAsync().ConfigureAwait(false);
        }
        catch
        {
            // Вкладка AdsPower могла уже быть спереди — клик всё равно пробуем.
        }

        var clickMethod = "failed";
        var clickedViaPointer = await TryClickCandidateChatWithPointerAsync(page, candidateIndex, cancellationToken)
            .ConfigureAwait(false);
        string? clickReason = null;
        var clicked = clickedViaPointer;
        if (clicked)
        {
            clickMethod = "pointer";
        }
        else
        {
            var domClick = await TryClickCandidateChatByDomAsync(page, candidateIndex, cancellationToken)
                .ConfigureAwait(false);
            clicked = domClick.Ok;
            clickReason = domClick.Reason;
            if (clicked)
            {
                clickMethod = "dom";
            }
        }

        if (!clicked)
        {
            if (!string.IsNullOrWhiteSpace(clickReason))
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower messenger enrich: chat click failed for candidate index {candidateIndex}: {clickReason}.",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(TryEnrichMessengerForCandidateCardAsync),
                    properties: new Dictionary<string, object?>
                    {
                        ["candidate.index"] = candidateIndex,
                        ["page.url"] = page.Url,
                        ["page.innerWidth"] = await TryReadInnerWidthAsync(page).ConfigureAwait(false),
                        ["messenger.clickReason"] = clickReason
                    });
            }

            return new MessengerCardEnrichmentResult(
                null,
                new JsonArray(),
                UiConfirmed: false,
                AutoReplySent: false,
                ClickMethod: "failed",
                Collection: MiniMessengerCollectionResult.NotCollected);
        }

        await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);

        var messengerUiConfirmed = await TryWaitForMessengerUiAsync(page, 12_000).ConfigureAwait(false);

        // Pointer/CDP mouse returns true as soon as the event is dispatched. Overlays, wander
        // tooltips and AdsPower hit-testing can swallow it — the existing DOM fallback never
        // ran on this path, so the mini-chat stayed closed (click=pointer, uiConfirmed=false,
        // reason=no_active_candidate_messenger).
        if (!messengerUiConfirmed)
        {
            try
            {
                await EvaluateWithRetryAsync<string>(
                        page,
                        AvitoCandidatesPageScripts.BuildDismissCandidateDetailPanelScript(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: popup/panel over the chat button.
            }

            var retry = await TryClickCandidateChatByDomAsync(page, candidateIndex, cancellationToken)
                .ConfigureAwait(false);
            if (retry.Ok)
            {
                clickMethod = clickedViaPointer ? "pointer_then_dom" : "dom_retry";
                await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);
                messengerUiConfirmed = await TryWaitForMessengerUiAsync(page, 8_000).ConfigureAwait(false);
            }
        }

        string? channelUrl = null;
        try
        {
            channelUrl = await page.EvaluateExpressionAsync<string>(
                    AvitoCandidatesPageScripts.BuildResolveMessengerChannelUrlExpression())
                .ConfigureAwait(false);
            channelUrl = string.IsNullOrWhiteSpace(channelUrl) ? null : channelUrl.Trim();
        }
        catch
        {
            // Ссылка канала может появиться позже, чем список сообщений.
        }

        var collection = await CollectMiniMessengerMessagesAsync(page, cancellationToken).ConfigureAwait(false);
        var chatMessages = collection.Messages;
        autoReply ??= new AvitoMessengerAutoReplySettings();
        var parsedChat = ParseMiniMessengerMessages(chatMessages);
        if (pendingOutbound.Count > 0)
        {
            var decision = AvitoPendingChatSendEvaluator.Evaluate(parsedChat, pendingOutbound);
            var acked = new List<Guid>();
            foreach (var item in decision.AlreadyInChat)
            {
                if (claimOutboundChatForDeliveryAsync is not null
                    && await claimOutboundChatForDeliveryAsync(item.Id, cancellationToken).ConfigureAwait(false))
                {
                    acked.Add(item.Id);
                }
            }

            foreach (var item in decision.ToSend)
            {
                if (claimOutboundChatForDeliveryAsync is null
                    || !await claimOutboundChatForDeliveryAsync(item.Id, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (await TrySendMiniMessengerTextAsync(page, item.Text, "manager-outbound", cancellationToken)
                        .ConfigureAwait(false))
                {
                    acked.Add(item.Id);
                }
                else
                {
                    break;
                }
            }

            if (acked.Count > 0 && ackOutboundChatSentAsync is not null)
            {
                try
                {
                    await ackOutboundChatSentAsync(acked, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower outbound chat ack failed: {ex.Message}.",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(TryEnrichMessengerForCandidateCardAsync));
                }
            }

            if (acked.Count > 0)
            {
                collection = await CollectMiniMessengerMessagesAsync(page, cancellationToken).ConfigureAwait(false);
                chatMessages = collection.Messages;
            }
        }
        else if (AvitoChatAutoReplyEvaluator.ShouldSendOnThisPass(
                     autoReply.Enabled,
                     candidateAlreadyKnown,
                     sentThisPass: 0,
                     maxPerPass: Math.Max(0, autoRepliesRemaining),
                     parsedChat,
                     autoReply.Message))
        {
            if (await TrySendMiniMessengerTextAsync(page, autoReply.Message, "auto-reply", cancellationToken)
                    .ConfigureAwait(false))
            {
                autoReplySent = true;
                collection = await CollectMiniMessengerMessagesAsync(page, cancellationToken).ConfigureAwait(false);
                chatMessages = collection.Messages;
            }
        }
        else if (autoReply.Enabled
                 && AvitoChatAutoReplyEvaluator.NeedsAutoReply(parsedChat, autoReply.Message))
        {
            _ = GlobalLogger.Instance.LogAsync(
                candidateAlreadyKnown
                    ? "AdsPower messenger auto-reply skipped: pass budget exhausted."
                    : "AdsPower messenger auto-reply deferred: first sight this pass.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(TryEnrichMessengerForCandidateCardAsync),
                properties: new Dictionary<string, object?>
                {
                    ["messenger.autoReply.deferred"] = true,
                    ["messenger.autoReply.knownCandidate"] = candidateAlreadyKnown,
                    ["messenger.autoReply.remaining"] = autoRepliesRemaining
                });
        }

        if (string.IsNullOrWhiteSpace(channelUrl))
        {
            try
            {
                channelUrl = await page.EvaluateExpressionAsync<string>(
                        AvitoCandidatesPageScripts.BuildResolveMessengerChannelUrlExpression())
                    .ConfigureAwait(false);
                channelUrl = string.IsNullOrWhiteSpace(channelUrl) ? null : channelUrl.Trim();
            }
            catch
            {
                // ignore
            }
        }

        if (!AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(page.Url))
        {
            await ReturnToCandidatesPageAfterMessengerAsync(page, candidatesReturnUrl, cancellationToken)
                .ConfigureAwait(false);
        }

        return new MessengerCardEnrichmentResult(
            channelUrl,
            chatMessages,
            UiConfirmed: messengerUiConfirmed || !string.IsNullOrWhiteSpace(channelUrl) || chatMessages.Count > 0,
            AutoReplySent: autoReplySent,
            ClickMethod: clickMethod,
            Collection: collection);
    }

    /// <summary>
    /// На узком окне AdsPower мини-чат не рендерится — один раз расширяем окно и viewport перед enrichment.
    /// </summary>
    private static async Task EnsureMessengerEnrichmentViewportAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var width = await TryReadInnerWidthAsync(page).ConfigureAwait(false);
        var height = await TryReadInnerHeightAsync(page).ConfigureAwait(false);
        if (width >= MessengerEnrichmentViewportWidth && height >= MessengerEnrichmentViewportHeight)
        {
            return;
        }

        await TryResizeBrowserWindowAsync(
                page,
                MessengerEnrichmentViewportWidth,
                MessengerEnrichmentViewportHeight)
            .ConfigureAwait(false);

        await page.SetViewportAsync(new ViewPortOptions
        {
            Width = MessengerEnrichmentViewportWidth,
            Height = MessengerEnrichmentViewportHeight
        }).ConfigureAwait(false);
        try
        {
            await page.EvaluateExpressionAsync("window.dispatchEvent(new Event('resize'))").ConfigureAwait(false);
        }
        catch
        {
            // Не прерываем enrichment — layout может обновиться и без явного resize.
        }

        await Task.Delay(MessengerEnrichmentViewportResizeDelayMs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryResizeBrowserWindowAsync(IPage page, int width, int height)
    {
        try
        {
            var windowIdRaw = await page.WindowIdAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(windowIdRaw) || !int.TryParse(windowIdRaw, out var windowId))
            {
                return;
            }

            await page.Client.SendAsync("Browser.setWindowBounds", new
            {
                windowId,
                bounds = new
                {
                    width,
                    height,
                    windowState = "normal"
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            // CDP resize is best-effort; SetViewportAsync still applies below.
        }
    }

    private static async Task ReturnToCandidatesPageAfterMessengerAsync(
        IPage page,
        string candidatesReturnUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            await page.GoBackAsync().ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(page.Url))
            {
                return;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            await page.GoToAsync(
                    candidatesReturnUrl,
                    new NavigationOptions
                    {
                        Timeout = 30_000,
                        WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                    })
                .ConfigureAwait(false);
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // DOM мог измениться; следующая карточка попробует закрыть мини-чат / кликнуть снова.
        }
    }

    private static async Task<int> TryReadInnerWidthAsync(IPage page)
    {
        try
        {
            return await page.EvaluateExpressionAsync<int>("window.innerWidth || 0").ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<int> TryReadInnerHeightAsync(IPage page)
    {
        try
        {
            return await page.EvaluateExpressionAsync<int>("window.innerHeight || 0").ConfigureAwait(false);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<MiniMessengerCollectionResult> CollectMiniMessengerMessagesAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var waitConfirmed = false;
        try
        {
            await page.WaitForFunctionAsync(
                    AvitoCandidatesPageScripts.BuildMessengerMessagesPresentExpression(),
                    new WaitForFunctionOptions { Timeout = 8_000, PollingInterval = 250 })
                .ConfigureAwait(false);
            waitConfirmed = true;
        }
        catch
        {
            // Оболочка мини-чата может появиться раньше сообщений; ниже ещё несколько опросов.
        }

        MiniMessengerCollectionResult? richest = null;
        var latest = MiniMessengerCollectionResult.NotCollected with { WaitConfirmed = waitConfirmed };
        for (var round = 0; round < 6; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await HumanDelay.DelayAsync(round == 0 ? 400 : 280, round == 0 ? 900 : 650, cancellationToken)
                .ConfigureAwait(false);

            var messagesRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildScrollAndCollectMiniMessengerMessagesScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = TryParseMiniMessengerCollectionResult(messagesRaw) with
            {
                WaitConfirmed = waitConfirmed,
                Attempts = round + 1
            };
            latest = parsed;
            if (parsed.Messages.Count == 0)
            {
                continue;
            }

            if (richest is null || parsed.Messages.Count > richest.Messages.Count)
            {
                richest = parsed;
                continue;
            }

            return richest;
        }

        return richest ?? latest;
    }

    private static IReadOnlyList<AvitoChatMessage> ParseMiniMessengerMessages(JsonArray chatMessages) =>
        AvitoChatMessagesJson.Parse(chatMessages.ToJsonString());

    private static bool CandidateJsonNeedsDetail(JsonObject item)
    {
        var vacancyUrl = item["vacancyUrl"]?.GetValue<string>();
        var age = item["age"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(vacancyUrl) || string.IsNullOrWhiteSpace(age);
    }

    private static async Task<bool> TryDetectJobCrmResponsesPageAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildIsJobCrmResponsesPageScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            return doc.RootElement.TryGetProperty("isJobCrm", out var prop) && prop.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private async Task TryApplyDetailPanelToCandidateAsync(
        IPage page,
        JsonObject item,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        await HumanDelay.BeforeCandidateClickAsync(cancellationToken).ConfigureAwait(false);
        var clicked = await TryClickCandidateItemWithPointerAsync(page, candidateIndex, cancellationToken)
            .ConfigureAwait(false);
        if (!clicked)
        {
            var clickRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildClickCandidateItemByIndexScript(candidateIndex),
                    cancellationToken)
                .ConfigureAwait(false);
            clicked = TryParseMessengerChatClickStep(clickRaw, out var ok, out _) && ok;
        }

        if (!clicked)
        {
            return;
        }

        await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var detailRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildReadDetailPanelScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            ApplyDetailPanelJson(item, detailRaw);
        }
        catch
        {
            // Панель могла не открыться — чат всё равно пробуем.
        }

        _ = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildDismissCandidateDetailPanelScript(),
                cancellationToken)
            .ConfigureAwait(false);
        await HumanDelay.AfterDetailPanelReadAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyDetailPanelJson(JsonObject item, string? detailRaw)
    {
        if (string.IsNullOrWhiteSpace(detailRaw))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(detailRaw));
            var root = doc.RootElement;
            CopyIfMissing(item, root, "vacancyUrl");
            CopyIfMissing(item, root, "vacancy");
            CopyIfMissing(item, root, "city");
            CopyIfMissing(item, root, "age");
        }
        catch
        {
            // ignore malformed panel snapshot
        }
    }

    private static void CopyIfMissing(JsonObject item, JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var current = item[property]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(current))
        {
            item[property] = text;
        }
    }

    private static async Task<bool> TryClickCandidateItemWithPointerAsync(
        IPage page,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await page.QuerySelectorAllAsync("[data-marker='job-application/item']").ConfigureAwait(false);
            if (items is null || candidateIndex < 0 || candidateIndex >= items.Length)
            {
                return false;
            }

            return await AvitoHumanPointer.TryClickHandleAsync(page, items[candidateIndex], cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryClickCandidateChatWithPointerAsync(
        IPage page,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await page.QuerySelectorAllAsync("[data-marker='job-application/item']").ConfigureAwait(false);
            if (items is null || candidateIndex < 0 || candidateIndex >= items.Length)
            {
                return false;
            }

            var chat = await items[candidateIndex]
                .QuerySelectorAsync("[data-marker='job-application/link/to-chat']")
                .ConfigureAwait(false);
            if (chat is null)
            {
                return false;
            }

            var inner = await chat.QuerySelectorAsync("button, a, [role='button']").ConfigureAwait(false);
            return await AvitoHumanPointer.TryClickHandleAsync(page, inner ?? chat, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryWaitForMessengerUiAsync(IPage page, int timeoutMs)
    {
        try
        {
            await page.WaitForFunctionAsync(
                    AvitoCandidatesPageScripts.BuildMessengerUiVisibleExpression(),
                    new WaitForFunctionOptions { Timeout = timeoutMs, PollingInterval = 250 })
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(bool Ok, string? Reason)> TryClickCandidateChatByDomAsync(
        IPage page,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        var clickRaw = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildClickCandidateChatByIndexScript(candidateIndex),
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryParseMessengerChatClickStep(clickRaw, out var clicked, out var reason) || !clicked)
        {
            return (false, reason);
        }

        return (true, null);
    }

    private static readonly string[] MiniMessengerSendSelectors =
    [
        "[data-marker='reply/send']",
        "[data-marker='reply/submit']",
        "[data-marker='reply/sendButton']",
        "form[data-marker='reply'] button[type='submit']"
    ];

    private static async Task<bool> TrySendMiniMessengerTextWithPointerAsync(
        IPage page,
        string messageText,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = await page.QuerySelectorAsync("[data-marker='reply/input']").ConfigureAwait(false);
            if (input is null)
            {
                return false;
            }

            if (!await AvitoHumanPointer.TryTypeIntoHandleAsync(page, input, messageText, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }

            await HumanDelay.BeforeMessengerAutoReplySendAsync(cancellationToken).ConfigureAwait(false);

            foreach (var selector in MiniMessengerSendSelectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await AvitoHumanPointer.TryClickSelectorAsync(page, selector, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }

            await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySendMiniMessengerTextAsync(
        IPage page,
        string messageText,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (await TrySendMiniMessengerTextWithPointerAsync(page, messageText, cancellationToken).ConfigureAwait(false))
        {
            var appearedViaPointer = await WaitForEmployerAutoReplyInChatAsync(page, messageText, cancellationToken)
                .ConfigureAwait(false);
            _ = GlobalLogger.Instance.LogAsync(
                appearedViaPointer
                    ? $"AdsPower messenger {purpose} sent."
                    : $"AdsPower messenger {purpose} submitted, but outgoing message was not confirmed in chat history.",
                appearedViaPointer ? DeskLinkAuditLogLevel.Info : DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TrySendMiniMessengerTextAsync),
                properties: new Dictionary<string, object?>
                {
                    ["page.url"] = page.Url,
                    ["messenger.send.purpose"] = purpose,
                    ["messenger.send.confirmed"] = appearedViaPointer,
                    ["messenger.send.method"] = "pointer-type"
                });
            return appearedViaPointer;
        }

        await HumanDelay.BeforeMessengerAutoReplySendAsync(cancellationToken).ConfigureAwait(false);

        var sendRaw = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildSendMiniMessengerReplyScript(messageText),
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryParseMessengerSendStep(sendRaw, out var sent, out var reason) || !sent)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower messenger {purpose} failed: {reason ?? "unknown"}.",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TrySendMiniMessengerTextAsync),
                properties: new Dictionary<string, object?>
                {
                    ["page.url"] = page.Url,
                    ["messenger.send.purpose"] = purpose,
                    ["messenger.send.reason"] = reason
                });
            return false;
        }

        var appeared = await WaitForEmployerAutoReplyInChatAsync(page, messageText, cancellationToken).ConfigureAwait(false);
        _ = GlobalLogger.Instance.LogAsync(
            appeared
                ? $"AdsPower messenger {purpose} sent."
                : $"AdsPower messenger {purpose} submitted, but outgoing message was not confirmed in chat history.",
            appeared ? DeskLinkAuditLogLevel.Info : DeskLinkAuditLogLevel.Warning,
            memberName: nameof(TrySendMiniMessengerTextAsync),
            properties: new Dictionary<string, object?>
            {
                ["page.url"] = page.Url,
                ["messenger.send.purpose"] = purpose,
                ["messenger.send.confirmed"] = appeared,
                ["messenger.send.method"] = reason
            });

        return appeared;
    }

    private async Task<bool> WaitForEmployerAutoReplyInChatAsync(
        IPage page,
        string autoReplyMessage,
        CancellationToken cancellationToken)
    {
        var expected = autoReplyMessage.Trim();
        for (var elapsed = 0;
             elapsed < MonitoringTiming.MessengerAutoReplyPostSendMaxWaitMs;
             elapsed += MonitoringTiming.MessengerAutoReplyPostSendPollMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(MonitoringTiming.MessengerAutoReplyPostSendPollMs, cancellationToken)
                .ConfigureAwait(false);

            var messagesRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildScrollAndCollectMiniMessengerMessagesScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            var messages = ParseMiniMessengerMessages(TryParseMiniMessengerMessages(messagesRaw));
            if (messages.Any(m =>
                    AvitoChatAutoReplyEvaluator.IsEmployerMessage(m)
                    && string.Equals(m.Text.Trim(), expected, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseMessengerSendStep(string? raw, out bool ok, out string? reason)
    {
        ok = false;
        reason = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "empty_send_result";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok && doc.RootElement.TryGetProperty("reason", out var reasonProp))
            {
                reason = reasonProp.GetString();
            }
            else if (ok && doc.RootElement.TryGetProperty("method", out var methodProp))
            {
                reason = methodProp.GetString();
            }

            return true;
        }
        catch
        {
            reason = "invalid_send_result";
            return false;
        }
    }

    private static MiniMessengerCollectionResult TryParseMiniMessengerCollectionResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return MiniMessengerCollectionResult.NotCollected with { Reason = "empty_script_result" };
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            if (!doc.RootElement.TryGetProperty("messages", out var messagesElement)
                || messagesElement.ValueKind != JsonValueKind.Array)
            {
                return MiniMessengerCollectionResult.NotCollected with { Reason = "invalid_messages_result" };
            }

            var result = new JsonArray();
            foreach (var message in messagesElement.EnumerateArray())
            {
                var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var node = new JsonObject
                {
                    ["text"] = text.Trim()
                };
                if (message.TryGetProperty("at", out var atProp) && atProp.ValueKind == JsonValueKind.String)
                {
                    node["at"] = atProp.GetString();
                }

                if (message.TryGetProperty("side", out var sideProp) && sideProp.ValueKind == JsonValueKind.String)
                {
                    node["side"] = sideProp.GetString();
                }

                if (message.TryGetProperty("isPlatform", out var platformProp)
                    && platformProp.ValueKind == JsonValueKind.True)
                {
                    node["isPlatform"] = true;
                }

                result.Add(node);
            }

            var root = doc.RootElement;
            var diagnostics = root.TryGetProperty("diagnostics", out var diagnosticsElement)
                && diagnosticsElement.ValueKind == JsonValueKind.Object
                ? diagnosticsElement
                : default;
            return new MiniMessengerCollectionResult(
                result,
                ReadJsonString(root, "reason") ?? (result.Count == 0 ? "no_message_text" : "collected"),
                WaitConfirmed: false,
                Attempts: 0,
                HasMiniLink: ReadJsonBool(diagnostics, "hasMiniLink"),
                HasMiniRoot: ReadJsonBool(diagnostics, "hasMiniRoot"),
                UsedMarkedHistoryFallback: ReadJsonBool(diagnostics, "usedMarkedHistoryFallback"),
                IsChannelPage: ReadJsonBool(diagnostics, "isChannelPage"),
                HistoryCount: ReadJsonInt(diagnostics, "historyCount"),
                VisibleHistoryCount: ReadJsonInt(diagnostics, "visibleHistoryCount"),
                RootMessageNodeCount: ReadJsonInt(diagnostics, "rootMessageNodeCount"),
                HasMessagesList: ReadJsonBool(diagnostics, "hasMessagesList"));
        }
        catch
        {
            return MiniMessengerCollectionResult.NotCollected with { Reason = "invalid_script_json" };
        }
    }

    private static JsonArray TryParseMiniMessengerMessages(string? raw) =>
        TryParseMiniMessengerCollectionResult(raw).Messages;

    private static string? ReadJsonString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadJsonBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static int ReadJsonInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static string UnwrapMessengerJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed) ?? trimmed;
            }
            catch
            {
                return trimmed;
            }
        }

        return trimmed;
    }

    private static bool TryParseMessengerChatClickStep(string? raw, out bool ok, out string? reason)
    {
        ok = false;
        reason = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "empty_click_result";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
            if (!ok && doc.RootElement.TryGetProperty("reason", out var reasonProp))
            {
                reason = reasonProp.GetString();
            }

            return true;
        }
        catch
        {
            reason = "invalid_click_result";
            return false;
        }
    }

    private static Task<T> EvaluateWithRetryAsync<T>(
        IPage page,
        string expression,
        CancellationToken cancellationToken) =>
        EvaluateWithRetryAsync<T>(page, expression, cancellationToken, timeout: null);

    private static async Task<T> EvaluateWithRetryAsync<T>(
        IPage page,
        string expression,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        try
        {
            return await EvaluateAsync<T>(page, expression, cancellationToken, timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            return await EvaluateAsync<T>(page, expression, cancellationToken, timeout).ConfigureAwait(false);
        }
    }

    private static async Task<T> EvaluateAsync<T>(
        IPage page,
        string expression,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var evaluation = page.EvaluateExpressionAsync<T>(expression);
        return await AdsPowerCdpGuard.WaitAsync(
                evaluation,
                timeout ?? CdpEvaluateHangTimeout,
                "JavaScript-проверка страницы",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly string ExtractionScript =
        AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer();
}
