using System.Text.Json;
using LeadFlow.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public sealed class AvitoPageReaderService(IWebPageAutomationService automationService) : IAvitoPageReaderService
{
    public async Task<AuthCheckResult> CheckAuthorizationAsync(BrowserAccountSession session, AvitoSelectorOptions selectors, CancellationToken cancellationToken)
    {
        var currentUrl = session.CurrentUrl;
        var raw = await automationService.ExecuteScriptAsync(
            session,
            """
            (() => {
                const bodyText = document.body?.innerText ?? "";
                // Текстовые + структурные маркеры: firewall-страница Avito («Доступ ограничен»)
                // не содержит слова «капча», но имеет div.firewall-container с hCaptcha/geetest.
                const hasCaptcha =
                    /капч|captcha|подтвердите|проверочный код|Доступ\s+ограничен|проблема\s+с\s+IP/i.test(bodyText) ||
                    !!document.querySelector('.firewall-container, .js-firewall-form, .firewall-title, .h-captcha') ||
                    !!document.getElementById('h-captcha') ||
                    !!document.getElementById('geetest_captcha') ||
                    !!document.getElementById('inner-captcha');
                const isVisible = (element) => {
                    if (!element) {
                        return false;
                    }

                    const style = window.getComputedStyle(element);
                    if (style.display === "none" || style.visibility === "hidden") {
                        return false;
                    }

                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                };

                const containsAuthText = (value) =>
                    /телефон или почта|пароль|забыли пароль|регистрац|войти|вход/i.test(value ?? "");

                const loginSelectors = Array.from(document.querySelectorAll("input, button, a, h1, h2, h3, label, span, div"));
                const hasLogin = loginSelectors.some((element) => {
                    if (!isVisible(element)) {
                        return false;
                    }

                    return containsAuthText(element.textContent) ||
                        containsAuthText(element.getAttribute?.("placeholder")) ||
                        containsAuthText(element.getAttribute?.("aria-label"));
                });

                const profileSelectors = Array.from(document.querySelectorAll("a, button, h1, h2, h3, span, div"));
                const hasProfileMarkers = profileSelectors.some((element) => {
                    if (!isVisible(element)) {
                        return false;
                    }

                    return /мои объявления|избранн|сообщени|кошел[её]к|заказы|настройки профиля|мой профиль/i.test(element.textContent ?? "");
                });

                const profileNameCandidates = [
                    document.querySelector("[data-marker='profile-sidebar-head/avatar']")?.getAttribute("title"),
                    document.querySelector("[data-marker='profile-sidebar-head/avatar']")?.parentElement?.parentElement?.querySelector("span div")?.textContent,
                    document.querySelector("[data-marker='profile-sidebar-head/avatar']")?.parentElement?.parentElement?.querySelector("span")?.textContent,
                    document.querySelector("meta[property='profile:first_name']")?.getAttribute("content"),
                    document.querySelector("h1")?.textContent
                ];

                const profileName = profileNameCandidates
                    .map((value) => value?.trim() ?? "")
                    .find((value) => value.length > 0 && !/профиль|настройки|редактировать/i.test(value)) ?? "";

                return {
                    url: window.location.href,
                    readyState: document.readyState,
                    bodyLength: bodyText.trim().length,
                    hasCaptcha,
                    hasLogin,
                    hasProfileMarkers,
                    isProfileUrl: /^https:\/\/www\.avito\.ru\/profile(?:[\/?#]|$)/i.test(window.location.href),
                    profileName
                };
            })();
            """,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new AuthCheckResult
            {
                CurrentUrl = currentUrl,
                StatusMessage = "Браузер не инициализирован"
            };
        }

        try
        {
            using var json = JsonDocument.Parse(raw);
            var root = json.RootElement;
            currentUrl = root.GetProperty("url").GetString() ?? currentUrl;
            var readyState = root.TryGetProperty("readyState", out var readyStateProp) ? readyStateProp.GetString() : null;
            var bodyLength = root.TryGetProperty("bodyLength", out var bodyLengthProp) ? bodyLengthProp.GetInt32() : 0;
            var hasCaptcha = root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean();
            var hasLogin = root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean();
            var hasProfileMarkers = root.TryGetProperty("hasProfileMarkers", out var profileMarkersProp) && profileMarkersProp.GetBoolean();
            var isProfileUrl = root.TryGetProperty("isProfileUrl", out var profileUrlProp) && profileUrlProp.GetBoolean();
            var profileName = root.TryGetProperty("profileName", out var profileNameProp) ? profileNameProp.GetString() ?? string.Empty : string.Empty;

            if (hasCaptcha)
            {
                return new AuthCheckResult
                {
                    CurrentUrl = currentUrl,
                    RequiresManualAction = true,
                    StatusMessage = "Требуется ручное действие"
                };
            }

            if (!string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase) || bodyLength < 200)
            {
                return new AuthCheckResult
                {
                    CurrentUrl = currentUrl,
                    StatusMessage = "Страница ещё загружается"
                };
            }

            if (hasLogin)
            {
                return new AuthCheckResult
                {
                    CurrentUrl = currentUrl,
                    StatusMessage = "Требуется авторизация"
                };
            }

            return new AuthCheckResult
            {
                CurrentUrl = currentUrl,
                IsAuthorized = isProfileUrl && hasProfileMarkers,
                ProfileName = profileName,
                StatusMessage = isProfileUrl && hasProfileMarkers
                    ? "Авторизован"
                    : "Не удалось подтвердить авторизацию"
            };
        }
        catch
        {
            return new AuthCheckResult
            {
                CurrentUrl = currentUrl,
                StatusMessage = "Не удалось проверить авторизацию"
            };
        }
    }
}
