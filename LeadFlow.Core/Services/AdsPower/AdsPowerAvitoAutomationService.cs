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
    private const string CandidatesPageUrl = "https://www.avito.ru/profile/candidates";
    private const string ProfileItemsPageUrl = "https://www.avito.ru/profile/pro/items";
    private const string ProfileBlockedItemsPageUrl = "https://www.avito.ru/profile/pro/items?filters=%7B%22tabs%22%3A%22rejected%22%7D";
    private const string ProfileDashboardPageUrl = "https://www.avito.ru/profile/dashboard";
    /// <summary>Модалка «Выбор профиля» через дашборд — надёжнее, чем с <c>/profile/pro/items</c>.</summary>
    private const string ProfileSwitchPageUrl = "https://www.avito.ru/profile/dashboard#profile/switch?withEntities=true";

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

            var knownPhones = await LoadKnownNormalizedPhonesAsync(messengerEnrichmentHints, cancellationToken)
                .ConfigureAwait(false);

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
                knownPhones).ConfigureAwait(false);

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

        if (IsOnUrl(page.Url, CandidatesPageUrl))
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

            if (IsOnUrl(page.Url, CandidatesPageUrl))
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
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
            if (state?.IsOnCandidates == true)
            {
                return;
            }

            if (state?.ProfileSwitchModalOpen == true)
            {
                recoveryAttempts.Add($"попытка {attempt}: закрыть модалку субпрофилей");
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                state = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
                if (state?.IsOnCandidates == true)
                {
                    return;
                }
            }

            var alreadyOnCandidates = IsOnUrl(page.Url, CandidatesPageUrl);
            _ = GlobalLogger.Instance.LogAsync(
                alreadyOnCandidates
                    ? $"AdsPower candidates: hard navigation attempt {attempt}/{maxAttempts}."
                    : $"AdsPower candidates: navigating to responses page (attempt {attempt}/{maxAttempts}).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(EnsureOnCandidatesPageAsync),
                properties: new Dictionary<string, object?>
                {
                    ["step"] = alreadyOnCandidates ? "candidates_hard_nav" : "candidates_goto",
                    ["attempt"] = attempt,
                    ["page.url"] = page.Url,
                    ["avito.url"] = CandidatesPageUrl,
                    ["pageState"] = state?.DescribeForDiagnostics()
                });

            recoveryAttempts.Add($"попытка {attempt}: переход на /profile/candidates");

            var navigationOptions = new NavigationOptions
            {
                Timeout = 60_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            };

            try
            {
                await page.GoToAsync(CandidatesPageUrl, navigationOptions).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(3000, cancellationToken).ConfigureAwait(false);
                await page.GoToAsync(CandidatesPageUrl, navigationOptions).ConfigureAwait(false);
            }

            string? staleListSignature = null;
            if (IsOnUrl(page.Url, CandidatesPageUrl))
            {
                staleListSignature = await AvitoCandidatesPageWaiter
                    .TryCaptureListSignatureAsync(executeScript, cancellationToken)
                    .ConfigureAwait(false);
            }

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
            if (state?.IsOnCandidates == true)
            {
                return;
            }
        }

        var finalState = await ProbePageStateAsync(page, cancellationToken).ConfigureAwait(false);
        throw new AvitoPageMismatchException(
            "отклики",
            AvitoPageKind.Candidates,
            finalState,
            recoveryAttempts);
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
        if (preferredUrl.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase))
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
            !string.IsNullOrEmpty(url) && url.Contains("/profile/candidates", StringComparison.OrdinalIgnoreCase),
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

    private async Task<HashSet<string>> LoadKnownNormalizedPhonesAsync(
        CandidatesMessengerEnrichmentHints? enrichmentHints,
        CancellationToken cancellationToken)
    {
        if (enrichmentHints is null)
        {
            return [];
        }

        return await duplicateRepository
            .GetAllStoredNormalizedPhonesAsync(
                enrichmentHints.DuplicateScope,
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
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

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

            var enrichment = await TryEnrichMessengerForCandidateCardAsync(page, i, cancellationToken)
                .ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);

        var clickExpr = CandidateCardChatClickExpression.Replace(
            "__INDEX__",
            candidateIndex.ToString(),
            StringComparison.Ordinal);

        var clicked = await PuppeteerJsonEvaluator.EvaluateBoolAsync(page, clickExpr).ConfigureAwait(false);
        if (!clicked)
        {
            return new MessengerCardEnrichmentResult(null, new JsonArray());
        }

        await Task.Delay(350, cancellationToken).ConfigureAwait(false);

        string? channelUrl = null;
        try
        {
            await page.WaitForSelectorAsync(
                    "a[data-marker='mini-messenger/messenger-page-link'][href*='/profile/messenger/']",
                    new WaitForSelectorOptions { Timeout = 12_000 })
                .ConfigureAwait(false);

            channelUrl = await page.EvaluateExpressionAsync<string>(
                    @"(() => {
                        const a = document.querySelector(""a[data-marker='mini-messenger/messenger-page-link']"");
                        return a?.href ?? """";
                    })()")
                .ConfigureAwait(false);
            channelUrl = string.IsNullOrWhiteSpace(channelUrl) ? null : channelUrl.Trim();
        }
        catch
        {
            // Мини-чат мог открыться без ссылки в шапке — всё равно попробуем снять сообщения.
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

        return new MessengerCardEnrichmentResult(channelUrl, chatMessages);
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

    /// <summary>Тот же порядок карточек, что и в <see cref="ExtractionScript"/>.</summary>
    private const string CandidateCardChatClickExpression =
        """
        (() => {
            const idx = __INDEX__;
            const statusButtons = Array.from(document.querySelectorAll("[data-marker='job-application/response/status-select-button']"));
            const roots = [];
            const seen = new Set();
            const findCardRoot = (element) => {
                let current = element;
                while (current) {
                    const name = current.querySelector?.("h3");
                    const phone = current.querySelector?.("[data-marker='job-application/phone']");
                    if (name && phone) {
                        return current;
                    }
                    current = current.parentElement;
                }
                return null;
            };
            const addRoot = (root) => {
                if (!root || seen.has(root)) {
                    return;
                }
                const name = root.querySelector?.("h3");
                const phone = root.querySelector?.("[data-marker='job-application/phone']");
                if (!name || !phone) {
                    return;
                }
                seen.add(root);
                roots.push(root);
            };
            for (const button of statusButtons) {
                const root = button.closest?.("[data-marker='job-application/item']") ?? findCardRoot(button);
                addRoot(root);
            }
            for (const item of document.querySelectorAll("[data-marker='job-application/item']")) {
                addRoot(item);
            }
            const root = roots[idx];
            if (!root) {
                return false;
            }
            const chat = root.querySelector("[data-marker='job-application/link/to-chat']");
            if (!chat) {
                return false;
            }
            try {
                chat.scrollIntoView({ block: "center", inline: "nearest" });
            } catch {
            }
            try {
                chat.click();
            } catch {
                return false;
            }
            return true;
        })()
        """;

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
