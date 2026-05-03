using System.Text.Json;
using LeadFlow.Data;
using LeadFlow.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public sealed class AvitoResponseSource(
    AppRepository repository,
    IBrowserSessionService browserSessionService,
    IWebPageAutomationService automationService) : IAvitoResponseSource
{
    private const string CandidatesUrl = "https://www.avito.ru/profile/candidates";

    public async Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        var session = await browserSessionService.CreateSessionAsync(account, cancellationToken);
        await using var host = await BackgroundWebViewHost.CreateAsync(cancellationToken);
        await host.AttachAsync(session, cancellationToken);
        await automationService.NavigateAsync(session, CandidatesUrl, cancellationToken);
        await WaitForCandidatesPageAsync(session, cancellationToken);
        account.LastAuthCheckAt = DateTime.UtcNow;

        var raw = await automationService.ExecuteScriptAsync(session, BuildExtractionScript(), cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            account.LastErrorMessage = "Не удалось получить данные со страницы кандидатов";
            return [];
        }

        using var json = JsonDocument.Parse(raw);
        var root = json.RootElement;
        var hasCaptcha = root.TryGetProperty("hasCaptcha", out var captchaProp) && captchaProp.GetBoolean();
        var hasLogin = root.TryGetProperty("hasLogin", out var loginProp) && loginProp.GetBoolean();

        if (hasCaptcha)
        {
            account.Status = AvitoAccountStatus.RequiresManualAction;
            account.LastErrorMessage = "На странице кандидатов требуется ручное действие";
            return [];
        }

        if (hasLogin)
        {
            account.Status = AvitoAccountStatus.RequiresLogin;
            account.LastErrorMessage = "Для страницы кандидатов требуется повторная авторизация";
            return [];
        }

        account.Status = AvitoAccountStatus.Authorized;
        account.LastErrorMessage = string.Empty;
        var candidates = ParseCandidates(root, account);
        var existingIds = await repository.GetExistingSourceResponseIdsAsync(
            candidates.Select(x => x.SourceResponseId),
            cancellationToken);

        return candidates
            .Where(x => !existingIds.Contains(x.SourceResponseId))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();
    }

    private async Task WaitForCandidatesPageAsync(BrowserAccountSession session, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = await automationService.ExecuteScriptAsync(
                session,
                "(() => ({ readyState: document.readyState, bodyLength: (document.body?.innerText ?? '').trim().length }))();",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var json = JsonDocument.Parse(raw);
                var root = json.RootElement;
                var readyState = root.TryGetProperty("readyState", out var readyStateProp) ? readyStateProp.GetString() : null;
                var bodyLength = root.TryGetProperty("bodyLength", out var bodyLengthProp) ? bodyLengthProp.GetInt32() : 0;
                if (string.Equals(readyState, "complete", StringComparison.OrdinalIgnoreCase) && bodyLength > 150)
                {
                    return;
                }
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    private static IReadOnlyList<CandidateResponse> ParseCandidates(JsonElement root, AvitoAccount account)
    {
        if (!root.TryGetProperty("candidates", out var candidatesElement) || candidatesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<CandidateResponse>();
        foreach (var item in candidatesElement.EnumerateArray())
        {
            var fullName = item.TryGetProperty("fullName", out var fullNameProp) ? fullNameProp.GetString() ?? string.Empty : string.Empty;
            var phone = item.TryGetProperty("phone", out var phoneProp) ? phoneProp.GetString() ?? string.Empty : string.Empty;
            var sourceResponseId = item.TryGetProperty("sourceResponseId", out var sourceIdProp) ? sourceIdProp.GetString() ?? string.Empty : string.Empty;

            if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(phone) || string.IsNullOrWhiteSpace(sourceResponseId))
            {
                continue;
            }

            var vacancy = item.TryGetProperty("vacancy", out var vacancyProp) ? vacancyProp.GetString() ?? string.Empty : string.Empty;
            var city = item.TryGetProperty("city", out var cityProp) ? cityProp.GetString() ?? string.Empty : string.Empty;
            var sourceUrl = item.TryGetProperty("sourceUrl", out var sourceUrlProp) ? sourceUrlProp.GetString() ?? CandidatesUrl : CandidatesUrl;
            var rawText = item.TryGetProperty("rawText", out var rawTextProp) ? rawTextProp.GetString() ?? string.Empty : string.Empty;
            var age = ParseAge(item.TryGetProperty("age", out var ageProp) ? ageProp.GetString() : null);

            results.Add(new CandidateResponse
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Source = "Avito",
                SourceResponseId = sourceResponseId,
                FullName = fullName,
                PhoneRaw = phone,
                City = city,
                Vacancy = vacancy,
                Age = age,
                SourceUrl = sourceUrl,
                RawText = rawText,
                CreatedAt = DateTime.UtcNow
            });
        }

        return results;
    }

    private static int? ParseAge(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var age) ? age : null;
    }

    private static string BuildExtractionScript() =>
        """
        (() => {
            const bodyText = document.body?.innerText ?? "";
            const hasCaptcha = /капч|captcha|подтвердите|проверочный код/i.test(bodyText);

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

            const loginCandidates = Array.from(document.querySelectorAll("input, button, a, h1, h2, h3, label, span, div"));
            const hasLogin = loginCandidates.some((element) => {
                if (!isVisible(element)) {
                    return false;
                }

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
                    if (name && phone) {
                        return current;
                    }

                    current = current.parentElement;
                }

                return null;
            };

            for (const button of statusButtons) {
                const root = findCardRoot(button);
                if (!root || seen.has(root)) {
                    continue;
                }

                seen.add(root);
                roots.push(root);
            }

            const normalizeUrl = (href) => {
                if (!href) {
                    return "";
                }

                if (href.startsWith("//")) {
                    return `https:${href}`;
                }

                if (href.startsWith("/")) {
                    return `${window.location.origin}${href}`;
                }

                return href;
            };

            const candidates = roots.map((root, index) => {
                const name = root.querySelector("h3")?.textContent?.trim() ?? "";
                const phone = root.querySelector("[data-marker='job-application/phone']")?.textContent?.trim() ?? "";
                const ageText = root.querySelector("p[data-marker='undefined/container'] span")?.textContent?.trim() ?? "";
                const resumeLink = root.querySelector("[data-marker='job-application/link/to-resume']");
                const sourceUrl = normalizeUrl(resumeLink?.getAttribute("href") ?? "");
                const vacancyLine = resumeLink?.textContent?.replace(/\s+/g, " ").trim() ?? "";
                const vacancyParts = vacancyLine.split("·").map((x) => x.trim()).filter(Boolean);
                const vacancy = vacancyParts[0] ?? "";
                const city = vacancyParts.length > 1 ? vacancyParts[1] : "";
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                const sourceResponseId = sourceUrl || `${name}|${phone}|${vacancy}|${index}`;

                return {
                    fullName: name,
                    phone,
                    age: ageText,
                    vacancy,
                    city,
                    sourceUrl: sourceUrl || window.location.href,
                    sourceResponseId,
                    rawText
                };
            }).filter((item) => item.fullName && item.phone);

            return {
                url: window.location.href,
                hasCaptcha,
                hasLogin,
                candidates
            };
        })();
        """;
}
