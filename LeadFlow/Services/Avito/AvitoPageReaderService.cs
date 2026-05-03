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
            "(() => ({ url: window.location.href, hasCaptcha: /капч|captcha|подтвердите/i.test(document.body.innerText), hasLogin: /войти|авториз/i.test(document.body.innerText) }))();",
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
            var hasCaptcha = root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean();
            var hasLogin = root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean();

            if (hasCaptcha)
            {
                return new AuthCheckResult
                {
                    CurrentUrl = currentUrl,
                    RequiresManualAction = true,
                    StatusMessage = "Требуется ручное действие"
                };
            }

            return new AuthCheckResult
            {
                CurrentUrl = currentUrl,
                IsAuthorized = !hasLogin,
                StatusMessage = hasLogin ? "Требуется авторизация" : "Авторизован"
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
