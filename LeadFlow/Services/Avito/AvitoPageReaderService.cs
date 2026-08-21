using System.Text.Json;
using LeadFlow.Core.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public sealed class AvitoPageReaderService(IWebPageAutomationService automationService) : IAvitoPageReaderService
{
    public async Task<AuthCheckResult> CheckAuthorizationAsync(
        BrowserAccountSession session,
        AvitoSelectorOptions selectors,
        CancellationToken cancellationToken)
    {
        _ = selectors;
        var currentUrl = session.CurrentUrl;
        var raw = await automationService.ExecuteScriptAsync(
            session,
            """
            (() => {
                const bodyText = document.body?.innerText ?? "";
                const hasCaptcha =
                    /капч|captcha|подтвердите|проверочный код|Доступ\s+ограничен|проблема\s+с\s+IP|Отключить\s+VPN|самол[её]те/i.test(bodyText) ||
                    location.hash === '#block' ||
                    !!document.querySelector('.firewall-container, .js-firewall-form, .firewall-title, .h-captcha, a[href*="support.avito.ru/request/720"]');
                const isVisible = (element) => {
                    if (!element) return false;
                    const style = window.getComputedStyle(element);
                    if (style.display === "none" || style.visibility === "hidden") return false;
                    return element.getBoundingClientRect().width > 0;
                };
                const hasLogin = Array.from(document.querySelectorAll("input, button, a, h1, label, span, div"))
                    .some((el) => isVisible(el) && /телефон|пароль|войти|вход/i.test(el.textContent ?? ""));
                const hasProfileMarkers = Array.from(document.querySelectorAll("a, button, h1, span, div"))
                    .some((el) => isVisible(el) && /мои объявления|избранн|сообщени|кошел[её]к|мой профиль/i.test(el.textContent ?? ""));
                const profileName = document.querySelector("h1")?.textContent?.trim() ?? "";
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
            return new AuthCheckResult { CurrentUrl = currentUrl, StatusMessage = "Браузер не инициализирован" };
        }

        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        currentUrl = root.GetProperty("url").GetString() ?? currentUrl;
        if (root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean())
        {
            return new AuthCheckResult { CurrentUrl = currentUrl, RequiresManualAction = true, StatusMessage = "Требуется ручное действие" };
        }

        if (root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean())
        {
            return new AuthCheckResult { CurrentUrl = currentUrl, StatusMessage = "Требуется авторизация" };
        }

        var isProfileUrl = root.TryGetProperty("isProfileUrl", out var profileUrlProp) && profileUrlProp.GetBoolean();
        var hasProfileMarkers = root.TryGetProperty("hasProfileMarkers", out var markersProp) && markersProp.GetBoolean();
        return new AuthCheckResult
        {
            CurrentUrl = currentUrl,
            IsAuthorized = isProfileUrl && hasProfileMarkers,
            ProfileName = root.TryGetProperty("profileName", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty,
            StatusMessage = isProfileUrl && hasProfileMarkers ? "Авторизован" : "Не удалось подтвердить авторизацию"
        };
    }
}