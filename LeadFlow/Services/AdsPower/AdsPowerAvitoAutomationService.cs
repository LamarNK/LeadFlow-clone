using System.Text.Json.Nodes;
using LeadFlow.Logging.Audit;
using LeadFlow.Services;
using LeadFlow.Services.Avito;
using PuppeteerSharp;

namespace LeadFlow.Services.AdsPower;

/// <summary>
/// CDP-операции в AdsPower-браузере для Avito: парсинг страниц <c>/profile/candidates</c>
/// и <c>/profile/pro/items</c>. Браузер AdsPower никогда не закрываем — после работы
/// вызываем <see cref="IBrowser.Disconnect"/>, чтобы пользователь продолжал работать в окне.
/// </summary>
public sealed class AdsPowerAvitoAutomationService(IAdsPowerApiClient adsPowerApiClient) : IAdsPowerAvitoAutomationService
{
    private const string CandidatesPageUrl = "https://www.avito.ru/profile/candidates";
    private const string ProfileItemsPageUrl = "https://www.avito.ru/profile/pro/items";
    private const string ProfileBlockedItemsPageUrl = "https://www.avito.ru/profile/pro/items?filters=%7B%22tabs%22%3A%22rejected%22%7D";
    /// <summary>Модалка «Выбор профиля» через дашборд — надёжнее, чем с <c>/profile/pro/items</c>.</summary>
    private const string ProfileSwitchPageUrl = "https://www.avito.ru/profile/dashboard#profile/switch?withEntities=true";

    public async Task<string> ExtractCandidatesJsonAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, CandidatesPageUrl, cancellationToken)
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
            var page = await GetOrCreateAvitoPageAsync(browser, CandidatesPageUrl).ConfigureAwait(false);

            if (!IsOnUrl(page.Url, CandidatesPageUrl))
            {
                try
                {
                    await page.GoToAsync(CandidatesPageUrl, new NavigationOptions
                    {
                        Timeout = 60_000,
                        WaitUntil = [WaitUntilNavigation.Networkidle2]
                    }).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRecoverableNavigationError(ex))
                {
                    await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                await page.WaitForFunctionAsync(
                        "() => document.readyState === 'complete' && (document.body?.innerText ?? '').trim().length > 150",
                        new WaitForFunctionOptions { Timeout = 30_000 })
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableNavigationError(ex))
            {
                await Task.Delay(1400, cancellationToken).ConfigureAwait(false);
            }

            var raw = await EvaluateWithRetryAsync<string>(page, ExtractionScript, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("AdsPower CDP: скрипт извлечения вернул пустой результат.");
            }

            raw = await TryEnrichCandidatesJsonMessengerUrlsAsync(page, raw, cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                "AdsPower CDP candidates extraction completed.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(ExtractCandidatesJsonAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
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
            try
            {
                browser?.Disconnect();
            }
            catch
            {
                // Disconnect must never throw out of the finally — браузер AdsPower остаётся в покое.
            }
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
            filePath: "AdsPowerAvitoAutomationService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileItemsPageUrl
            });

        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, ProfileItemsPageUrl, cancellationToken)
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
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "cdp_connected",
                    ["adsPower.userId"] = adsPowerUserId
                });

            var page = await GetOrCreateAvitoPageAsync(browser, ProfileItemsPageUrl).ConfigureAwait(false);

            if (!IsOnUrl(page.Url, ProfileItemsPageUrl))
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
                        memberName: nameof(LoadProfileItemsHtmlAsync),
                        filePath: "AdsPowerAvitoAutomationService.cs");
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
                    filePath: "AdsPowerAvitoAutomationService.cs",
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
                    filePath: "AdsPowerAvitoAutomationService.cs",
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
                    filePath: "AdsPowerAvitoAutomationService.cs",
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
                    filePath: "AdsPowerAvitoAutomationService.cs",
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

            ThrowIfCaptcha(html, page.Url, nameof(LoadProfileItemsHtmlAsync), adsPowerUserId);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-items: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadProfileItemsHtmlAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
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
            try
            {
                browser?.Disconnect();
                _ = GlobalLogger.Instance.LogAsync(
                    "AdsPower profile-items: CDP disconnected (browser kept alive).",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(LoadProfileItemsHtmlAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "cdp_disconnected",
                        ["adsPower.userId"] = adsPowerUserId
                    });
            }
            catch
            {
                // Disconnect must never throw out of the finally.
            }
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
            filePath: "AdsPowerAvitoAutomationService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileBlockedItemsPageUrl
            });

        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, ProfileBlockedItemsPageUrl, cancellationToken)
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
            var page = await GetOrCreateAvitoPageAsync(browser, ProfileBlockedItemsPageUrl).ConfigureAwait(false);

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
                    memberName: nameof(LoadBlockedItemsHtmlAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs");
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
                    filePath: "AdsPowerAvitoAutomationService.cs",
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

            ThrowIfCaptcha(html, page.Url, nameof(LoadBlockedItemsHtmlAsync), adsPowerUserId);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower blocked-items: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadBlockedItemsHtmlAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
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
            try { browser?.Disconnect(); } catch { /* keep AdsPower window alive */ }
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
    private static void ThrowIfCaptcha(string html, string? pageUrl, string memberName, string adsPowerUserId)
    {
        var kind = AvitoCaptchaDetector.Classify(html);
        if (kind is null)
        {
            return;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower {memberName}: обнаружена капча/firewall ({kind}) на {pageUrl ?? "<unknown>"}.",
            DeskLinkAuditLogLevel.Warning,
            memberName: memberName,
            filePath: "AdsPowerAvitoAutomationService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "captcha_detected",
                ["adsPower.userId"] = adsPowerUserId,
                ["page.url"] = pageUrl,
                ["captcha.kind"] = kind
            });

        throw new AvitoCaptchaDetectedException(kind, pageUrl, html);
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
            filePath: "AdsPowerAvitoAutomationService.cs",
            properties: new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = ProfileSwitchPageUrl
            });

        var start = await adsPowerApiClient
            .StartBrowserAsync(options, adsPowerUserId, ProfileSwitchPageUrl, cancellationToken)
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
            var page = await GetOrCreateAvitoPageAsync(browser, ProfileSwitchPageUrl).ConfigureAwait(false);

            await EnsureSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(LoadProfileSwitchHtmlAsync))
                .ConfigureAwait(false);

            var html = await EvaluateWithRetryAsync<string>(
                page,
                "(() => document.documentElement?.outerHTML || '')()",
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(html))
            {
                throw new InvalidOperationException("AdsPower CDP: страница переключения профилей вернула пустой HTML.");
            }

            ThrowIfCaptcha(html, page.Url, nameof(LoadProfileSwitchHtmlAsync), adsPowerUserId);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: HTML captured ({html.Length} chars).",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(LoadProfileSwitchHtmlAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
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
            try { browser?.Disconnect(); } catch { /* keep AdsPower window alive */ }
        }
    }

    public async Task<bool> SwitchActiveProfileAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        string subProfileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subProfileId))
        {
            return false;
        }

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch click started: subProfile={subProfileId}.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(SwitchActiveProfileAsync),
            filePath: "AdsPowerAvitoAutomationService.cs",
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
        try
        {
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            var page = await GetOrCreateAvitoPageAsync(browser, ProfileSwitchPageUrl).ConfigureAwait(false);

            // Всегда dashboard#profile/switch — читаем модалку в актуальном контексте Avito.
            await EnsureSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
            await AwaitProfileSwitchModalContentAsync(page, cancellationToken, nameof(SwitchActiveProfileAsync))
                .ConfigureAwait(false);

            if (await IsTargetSubProfileAlreadyCurrentAsync(page, subProfileId).ConfigureAwait(false))
            {
                await DismissProfileSwitchModalAsync(page, cancellationToken).ConfigureAwait(false);
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: subProfile {subProfileId} already current — closed modal, no click.",
                    DeskLinkAuditLogLevel.Info,
                    memberName: nameof(SwitchActiveProfileAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "already_current",
                        ["avito.subProfileId"] = subProfileId
                    });
                return true;
            }

            return await TryClickSubProfileCardAndWaitCloseAsync(page, subProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { browser?.Disconnect(); } catch { /* keep AdsPower window alive */ }
        }
    }

    private static Task<bool> IsTargetSubProfileAlreadyCurrentAsync(IPage page, string subProfileId) =>
        page.EvaluateExpressionAsync<bool>(
            $@"(() => {{
                const el = document.querySelector('[data-marker=""component-profile-switch/profile-{Escape(subProfileId)}""]');
                return !!el && /isCurrent/i.test(el.className || '');
            }})()");

    /// <summary>Закрывает модалку «Выбор профиля», если она открыта (Escape, затем уход на /profile/pro/items).</summary>
    private static async Task DismissProfileSwitchModalAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 3; i++)
        {
            var open = await page.EvaluateExpressionAsync<bool>(
                "() => !!document.querySelector(\"[data-marker='component-profile-switch/root']\")")
                .ConfigureAwait(false);
            if (!open)
            {
                return;
            }

            try
            {
                await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            await Task.Delay(450, cancellationToken).ConfigureAwait(false);
        }

        var stillOpen = await page.EvaluateExpressionAsync<bool>(
            "() => !!document.querySelector(\"[data-marker='component-profile-switch/root']\")")
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
        catch
        {
            // ignore
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
                    new WaitForSelectorOptions { Timeout = 20_000, Visible = true })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: target card not found in time: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "card_timeout",
                    ["avito.subProfileId"] = subProfileId
                });
            return false;
        }

        var clicked = await page.EvaluateExpressionAsync<bool>(
                $@"(() => {{
                    const el = document.querySelector('[data-marker=""component-profile-switch/profile-{Escape(subProfileId)}""]');
                    if (!el) return false;
                    el.click();
                    return true;
                }})()")
            .ConfigureAwait(false);

        if (!clicked)
        {
            return false;
        }

        try
        {
            await page.WaitForFunctionAsync(
                    "() => !document.querySelector(\"[data-marker='component-profile-switch/root']\") || !document.querySelector(\"[role='dialog']\")",
                    new WaitForFunctionOptions { Timeout = 30_000, PollingInterval = 650 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: modal-close wait timed out: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "modal_close_timeout",
                    ["avito.subProfileId"] = subProfileId
                });
        }

        await HumanDelay.AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);

        _ = GlobalLogger.Instance.LogAsync(
            $"AdsPower profile-switch: subProfile {subProfileId} activated.",
            DeskLinkAuditLogLevel.Info,
            memberName: nameof(TryClickSubProfileCardAndWaitCloseAsync),
            filePath: "AdsPowerAvitoAutomationService.cs",
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
        CancellationToken cancellationToken = default)
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
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["adsPower.userId"] = adsPowerUserId,
                    ["page.url"] = page.Url,
                    ["targetUrl"] = target
                });
        }
        finally
        {
            try
            {
                browser?.Disconnect();
            }
            catch
            {
                // keep AdsPower window alive
            }
        }
    }

    /// <summary>
    /// Всегда открывает модалку «Выбор профиля» через <c>/profile/dashboard#profile/switch?withEntities=true</c>,
    /// чтобы прочитать актуальный <c>isCurrent</c>, а не состояние с другой страницы Avito.
    /// </summary>
    private static async Task EnsureSwitchModalAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.GoToAsync(ProfileSwitchPageUrl, new NavigationOptions
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

    /// <summary>Ждём корень модалки и карточки профилей перед чтением <c>isCurrent</c> или кликом.</summary>
    private static async Task AwaitProfileSwitchModalContentAsync(
        IPage page,
        CancellationToken cancellationToken,
        string callerMemberName)
    {
        try
        {
            await page.WaitForSelectorAsync(
                    "[data-marker='component-profile-switch/root']",
                    new WaitForSelectorOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: modal selector wait timed out: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "modal_timeout",
                    ["page.url"] = page.Url
                });
        }

        try
        {
            await page.WaitForFunctionAsync(
                    "() => !!document.querySelector(\"[data-marker^='component-profile-switch/profile-']\")",
                    new WaitForFunctionOptions { Timeout = 20_000, PollingInterval = 650 })
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: profile cards not detected in time: {ex.Message}",
                DeskLinkAuditLogLevel.Warning,
                memberName: callerMemberName,
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "cards_timeout",
                    ["page.url"] = page.Url
                });
        }

        await HumanDelay.AfterSwitchModalAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static async Task<IPage> GetOrCreateAvitoPageAsync(IBrowser browser, string preferredUrl)
    {
        var pages = await browser.PagesAsync().ConfigureAwait(false);

        var exact = pages.FirstOrDefault(p => IsOnUrl(p.Url, preferredUrl));
        if (exact is not null)
        {
            return exact;
        }

        var avito = pages.FirstOrDefault(p => !string.IsNullOrEmpty(p.Url) &&
            p.Url.Contains("avito.ru", StringComparison.OrdinalIgnoreCase));
        if (avito is not null)
        {
            return avito;
        }

        var any = pages.FirstOrDefault();
        return any ?? await browser.NewPageAsync().ConfigureAwait(false);
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
         ex.Message.Contains("frame got detached", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeAvitoMessengerChannelUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/profile/messenger/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("messenger/channel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// На странице откликов кнопка «в чат» часто без href; ссылка канала появляется в шапке мини-мессенджера
    /// (<c>mini-messenger/messenger-page-link</c>) только после клика — дополняем JSON для AdsPower CDP.
    /// </summary>
    private static async Task<string> TryEnrichCandidatesJsonMessengerUrlsAsync(
        IPage page,
        string rawJson,
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

        const int maxEnrich = 80;
        for (var i = 0; i < candidates.Count && i < maxEnrich; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = candidates[i]?.AsObject();
            if (item is null)
            {
                continue;
            }

            var messengerUrl = item["messengerUrl"]?.GetValue<string>();
            if (LooksLikeAvitoMessengerChannelUrl(messengerUrl))
            {
                continue;
            }

            var channelUrl = await TryReadMessengerChannelUrlForCandidateCardAsync(page, i, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(channelUrl))
            {
                continue;
            }

            item["messengerUrl"] = channelUrl;
            item["sourceResponseId"] = channelUrl;
        }

        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);

        return root.ToJsonString();
    }

    private static async Task CloseMiniMessengerPanelIfOpenAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            var hasPanel = await page.EvaluateExpressionAsync<bool>(
                    "!!document.querySelector(\"a[data-marker='mini-messenger/messenger-page-link']\")")
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

    private static async Task<string?> TryReadMessengerChannelUrlForCandidateCardAsync(
        IPage page,
        int candidateIndex,
        CancellationToken cancellationToken)
    {
        await CloseMiniMessengerPanelIfOpenAsync(page, cancellationToken).ConfigureAwait(false);

        var clickExpr = CandidateCardChatClickExpression.Replace(
            "__INDEX__",
            candidateIndex.ToString(),
            StringComparison.Ordinal);

        var clicked = await page.EvaluateExpressionAsync<bool>(clickExpr).ConfigureAwait(false);
        if (!clicked)
        {
            return null;
        }

        await Task.Delay(350, cancellationToken).ConfigureAwait(false);

        try
        {
            await page.WaitForSelectorAsync(
                    "a[data-marker='mini-messenger/messenger-page-link'][href*='/profile/messenger/']",
                    new WaitForSelectorOptions { Timeout = 12_000 })
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var href = await page.EvaluateExpressionAsync<string>(
                @"(() => {
                    const a = document.querySelector(""a[data-marker='mini-messenger/messenger-page-link']"");
                    return a?.href ?? """";
                })()")
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(href) ? null : href.Trim();
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
            for (const button of statusButtons) {
                const root = button.closest?.("[data-marker='job-application/item']") ?? findCardRoot(button);
                if (!root || seen.has(root)) {
                    continue;
                }
                seen.add(root);
                roots.push(root);
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

    private const string ExtractionScript =
        """
        JSON.stringify((() => {
            const bodyText = document.body?.innerText ?? "";
            // Текстовые маркеры + структурные (Avito firewall не пишет «капча» в видимом тексте,
            // но всегда имеет div.firewall-container / форму js-firewall-form / встроенный hCaptcha/geetest).
            const hasCaptcha =
                /капч|captcha|подтвердите|проверочный код|Доступ\s+ограничен|проблема\s+с\s+IP/i.test(bodyText) ||
                !!document.querySelector('.firewall-container, .js-firewall-form, .firewall-title, .h-captcha') ||
                !!document.getElementById('h-captcha') ||
                !!document.getElementById('geetest_captcha') ||
                !!document.getElementById('inner-captcha');
            const isVisible = (element) => {
                if (!element) return false;
                const style = window.getComputedStyle(element);
                if (style.display === "none" || style.visibility === "hidden") return false;
                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };
            const containsAuthText = (value) =>
                /телефон или почта|пароль|забыли пароль|регистрац|войти|вход/i.test(value ?? "");
            const loginCandidates = Array.from(document.querySelectorAll("input, button, a, h1, h2, h3, label, span, div"));
            const hasLogin = loginCandidates.some((element) => {
                if (!isVisible(element)) return false;
                return containsAuthText(element.textContent) ||
                    containsAuthText(element.getAttribute?.("placeholder")) ||
                    containsAuthText(element.getAttribute?.("aria-label"));
            });

            const statusButtons = Array.from(document.querySelectorAll("[data-marker='job-application/response/status-select-button']"));
            const roots = [];
            const seen = new Set();
            const findCardRoot = (element) => {
                let current = element;
                while (current) {
                    const name = current.querySelector?.("h3");
                    const phone = current.querySelector?.("[data-marker='job-application/phone']");
                    if (name && phone) return current;
                    current = current.parentElement;
                }
                return null;
            };
            for (const button of statusButtons) {
                const root = button.closest?.("[data-marker='job-application/item']") ?? findCardRoot(button);
                if (!root || seen.has(root)) continue;
                seen.add(root);
                roots.push(root);
            }

            const normalizeUrl = (href) => {
                if (!href) return "";
                const t = href.trim();
                if (!t || t === "#") return "";
                if (t.startsWith("//")) return `https:${t}`;
                if (t.startsWith("/")) return `${window.location.origin}${t}`;
                return t;
            };
            const resolveMessengerUrl = (root) => {
                const pick = (href) => normalizeUrl(href ?? "");
                const attrCandidates = ["href", "data-href", "data-url", "data-to", "data-link", "data-state", "onclick"];
                const fromAttributes = (element) => {
                    if (!element) {
                        return "";
                    }

                    for (const attr of attrCandidates) {
                        const raw = element.getAttribute?.(attr);
                        if (!raw) {
                            continue;
                        }

                        const direct = pick(raw);
                        if (direct && /(messenger|chat|dialog)/i.test(direct)) {
                            return direct;
                        }

                        const match = String(raw).match(/https?:\/\/[^"'\\\s]*(messenger|chat|dialog)[^"'\\\s]*/i);
                        if (match?.[0]) {
                            return pick(match[0]);
                        }
                    }

                    return "";
                };

                const chatEl = root.querySelector("[data-marker='job-application/link/to-chat']");
                if (chatEl) {
                    const ownUrl = fromAttributes(chatEl);
                    if (ownUrl) {
                        return ownUrl;
                    }

                    const parentA = chatEl.closest("a");
                    if (parentA) {
                        const h = pick(parentA.getAttribute("href"));
                        if (h && /(messenger|chat|dialog)/i.test(h)) {
                            return h;
                        }
                    }

                    const parentWithAttrs = chatEl.closest("[href],[data-href],[data-url],[data-to],[data-link],[data-state],[onclick]");
                    const parentUrl = fromAttributes(parentWithAttrs);
                    if (parentUrl) {
                        return parentUrl;
                    }
                }

                for (const element of root.querySelectorAll("[href],[data-href],[data-url],[data-to],[data-link],[data-state],[onclick]")) {
                    if (element.closest?.("a[data-marker='job-application/link/to-resume']")) {
                        continue;
                    }

                    const h = fromAttributes(element);
                    if (h) {
                        return h;
                    }
                }

                return "";
            };
            const fnv1a32Hex = (text) => {
                let h = 2166136261 >>> 0;
                for (let i = 0; i < text.length; i++) {
                    h ^= text.charCodeAt(i);
                    h = Math.imul(h, 16777619) >>> 0;
                }
                return h.toString(16);
            };

            const candidates = roots.map((root) => {
                const name = root.querySelector("h3")?.textContent?.trim() ?? "";
                const phone = root.querySelector("[data-marker='job-application/phone']")?.textContent?.trim() ?? "";
                const ageText = root.querySelector("p[data-marker='undefined/container'] span")?.textContent?.trim() ?? "";
                const vacancyAnchor = root.querySelector("[data-marker='job-application/link/to-resume']");
                let vacancyUrl = normalizeUrl(vacancyAnchor?.getAttribute("href") ?? "");
                if (/\/profile\/candidates(?:[/?#]|$)/i.test(vacancyUrl)) vacancyUrl = "";
                const vacancyLine = vacancyAnchor?.textContent?.replace(/\s+/g, " ").trim() ?? "";
                const vacancyParts = vacancyLine.split("·").map((x) => x.trim()).filter(Boolean);
                const vacancy = vacancyParts[0] ?? "";
                const city = vacancyParts.length > 1 ? vacancyParts[1] : "";
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                const messengerUrl = resolveMessengerUrl(root);
                const stablePayload = [name, phone, vacancy, city, vacancyUrl, messengerUrl]
                    .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                    .join("\u001f");
                const sourceResponseId = messengerUrl || `avito:${fnv1a32Hex(stablePayload)}`;
                return { fullName: name, phone, age: ageText, vacancy, city, vacancyUrl, messengerUrl, sourceResponseId, rawText };
            }).filter((item) => item.fullName && item.phone);

            return { url: window.location.href, hasCaptcha, hasLogin, candidates };
        })())
        """;
}
