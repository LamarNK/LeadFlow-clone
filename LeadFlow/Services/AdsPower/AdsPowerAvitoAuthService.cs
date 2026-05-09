using System.Text.Json;
using LeadFlow.Logging.Audit;
using PuppeteerSharp;

namespace LeadFlow.Services.AdsPower;

/// <summary>
/// Проверяет авторизацию Avito в внешнем браузере AdsPower через CDP (PuppeteerSharp.ConnectAsync).
/// Браузер не закрывается — после извлечения данных вызываем <see cref="IBrowser.Disconnect"/>.
/// </summary>
public sealed class AdsPowerAvitoAuthService(IAdsPowerApiClient adsPowerApiClient) : IAdsPowerAvitoAuthService
{
    /// <summary>
    /// Стабильная страница профиля. Avito не редиректит её для Pro-аккаунтов
    /// (в отличие от <c>/profile</c>), а в боковой панели всегда есть
    /// <c>[data-marker='osp-sidebar/tools/profile/name']</c> с именем пользователя.
    /// </summary>
    private const string AvitoProfileUrl = "https://www.avito.ru/profile/basic";

    /// <summary>
    /// Селектор имени пользователя в боковой панели Avito Pro. Используем как «маркер готовности» страницы.
    /// </summary>
    private const string ProfileNameSelector = "[data-marker='osp-sidebar/tools/profile/name']";

    public async Task<AdsPowerAvitoAuthResult> CheckAuthorizationAsync(
        AdsPowerConnectionOptions options,
        string adsPowerUserId,
        CancellationToken cancellationToken = default)
    {
        Log(
            DeskLinkAuditLogLevel.Info,
            $"Auth check started for AdsPower profile {adsPowerUserId}.",
            new Dictionary<string, object?>
            {
                ["step"] = "start",
                ["adsPower.userId"] = adsPowerUserId,
                ["adsPower.baseUrl"] = options.BaseUrl,
                ["avito.url"] = AvitoProfileUrl
            });

        AdsPowerBrowserStartResult start;
        try
        {
            start = await adsPowerApiClient
                .StartBrowserAsync(options, adsPowerUserId, AvitoProfileUrl, cancellationToken)
                .ConfigureAwait(false);
            Log(
                DeskLinkAuditLogLevel.Info,
                "AdsPower browser/start completed; will connect via CDP.",
                new Dictionary<string, object?>
                {
                    ["step"] = "browser_started",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["adsPower.hasWsEndpoint"] = !string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl),
                    ["adsPower.debugPort"] = start.DebugPort
                });
        }
        catch (Exception ex)
        {
            Log(
                DeskLinkAuditLogLevel.Error,
                $"AdsPower browser/start failed during auth check: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["step"] = "browser_start_failed",
                    ["adsPower.userId"] = adsPowerUserId,
                    ["error.type"] = ex.GetType().FullName
                });
            return new AdsPowerAvitoAuthResult(
                IsAuthorized: false,
                ProfileName: null,
                CurrentUrl: null,
                HasLoginForm: false,
                HasCaptcha: false,
                ErrorMessage: $"AdsPower не смог запустить браузер: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(start.WebSocketDebuggerUrl))
        {
            Log(
                DeskLinkAuditLogLevel.Warning,
                "AdsPower не вернул ws.puppeteer endpoint — без CDP проверка невозможна.",
                new Dictionary<string, object?>
                {
                    ["step"] = "no_ws_endpoint",
                    ["adsPower.userId"] = adsPowerUserId
                });
            return new AdsPowerAvitoAuthResult(
                IsAuthorized: false,
                ProfileName: null,
                CurrentUrl: null,
                HasLoginForm: false,
                HasCaptcha: false,
                ErrorMessage: "AdsPower не вернул ws.puppeteer endpoint. Включите Local API и обновите AdsPower.");
        }

        IBrowser? browser = null;
        try
        {
            var connectOptions = new ConnectOptions
            {
                BrowserWSEndpoint = start.WebSocketDebuggerUrl,
                DefaultViewport = null
            };
            browser = await Puppeteer.ConnectAsync(connectOptions).ConfigureAwait(false);
            Log(
                DeskLinkAuditLogLevel.Info,
                "Connected to AdsPower browser via CDP.",
                new Dictionary<string, object?>
                {
                    ["step"] = "cdp_connected",
                    ["adsPower.userId"] = adsPowerUserId
                });

            var page = await GetOrCreateAvitoPageAsync(browser).ConfigureAwait(false);
            Log(
                DeskLinkAuditLogLevel.Info,
                $"Working with page {page.Url}.",
                new Dictionary<string, object?>
                {
                    ["step"] = "page_acquired",
                    ["page.url"] = page.Url
                });

            await EnsureAvitoProfilePageAsync(page, cancellationToken).ConfigureAwait(false);

            await WaitForProfileSidebarAsync(page, cancellationToken).ConfigureAwait(false);

            var rawJson = await EvaluateWithRetryAsync(page, cancellationToken).ConfigureAwait(false);
            Log(
                DeskLinkAuditLogLevel.Info,
                "Auth check extraction script evaluated.",
                new Dictionary<string, object?>
                {
                    ["step"] = "evaluated",
                    ["raw.length"] = rawJson?.Length ?? 0
                });

            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new AdsPowerAvitoAuthResult(
                    IsAuthorized: false,
                    ProfileName: null,
                    CurrentUrl: page.Url,
                    HasLoginForm: false,
                    HasCaptcha: false,
                    ErrorMessage: "Скрипт проверки вернул пустой результат.");
            }

            var parsed = ParseExtractionResult(rawJson, fallbackUrl: page.Url);
            Log(
                DeskLinkAuditLogLevel.Info,
                $"Auth check parsed. authorized={parsed.IsAuthorized}, hasLoginForm={parsed.HasLoginForm}, hasCaptcha={parsed.HasCaptcha}, profileName={parsed.ProfileName ?? "<null>"}",
                new Dictionary<string, object?>
                {
                    ["step"] = "parsed",
                    ["auth.isAuthorized"] = parsed.IsAuthorized,
                    ["auth.hasLoginForm"] = parsed.HasLoginForm,
                    ["auth.hasCaptcha"] = parsed.HasCaptcha,
                    ["auth.profileName"] = parsed.ProfileName,
                    ["auth.currentUrl"] = parsed.CurrentUrl
                });

            return parsed;
        }
        catch (Exception ex)
        {
            Log(
                DeskLinkAuditLogLevel.Error,
                $"Auth check failed: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["step"] = "failed",
                    ["error.type"] = ex.GetType().FullName,
                    ["error.message"] = ex.Message
                });
            return new AdsPowerAvitoAuthResult(
                IsAuthorized: false,
                ProfileName: null,
                CurrentUrl: null,
                HasLoginForm: false,
                HasCaptcha: false,
                ErrorMessage: ex.Message);
        }
        finally
        {
            if (browser is not null)
            {
                try
                {
                    browser.Disconnect();
                    Log(
                        DeskLinkAuditLogLevel.Info,
                        "Disconnected from AdsPower browser (browser kept open).",
                        new Dictionary<string, object?>
                        {
                            ["step"] = "cdp_disconnected"
                        });
                }
                catch (Exception ex)
                {
                    Log(
                        DeskLinkAuditLogLevel.Warning,
                        $"CDP disconnect raised: {ex.Message}",
                        new Dictionary<string, object?>
                        {
                            ["step"] = "cdp_disconnect_failed",
                            ["error.type"] = ex.GetType().FullName
                        });
                }
            }
        }
    }

    private static async Task<IPage> GetOrCreateAvitoPageAsync(IBrowser browser)
    {
        var pages = await browser.PagesAsync().ConfigureAwait(false);
        var avito = pages.FirstOrDefault(p =>
            !string.IsNullOrWhiteSpace(p.Url)
            && p.Url.Contains("avito.ru", StringComparison.OrdinalIgnoreCase));
        if (avito is not null)
        {
            return avito;
        }

        var any = pages.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Url) && !p.Url.StartsWith("about:", StringComparison.Ordinal));
        if (any is not null)
        {
            return any;
        }

        return pages.FirstOrDefault() ?? await browser.NewPageAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Принудительно ведём страницу на <c>/profile/basic</c>: только там точно есть имя в боковой панели,
    /// плюс /profile у Pro-аккаунтов уезжает на /profile/pro/items. На login переходить не нужно — это и так login.
    /// </summary>
    private static async Task EnsureAvitoProfilePageAsync(IPage page, CancellationToken cancellationToken)
    {
        var url = page.Url ?? string.Empty;
        if (url.Contains("avito.ru/login", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (url.Contains("avito.ru/profile/basic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await page.GoToAsync(AvitoProfileUrl, new NavigationOptions
            {
                Timeout = 60_000,
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded]
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("Navigation", StringComparison.OrdinalIgnoreCase))
        {
            Log(
                DeskLinkAuditLogLevel.Warning,
                $"Navigation to Avito profile raised, will continue: {ex.Message}",
                new Dictionary<string, object?>
                {
                    ["step"] = "navigation_recoverable_error",
                    ["error.type"] = ex.GetType().FullName
                });
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ждёт появления имени в боковой панели до 8 секунд. Если страница — login, ждать нечего и сразу идём дальше.
    /// </summary>
    private static async Task WaitForProfileSidebarAsync(IPage page, CancellationToken cancellationToken)
    {
        var url = page.Url ?? string.Empty;
        if (url.Contains("avito.ru/login", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await page.WaitForSelectorAsync(
                ProfileNameSelector,
                new WaitForSelectorOptions { Timeout = 8_000, Visible = false }).ConfigureAwait(false);
            Log(
                DeskLinkAuditLogLevel.Info,
                "Profile sidebar selector appeared.",
                new Dictionary<string, object?>
                {
                    ["step"] = "sidebar_ready",
                    ["selector"] = ProfileNameSelector
                });
        }
        catch (Exception ex)
        {
            Log(
                DeskLinkAuditLogLevel.Warning,
                $"Profile sidebar selector did not appear in time ({ex.GetType().Name}). Continuing with what we have.",
                new Dictionary<string, object?>
                {
                    ["step"] = "sidebar_timeout",
                    ["selector"] = ProfileNameSelector
                });
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Avito может пересоздать execution context во время evaluate; делаем 1 повтор.
    /// </summary>
    private static async Task<string> EvaluateWithRetryAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await page.EvaluateExpressionAsync<string>(ExtractionScript).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.Message.Contains("Execution Context was destroyed", StringComparison.OrdinalIgnoreCase))
        {
            Log(
                DeskLinkAuditLogLevel.Warning,
                "EvaluateExpression: Execution Context was destroyed; retrying once.",
                new Dictionary<string, object?>
                {
                    ["step"] = "evaluate_retry"
                });
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken).ConfigureAwait(false);
            return await page.EvaluateExpressionAsync<string>(ExtractionScript).ConfigureAwait(false);
        }
    }

    private static AdsPowerAvitoAuthResult ParseExtractionResult(string rawJson, string fallbackUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var hasLogin = root.TryGetProperty("hasLogin", out var hl) && hl.ValueKind == JsonValueKind.True;
            var hasCaptcha = root.TryGetProperty("hasCaptcha", out var hc) && hc.ValueKind == JsonValueKind.True;
            var profileName = root.TryGetProperty("profileName", out var pn) ? pn.GetString() : null;
            var trimmedName = string.IsNullOrWhiteSpace(profileName) ? null : profileName!.Trim();

            // Avito редиректит залогиненных PRO-пользователей с /profile на /profile/pro/items и т.п.
            // Если конечный URL остаётся на /profile/* (не /login) и нет формы входа — считаем авторизованным,
            // даже если имя в шапке не удалось распознать.
            var effectiveUrl = string.IsNullOrWhiteSpace(url) ? fallbackUrl : url;
            var urlIndicatesAuthorized =
                !string.IsNullOrWhiteSpace(effectiveUrl)
                && effectiveUrl!.Contains("avito.ru/profile", StringComparison.OrdinalIgnoreCase)
                && !effectiveUrl.Contains("/profile/login", StringComparison.OrdinalIgnoreCase)
                && !effectiveUrl.Contains("/profile/auth", StringComparison.OrdinalIgnoreCase)
                && !effectiveUrl.Contains("avito.ru/login", StringComparison.OrdinalIgnoreCase);

            var isAuthorized = !hasLogin && !hasCaptcha && urlIndicatesAuthorized;

            return new AdsPowerAvitoAuthResult(
                IsAuthorized: isAuthorized,
                ProfileName: trimmedName,
                CurrentUrl: effectiveUrl,
                HasLoginForm: hasLogin,
                HasCaptcha: hasCaptcha,
                ErrorMessage: null);
        }
        catch (JsonException jex)
        {
            return new AdsPowerAvitoAuthResult(
                IsAuthorized: false,
                ProfileName: null,
                CurrentUrl: fallbackUrl,
                HasLoginForm: false,
                HasCaptcha: false,
                ErrorMessage: $"Не удалось разобрать ответ скрипта: {jex.Message}");
        }
    }

    private static void Log(
        DeskLinkAuditLogLevel level,
        string message,
        Dictionary<string, object?>? properties)
    {
        try
        {
            _ = GlobalLogger.Instance.LogAsync(
                message,
                level,
                memberName: nameof(AdsPowerAvitoAuthService),
                filePath: "AdsPowerAvitoAuthService.cs",
                properties: properties);
        }
        catch
        {
            // Логирование не должно ломать проверку.
        }
    }

    /// <summary>
    /// Скрипт читает: текущий URL, наличие формы входа, капчи и имя пользователя в шапке Avito.
    /// IIFE async — Puppeteer.EvaluateExpression сам ждёт Promise.
    /// </summary>
    private const string ExtractionScript =
        """
        (async () => {
            const url = window.location.href || '';
            const bodyText = (document.body && document.body.innerText) ? document.body.innerText : '';

            const cleanText = (s) => (s || '').replace(/\s+/g, ' ').trim();
            const looksLikeName = (s) => {
                const t = cleanText(s);
                if (!t || t.length < 2 || t.length > 80) return false;
                if (/войти|вход|регистр|пароль|телефон|почта|кабинет|меню|menu|выйти|объявлен|управлен|настрой|подписк/i.test(t)) return false;
                if (/^avito\b|^авито\b/i.test(t)) return false;
                return true;
            };

            // Текстовые + структурные маркеры: firewall-страница Avito («Доступ ограничен»)
            // в bodyText слова «firewall» не содержит, поэтому проверяем DOM-узлы напрямую.
            const hasCaptcha =
                /капч|captcha|подтвердите[\s\S]*проверочный код|Доступ\s+ограничен|проблема\s+с\s+IP/i.test(bodyText) ||
                !!document.querySelector('.firewall-container, .js-firewall-form, .firewall-title, .h-captcha') ||
                !!document.getElementById('h-captcha') ||
                !!document.getElementById('geetest_captcha') ||
                !!document.getElementById('inner-captcha');
            const hasLoginForm =
                !!document.querySelector("input[type='password']") ||
                !!document.querySelector("[data-marker='login/password']") ||
                !!document.querySelector("form[action*='login']") ||
                /\/login(?:[/?#]|$)|\/profile\/login(?:[/?#]|$)/i.test(url) ||
                /телефон или почт|войти в авито|зарегистрир/i.test(bodyText.slice(0, 4000));

            // Точные селекторы Avito Pro имеют наивысший приоритет; остальные — на случай редизайна и обычного /profile.
            const selectors = [
                "[data-marker='osp-sidebar/tools/profile/name'] h6[title]",
                "[data-marker='osp-sidebar/tools/profile/name']",
                "[data-marker='osp-sidebar/tools/profile/avatar'] img[alt]",
                "[data-marker='header/profile']",
                "[data-marker='profile-button']",
                "[data-marker='profile/avatar']",
                "[data-marker='header-bar/user-menu']",
                "[data-marker='user-menu']",
                "[data-marker='user-menu/header/name']",
                "[data-marker='user-menu/item/name']",
                "[data-marker='user-menu/profile-name']",
                "[data-marker*='user-menu'][data-marker*='name']",
                "header [data-marker='profile-name']",
                "header a[href='/profile']",
                "header a[href^='/profile']",
                "header [class*='userName']",
                "header [class*='user-name']",
                "header [class*='userMenu'][class*='name']",
                "header [class*='UserMenu'][class*='name']",
                "header [class*='profileMenu'][class*='name']",
                "[class*='profileSidebar'] [class*='name']",
                "[class*='profile-header'] [class*='name']",
                "[class*='ProfileHeader'] [class*='name']"
            ];

            let profileName = '';
            const tryNode = (el) => {
                if (!el) return '';
                if (typeof el.getAttribute === 'function') {
                    const title = cleanText(el.getAttribute('title'));
                    if (looksLikeName(title)) return title;
                    const aria = cleanText(el.getAttribute('aria-label'));
                    if (looksLikeName(aria)) return aria;
                    const altAttr = cleanText(el.getAttribute('alt'));
                    if (looksLikeName(altAttr) && !/аватар|avatar/i.test(altAttr)) return altAttr;
                }
                const candidate = cleanText(el.textContent);
                if (looksLikeName(candidate)) return candidate;
                return '';
            };
            for (const sel of selectors) {
                const elements = document.querySelectorAll(sel);
                for (const el of elements) {
                    const got = tryNode(el);
                    if (got) { profileName = got; break; }
                }
                if (profileName) break;
            }

            // Fallback: дёргаем внутренние JSON-эндпоинты Avito (cookies сессии браузера AdsPower уже подставятся).
            if (!profileName && !hasLoginForm) {
                const tryFetch = async (path) => {
                    try {
                        const r = await fetch(path, {
                            credentials: 'include',
                            cache: 'no-cache',
                            headers: { 'Accept': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }
                        });
                        if (!r.ok) return null;
                        const ct = r.headers.get('content-type') || '';
                        if (!/json/i.test(ct)) return null;
                        return await r.json();
                    } catch (e) { return null; }
                };
                const pickName = (j) => {
                    if (!j || typeof j !== 'object') return '';
                    const direct =
                        j.name || j.full_name || j.fullName || j.displayName || j.display_name ||
                        (j.user && (j.user.name || j.user.full_name || j.user.fullName || j.user.displayName)) ||
                        (j.profile && (j.profile.name || j.profile.full_name || j.profile.fullName)) ||
                        (j.account && (j.account.name || j.account.full_name || j.account.fullName)) ||
                        (j.result && (j.result.name || (j.result.user && (j.result.user.name || j.result.user.fullName))));
                    return cleanText(direct);
                };

                const endpoints = [
                    '/web/2/header/info',
                    '/web/3/header/info',
                    '/api/3.0/user/info',
                    '/web/1/profile/me',
                    '/web/2/profile/preview'
                ];
                for (const p of endpoints) {
                    const j = await tryFetch(p);
                    if (!j) continue;
                    const nm = pickName(j);
                    if (looksLikeName(nm)) { profileName = nm; break; }
                }
            }

            // Title-fallback: для Avito Pro обычно бесполезен ("Объявления — Avito Pro"),
            // зато для обычного /profile иногда даёт "Имя Фамилия — личный кабинет ...".
            if (!profileName && !hasLoginForm) {
                const title = cleanText(document.title || '');
                if (title) {
                    const parts = title.split(/\s[—\-–]\s/);
                    if (parts.length >= 2 && looksLikeName(parts[0])) profileName = cleanText(parts[0]);
                }
            }

            return JSON.stringify({ url, hasLogin: hasLoginForm, hasCaptcha, profileName });
        })()
        """;
}
