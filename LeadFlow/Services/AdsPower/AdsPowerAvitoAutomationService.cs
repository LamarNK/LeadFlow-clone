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
    private const string ProfileSwitchPageUrl = "https://www.avito.ru/profile/pro/items#profile/switch?withEntities=true";

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
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(800, cancellationToken).ConfigureAwait(false);
            }

            var raw = await EvaluateWithRetryAsync<string>(page, ExtractionScript, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidOperationException("AdsPower CDP: скрипт извлечения вернул пустой результат.");
            }

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
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
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
                        new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 500 })
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
            // Используем «человеческую» рандомную задержку (1.5–3.5 с), чтобы не палить ботскую частоту запросов.
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
                    await Task.Delay(800, cancellationToken).ConfigureAwait(false);
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
                        new WaitForFunctionOptions { Timeout = 60_000, PollingInterval = 500 })
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

            try
            {
                await page.WaitForSelectorAsync(
                    "[data-marker='component-profile-switch/root']",
                    new WaitForSelectorOptions { Timeout = 30_000 }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: modal selector wait timed out: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadProfileSwitchHtmlAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "modal_timeout",
                        ["page.url"] = page.Url
                    });
            }

            // Ждём, пока в модалке появится хотя бы одна карточка профиля.
            try
            {
                await page.WaitForFunctionAsync(
                        "() => !!document.querySelector(\"[data-marker^='component-profile-switch/profile-']\")",
                        new WaitForFunctionOptions { Timeout = 20_000, PollingInterval = 400 })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: profile cards not detected in time: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(LoadProfileSwitchHtmlAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "cards_timeout",
                        ["page.url"] = page.Url
                    });
            }

            await HumanDelay.AfterSwitchModalAsync(cancellationToken).ConfigureAwait(false);

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

            // Если профиль уже активный (isCurrent), переключение не нужно — Avito не проводит навигацию.
            var alreadyCurrent = await page.EvaluateExpressionAsync<bool>(
                $"(() => {{ const el = document.querySelector('[data-marker=\"component-profile-switch/profile-{Escape(subProfileId)}\"]'); return !!el && /isCurrent/i.test(el.className || ''); }})()")
                .ConfigureAwait(false);

            if (alreadyCurrent)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: subProfile {subProfileId} is already current — skipping click.",
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

            try
            {
                await page.WaitForSelectorAsync(
                    $"[data-marker='component-profile-switch/profile-{Escape(subProfileId)}']",
                    new WaitForSelectorOptions { Timeout = 20_000, Visible = true }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: target card not found in time: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(SwitchActiveProfileAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "card_timeout",
                        ["avito.subProfileId"] = subProfileId
                    });
                return false;
            }

            // JS-клик надёжнее ElementHandle.ClickAsync — не зависит от viewport и анимаций.
            var clicked = await page.EvaluateExpressionAsync<bool>(
                $"(() => {{ const el = document.querySelector('[data-marker=\"component-profile-switch/profile-{Escape(subProfileId)}\"]'); if (!el) return false; el.click(); return true; }})()")
                .ConfigureAwait(false);

            if (!clicked)
            {
                return false;
            }

            // Ждём, пока модалка закроется (Avito скрывает её и перезагружает данные профиля).
            try
            {
                await page.WaitForFunctionAsync(
                        "() => !document.querySelector(\"[data-marker='component-profile-switch/root']\") || !document.querySelector(\"[role='dialog']\")",
                        new WaitForFunctionOptions { Timeout = 30_000, PollingInterval = 400 })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"AdsPower profile-switch: modal-close wait timed out: {ex.Message}",
                    DeskLinkAuditLogLevel.Warning,
                    memberName: nameof(SwitchActiveProfileAsync),
                    filePath: "AdsPowerAvitoAutomationService.cs",
                    properties: new Dictionary<string, object?>
                    {
                        ["step"] = "modal_close_timeout",
                        ["avito.subProfileId"] = subProfileId
                    });
            }

            // Даём React-у дозагрузить данные нового профиля + рандом, чтобы не было ровного интервала между переключениями.
            await HumanDelay.AfterProfileSwitchAsync(cancellationToken).ConfigureAwait(false);

            _ = GlobalLogger.Instance.LogAsync(
                $"AdsPower profile-switch: subProfile {subProfileId} activated.",
                DeskLinkAuditLogLevel.Info,
                memberName: nameof(SwitchActiveProfileAsync),
                filePath: "AdsPowerAvitoAutomationService.cs",
                properties: new Dictionary<string, object?>
                {
                    ["step"] = "switched",
                    ["avito.subProfileId"] = subProfileId
                });

            return true;
        }
        finally
        {
            try { browser?.Disconnect(); } catch { /* keep AdsPower window alive */ }
        }
    }

    /// <summary>
    /// Гарантирует, что URL содержит хеш <c>#profile/switch?withEntities=true</c>; если нет — навигирует.
    /// Хеш-навигация на той же странице не вызывает полную перезагрузку, так что используем GoTo на абсолютный URL.
    /// </summary>
    private static async Task EnsureSwitchModalAsync(IPage page, CancellationToken cancellationToken)
    {
        var url = page.Url ?? string.Empty;
        var alreadyOnSwitch = url.Contains("#profile/switch", StringComparison.OrdinalIgnoreCase);

        if (alreadyOnSwitch)
        {
            return;
        }

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
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
        }
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

    private static async Task<T> EvaluateWithRetryAsync<T>(IPage page, string expression, CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<T>(expression).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverableNavigationError(ex))
        {
            await Task.Delay(800, cancellationToken).ConfigureAwait(false);
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
                const chatEl = root.querySelector("[data-marker='job-application/link/to-chat']");
                const fromEl = (el) => normalizeUrl(el?.getAttribute?.("href") ?? el?.getAttribute?.("data-href") ?? "");
                const direct = fromEl(chatEl) || fromEl(chatEl?.closest?.("a"));
                if (direct) return direct;
                const anyLink = Array.from(root.querySelectorAll("[href],[data-href]")).map(fromEl).find(Boolean);
                return anyLink || "";
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
