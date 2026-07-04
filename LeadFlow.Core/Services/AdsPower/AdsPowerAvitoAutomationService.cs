using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadFlow.Core.Data;
using LeadFlow.Core.Logging.Audit;
using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using LeadFlow.Core.Services.Browser;
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
    IPhoneNormalizer phoneNormalizer) : IAdsPowerAvitoAutomationService
{
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

    public async Task<string> ExtractCandidatesJsonAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default,
        CandidatesMessengerEnrichmentHints? messengerEnrichmentHints = null)
    {
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
                    cancellationToken)
                .ConfigureAwait(false);

            var executeScript = (string script, CancellationToken ct) =>
                EvaluateWithRetryAsync<string>(page, script, ct);

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
                BuildResolveExistingPhonesCallback(messengerEnrichmentHints)).ConfigureAwait(false);

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
                    cancellationToken)
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
                    cancellationToken)
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
    /// Если в HTML обнаружена капча/firewall — логируем и бросаем <see cref="AvitoCaptchaDetectedException"/>,
    /// чтобы мониторинг перевёл аккаунт в RequiresManualAction и не долбил Avito дальше.
    /// </summary>
    private static async Task ThrowIfCaptchaAsync(IPage page, string html, CancellationToken cancellationToken)
    {
        var kind = AvitoCaptchaDetector.Classify(html);
        if (kind is null)
        {
            return;
        }

        var screenshot = await BrowserDiagnosticsCapture
            .CapturePageScreenshotAsync(page, cancellationToken)
            .ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower captcha/firewall detected ({kind}) on {page.Url ?? "<unknown>"}.",
            DeskLinkAuditLogLevel.Warning,
            properties: new Dictionary<string, object?>
            {
                ["step"] = "captcha_detected",
                ["page.url"] = page.Url,
                ["captcha.kind"] = kind,
                ["screenshot.bytes"] = screenshot?.Length ?? 0
            });

        throw new AvitoCaptchaDetectedException(kind, page.Url, html, screenshot);
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
                    cancellationToken)
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

        if (IsOnCandidatesResponsesPage(page.Url))
        {
            await NavigateAwayFromCandidatesForSwitchAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
                .ConfigureAwait(false);
        }

        await EnsureSwitchModalAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
            .ConfigureAwait(false);
        if (!await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(CaptureProfileSwitchHtmlInSessionAsync))
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("AdsPower CDP: модалка переключения профилей не загрузилась.");
        }

        var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken)
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
                ["html.length"] = html.Length
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
                    cancellationToken)
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

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId).ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: subProfile {subProfileId} already current — closed modal, no click.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(SwitchActiveProfileAsync),
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "already_current",
                        ["avito.subProfileId"] = subProfileId
                    });
                result = true;
                return result;
            }

            result = await TryClickSubProfileCardAndWaitCloseAsync(page, subProfileId, cancellationToken)
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

    private static Task<bool> IsTargetSubProfileAlreadyCurrentAsync(IPage page, string subProfileId) =>
        PuppeteerJsonEvaluator.EvaluateBoolAsync(
            page,
            $@"(() => {{
                const el = document.querySelector('[data-marker=""component-profile-switch/profile-{Escape(subProfileId)}""]');
                return !!el && /isCurrent/i.test(el.className || '');
            }})()");

    /// <summary>Закрывает модалку «Выбор профиля», если она открыта (Escape, затем уход на /profile/pro/items).</summary>
    private static async Task DismissProfileSwitchModalAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var open = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                page,
                "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))")
                .ConfigureAwait(false);
            if (!open)
            {
                return;
            }

            try
            {
                await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
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
            "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))")
            .ConfigureAwait(false);
        if (!stillOpen)
        {
            return;
        }

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
        CancellationToken cancellationToken)
    {
        try
        {
            await page.WaitForSelectorAsync(
                    $"[data-marker='component-profile-switch/profile-{Escape(subProfileId)}']",
                    new WaitForSelectorOptions { Timeout = 20_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: target card not found in time: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "card_timeout",
                    ["avito.subProfileId"] = subProfileId
                });
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var clicked = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                page,
                BuildClickSubProfileJs(subProfileId))
            .ConfigureAwait(false);

        if (!clicked)
        {
            await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            await page.WaitForFunctionAsync(
                    "() => !document.querySelector(\"[data-marker='component-profile-switch/root']\") || !document.querySelector(\"[role='dialog']\")",
                    new WaitForFunctionOptions { Timeout = 30_000, PollingInterval = 650 })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: modal-close wait timed out: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "modal_close_timeout",
                    ["avito.subProfileId"] = subProfileId
                });

            var modalClosed = false;
            try
            {
                var modalStillOpen = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                    page,
                    "(!!document.querySelector(\"[data-marker='component-profile-switch/root']\"))").ConfigureAwait(false);
                if (!modalStillOpen)
                {
                    modalClosed = true;
                }
                else
                {
                    for (var retry = 1; retry <= 3; retry++)
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            $"AdsPower profile-switch: retry {retry}/3 click for subProfile {subProfileId}...",
                            DeskLinkAuditLogLevel.Info,
                            memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                            
                            properties: new Dictionary<string, object?>
                            {
                                ["step"] = "retry_click",
                                ["retry"] = retry,
                                ["avito.subProfileId"] = subProfileId
                            });

                        var elementClicked = await PuppeteerJsonEvaluator.EvaluateBoolAsync(
                            page,
                            BuildClickSubProfileJs(subProfileId)).ConfigureAwait(false);

                        if (!elementClicked)
                        {
                            _ = GlobalLogger.Instance.LogAsync(
                                $"AdsPower profile-switch: retry {retry}/3 — subProfile card element not found in DOM for {subProfileId}.",
                                DeskLinkAuditLogLevel.Warning,
                                memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                                properties: new Dictionary<string, object?>
                                {
                                    ["step"] = "retry_element_not_found",
                                    ["retry"] = retry,
                                    ["avito.subProfileId"] = subProfileId
                                });
                            await Task.Delay(retry * 2000, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        try
                        {
                            await page.WaitForFunctionAsync(
                                    "() => !document.querySelector(\"[data-marker='component-profile-switch/root']\") || !document.querySelector(\"[role='dialog']\")",
                                    new WaitForFunctionOptions { Timeout = 15_000, PollingInterval = 400 })
                                .ConfigureAwait(false);
                            modalClosed = true;
                            break;
                        }
                        catch
                        {
                            // still open after this retry, continue loop
                        }
                    }

                    if (!modalClosed)
                    {
                        _ = GlobalLogger.Instance.LogAsync(
                            $"AdsPower profile-switch: all retries failed for subProfile {subProfileId}, skipping.",
                            DeskLinkAuditLogLevel.Warning,
                            memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                            
                            properties: new Dictionary<string, object?>
                            {
                                ["step"] = "retry_failed",
                                ["avito.subProfileId"] = subProfileId
                            });
                    }
                }
            }
            catch (Exception innerEx)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: retry logic threw for subProfile {subProfileId}: {innerEx.Message}",
                    DeskLinkAuditLogLevel.Error,
                            memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                            properties: new Dictionary<string, object?>
                    {
                        ["step"] = "retry_internal_error",
                        ["avito.subProfileId"] = subProfileId
                    });
            }

            if (!modalClosed)
            {
                return false;
            }
        }

        await HumanDelay.AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch: subProfile {subProfileId} activated.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
            
            properties: new Dictionary<string, object?>
            {
                ["step"] = "switched",
                ["avito.subProfileId"] = subProfileId
            });

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

            var page = await browser.NewPageAsync().ConfigureAwait(false);
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
                "AdsPower: URL открыт в новой вкладке через CDP.",
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
    private static async Task EnsureSwitchModalAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName = nameof(EnsureSwitchModalAsync))
    {
        if (IsOnProfileSwitchPage(page.Url))
        {
            await BounceToDashboardBeforeSwitchAsync(page, cancellationToken, callerMemberName)
                .ConfigureAwait(false);
        }

        for (var i = 0; i <= 2; i++)
        {
            try
            {
                await page.GoToAsync(ProfileSwitchPageUrl, new NavigationOptions
                {
                    Timeout = 45_000,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
                }).ConfigureAwait(false);

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
                return;
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
            "Не удалось открыть страницу переключения суб-профилей Avito (навигация на dashboard#profile/switch).");
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
            await page.GoToAsync(ProfileDashboardPageUrl, new NavigationOptions
            {
                Timeout = 45_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            await page.GoToAsync(ProfileDashboardPageUrl, new NavigationOptions
            {
                Timeout = 45_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ждём корень модалки и карточки профилей перед чтением <c>isCurrent</c> или кликом.</summary>
    private static async Task<bool> AwaitProfileSwitchModalContentAsync(
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

    private static async Task<bool> TryAwaitProfileSwitchModalContentOnceAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        var modalReady = false;
        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='component-profile-switch/root']",
                    new WaitForSelectorOptions { Timeout = 18_000 })
                .ConfigureAwait(false);
            modalReady = true;
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
            return false;
        }

        try
        {
            await page.WaitForFunctionAsync(
                    "() => !!document.querySelector(\"[data-marker^='component-profile-switch/profile-']\")",
                    new WaitForFunctionOptions { Timeout = 12_000, PollingInterval = 650 })
                .ConfigureAwait(false);
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
            el.click();
            return true;
        }})()";

    private static Task<AvitoPageState?> ProbePageStateAsync(IPage page, CancellationToken cancellationToken) =>
        AvitoPageStateProbe.TryProbeAsync(
            (script, ct) => EvaluateWithRetryAsync<string>(page, script, ct),
            cancellationToken);

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
                if (AvitoAutomationFailureFormatter.SuggestsLogin(state))
                {
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

                string? staleListSignature = null;
                if (AvitoCandidatesPageUrls.IsCandidatesResponsesUrl(page.Url))
                {
                    staleListSignature = await AvitoCandidatesPageWaiter
                        .TryCaptureListSignatureAsync(executeScript, cancellationToken)
                        .ConfigureAwait(false);
                }

                await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken)
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
                        staleListSignature)
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

        await AvitoLoginProbe.ThrowIfLoginRequiredAsync(executeScript, cancellationToken)
            .ConfigureAwait(false);

        var finalState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        if (finalState?.IsOnCandidates == true)
        {
            return;
        }

        if (AvitoAutomationFailureFormatter.SuggestsLogin(finalState))
        {
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
    /// Одна рабочая вкладка на сессию CDP: иначе при нескольких вкладках Avito автоматизация идёт в фоне,
    /// а пользователь смотрит на другую (типично «Мои объявления»), и кажется, что парсинг не работает.
    /// </summary>
    private static async Task<IPage> AcquireAutomationPageAsync(
        IBrowser browser,
        string preferredUrl,
        string callerMemberName,
        CancellationToken cancellationToken)
    {
        var targetKind = ClassifyAutomationPageKind(preferredUrl);
        var pages = (await browser.PagesAsync().ConfigureAwait(false)).ToList();

        var worker =
            pages.FirstOrDefault(p => PageMatchesAutomationKind(p.Url, targetKind))
            ?? pages.FirstOrDefault(p => IsAvitoProfileAutomationTab(p.Url))
            ?? pages.FirstOrDefault(p => IsUsableWorkerPageUrl(p.Url) && IsAvitoProfileAutomationTab(p.Url))
            ?? pages.FirstOrDefault(p => IsUsableWorkerPageUrl(p.Url));

        if (worker is null || !IsUsableWorkerPageUrl(worker.Url))
        {
            worker = await browser.NewPageAsync().ConfigureAwait(false);
            pages = [worker];
        }

        var closed = 0;
        foreach (var page in pages)
        {
            if (page == worker || !IsAvitoProfileAutomationTab(page.Url))
            {
                continue;
            }

            try
            {
                await page.CloseAsync().ConfigureAwait(false);
                closed++;
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower CDP: не удалось закрыть лишнюю вкладку Avito ({page.Url}): {ex.Message}",
                    DeskLinkAuditLogLevel.Debug,
                    memberName: callerMemberName,
                    
                    properties: new Dictionary<string, object?>
                    {
                        ["page.url"] = page.Url,
                        ["error.type"] = ex.GetType().FullName
                    });
            }
        }

        try
        {
            await worker.BringToFrontAsync().ConfigureAwait(false);
        }
        catch
        {
            // Не критично для парсинга.
        }

        if (closed > 0 || pages.Count > 1)
        {
            _ = GlobalLogger.Instance.LogAsync(
                closed > 0
                    ? $"AdsPower CDP: рабочая вкладка {worker.Url} (закрыто лишних вкладок кабинета: {closed})."
                    : $"AdsPower CDP: рабочая вкладка {worker.Url} (всего вкладок в профиле: {pages.Count}).",
                DeskLinkAuditLogLevel.Info,
                memberName: callerMemberName,
                
                properties: new Dictionary<string, object?>
                {
                    ["automation.targetKind"] = targetKind.ToString(),
                    ["automation.workerUrl"] = worker.Url,
                    ["automation.tabsClosed"] = closed,
                    ["automation.tabsBefore"] = pages.Count
                });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return worker;
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

    /// <summary>
    /// AdsPower при старте часто отдаёт вкладку с URL «:» / about:blank — CDP на ней не рендерит Avito SPA.
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

    private static bool LooksLikeAvitoMessengerChannelUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/profile/messenger/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("messenger/channel", StringComparison.OrdinalIgnoreCase);
    }

    private Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? BuildResolveExistingPhonesCallback(
        CandidatesMessengerEnrichmentHints? enrichmentHints)
    {
        if (enrichmentHints is null)
        {
            return null;
        }

        return async (phoneCandidates, cancellationToken) =>
            (IReadOnlySet<string>)await duplicateRepository
                .GetExistingNormalizedPhonesAsync(
                    phoneCandidates,
                    enrichmentHints.DuplicateScope,
                    enrichmentHints.AccountId,
                    cancellationToken,
                    enrichmentHints.AvitoSubProfileId)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// На странице откликов кнопка «в чат» часто без href; ссылка канала появляется в шапке мини-мессенджера
    /// (<c>mini-messenger/messenger-page-link</c>) только после клика — дополняем JSON для AdsPower CDP.
    /// </summary>
    /// <summary>Номер уже полностью на карточке (не «узнать в чате»): после <see cref="IPhoneNormalizer.Normalize"/> — типичный РФ-мобильный.</summary>
    private static bool LooksLikeCompleteRussianMobile(string normalized) =>
        normalized.Length == 11 && normalized.StartsWith("7", StringComparison.Ordinal);

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

        HashSet<string>? existingNormalizedFromDb = null;
        if (enrichmentHints is not null)
        {
            var toQuery = new List<string>();
            foreach (var node in candidates)
            {
                var o = node?.AsObject();
                if (o is null)
                {
                    continue;
                }

                var phoneRaw = o["phone"]?.GetValue<string>() ?? string.Empty;
                var n = phoneNormalizer.Normalize(phoneRaw);
                if (LooksLikeCompleteRussianMobile(n))
                {
                    toQuery.Add(n);
                }
            }

            if (toQuery.Count > 0)
            {
                existingNormalizedFromDb = await duplicateRepository
                    .GetExistingNormalizedPhonesAsync(
                        toQuery,
                        enrichmentHints.DuplicateScope,
                        enrichmentHints.AccountId,
                        cancellationToken,
                        enrichmentHints.AvitoSubProfileId)
                    .ConfigureAwait(false);
            }
        }

        var viewportRestore = await EnsureMessengerEnrichmentViewportAsync(page, cancellationToken)
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

        try
        {
            const int maxEnrich = 80;
            for (var i = 0; i < candidates.Count && i < maxEnrich; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = candidates[i]?.AsObject();
                if (item is null)
                {
                    continue;
                }

                var phoneRawForSkip = item["phone"]?.GetValue<string>() ?? string.Empty;
                var normalizedForSkip = phoneNormalizer.Normalize(phoneRawForSkip);
                var isKnownPhone = existingNormalizedFromDb is not null
                    && LooksLikeCompleteRussianMobile(normalizedForSkip)
                    && existingNormalizedFromDb.Contains(normalizedForSkip);
                if (isKnownPhone)
                {
                    var hasUnread = await TryReadCandidateChatUnreadAsync(page, i, cancellationToken)
                        .ConfigureAwait(false);
                    if (!hasUnread)
                    {
                        continue;
                    }
                }

                var enrichment = await TryEnrichMessengerForCandidateCardAsync(
                        page,
                        i,
                        candidatesReturnUrl,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(enrichment.ChannelUrl) && enrichment.ChatMessages.Count == 0)
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"AdsPower messenger enrich: no chat data for candidate index {i}.",
                        DeskLinkAuditLogLevel.Warning,
                        memberName: nameof(TryEnrichCandidatesJsonMessengerUrlsAsync),
                        properties: new Dictionary<string, object?>
                        {
                            ["candidate.index"] = i,
                            ["page.url"] = page.Url,
                            ["page.innerWidth"] = await TryReadInnerWidthAsync(page).ConfigureAwait(false)
                        });
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
        finally
        {
            await RestoreMessengerEnrichmentViewportAsync(page, viewportRestore, cancellationToken)
                .ConfigureAwait(false);
        }
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
                    back.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
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

    private sealed record MessengerCardEnrichmentResult(string? ChannelUrl, JsonArray ChatMessages);

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

    private static async Task<MessengerCardEnrichmentResult> TryEnrichMessengerForCandidateCardAsync(
        IPage page,
        int candidateIndex,
        string candidatesReturnUrl,
        CancellationToken cancellationToken)
    {
        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);
        await HumanDelay.BeforeCandidateClickAsync(cancellationToken).ConfigureAwait(false);

        var clickRaw = await EvaluateWithRetryAsync<string>(
                page,
                AvitoCandidatesPageScripts.BuildClickCandidateChatByIndexScript(candidateIndex),
                cancellationToken)
            .ConfigureAwait(false);
        if (!TryParseMessengerChatClickStep(clickRaw, out var clicked, out var clickReason) || !clicked)
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

            return new MessengerCardEnrichmentResult(null, new JsonArray());
        }

        await HumanDelay.AfterCandidateClickAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await page.WaitForFunctionAsync(
                    AvitoCandidatesPageScripts.BuildMessengerUiVisibleExpression(),
                    new WaitForFunctionOptions { Timeout = 12_000, PollingInterval = 250 })
                .ConfigureAwait(false);
        }
        catch
        {
            // На узком окне мини-чат может не появиться; полноэкранный канал тоже ждём ниже при сборе.
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

        JsonArray chatMessages = new();
        for (var round = 0; round < 4; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(round == 0 ? 280 : 220, cancellationToken).ConfigureAwait(false);

            var messagesRaw = await EvaluateWithRetryAsync<string>(
                    page,
                    AvitoCandidatesPageScripts.BuildScrollAndCollectMiniMessengerMessagesScript(),
                    cancellationToken)
                .ConfigureAwait(false);
            var parsed = TryParseMiniMessengerMessages(messagesRaw);
            if (parsed.Count > 0)
            {
                chatMessages = parsed;
                break;
            }
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

        return new MessengerCardEnrichmentResult(channelUrl, chatMessages);
    }

    private sealed record ViewportSnapshot(int Width, int Height);

    /// <summary>
    /// На узком окне Avito открывает чат на отдельной странице вместо мини-панели — расширяем viewport перед enrichment.
    /// </summary>
    private static async Task<ViewportSnapshot?> EnsureMessengerEnrichmentViewportAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        var width = await TryReadInnerWidthAsync(page).ConfigureAwait(false);
        var height = await TryReadInnerHeightAsync(page).ConfigureAwait(false);
        if (width >= MessengerEnrichmentViewportWidth && height >= MessengerEnrichmentViewportHeight)
        {
            return null;
        }

        var snapshot = new ViewportSnapshot(
            width > 0 ? width : MessengerEnrichmentViewportWidth,
            height > 0 ? height : MessengerEnrichmentViewportHeight);

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

        await Task.Delay(450, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private static async Task RestoreMessengerEnrichmentViewportAsync(
        IPage page,
        ViewportSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = snapshot.Width,
                Height = snapshot.Height
            }).ConfigureAwait(false);
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Не прерываем выдачу списка кандидатов.
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

    private static JsonArray TryParseMiniMessengerMessages(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new JsonArray();
        }

        try
        {
            using var doc = JsonDocument.Parse(UnwrapMessengerJson(raw));
            if (!doc.RootElement.TryGetProperty("messages", out var messagesElement)
                || messagesElement.ValueKind != JsonValueKind.Array)
            {
                return new JsonArray();
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

            return result;
        }
        catch
        {
            return new JsonArray();
        }
    }

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

    private static async Task<T> EvaluateWithRetryAsync<T>(IPage page, string expression, CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<T>(expression).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            return await page.EvaluateExpressionAsync<T>(expression).ConfigureAwait(false);
        }
    }

    private static readonly string ExtractionScript =
        AvitoCandidatesPageScripts.BuildExtractionScriptForPuppeteer();
}
