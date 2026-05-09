using System.Text.Json;
using LeadFlow.Data;
using LeadFlow.Logging.Audit;
using LeadFlow.Models;
using LeadFlow.Services.Browser;

namespace LeadFlow.Services.Avito;

public sealed class AvitoResponseSource(
    AppRepository repository,
    IBrowserSessionService browserSessionService,
    IBackgroundWebViewHostFactory backgroundWebViewHostFactory,
    IWebPageAutomationService automationService) : IAvitoResponseSource
{
    public const string CandidatesPageUrl = "https://www.avito.ru/profile/candidates";

    public async Task<IReadOnlyList<CandidateResponse>> GetNewResponsesAsync(AvitoAccount account, AppSettings settings, CancellationToken cancellationToken)
    {
        if (account.ProfileProvider == AvitoProfileProvider.AdsPower && !settings.DemoModeEnabled)
        {
            account.LastErrorMessage =
                "Аккаунт AdsPower: автоматический опрос откликов через встроенный браузер недоступен. Используйте локальный профиль или демо-режим.";
            return [];
        }

        var session = await browserSessionService.CreateSessionAsync(account, cancellationToken);

        await using var host = await backgroundWebViewHostFactory.CreateAsync(cancellationToken);

        await host.AttachAsync(session, cancellationToken);

        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var pause = attempt == 2 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
                _ = GlobalLogger.Instance.LogAsync(
                    $"Candidates page fetch retry {attempt}/{maxAttempts} for {account.DisplayName} after {pause.TotalSeconds:F0} s.",
                    DeskLinkAuditLogLevel.Info);
                await Task.Delay(pause, cancellationToken);
            }

            try
            {
                await automationService.NavigateAsync(session, CandidatesPageUrl, cancellationToken);
                await WaitForCandidatesPageAsync(session, cancellationToken);
                account.LastAuthCheckAt = DateTime.UtcNow;

                var raw = await automationService.ExecuteScriptAsync(session, BuildExtractionScript(), cancellationToken);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    _ = GlobalLogger.Instance.LogAsync(
                        $"Empty extraction script result for {account.DisplayName} (attempt {attempt}/{maxAttempts}).",
                        DeskLinkAuditLogLevel.Warning);
                    if (attempt == maxAttempts)
                    {
                        account.LastErrorMessage = "Пустой ответ скрипта со страницы кандидатов после нескольких попыток";
                        return [];
                    }

                    continue;
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
                var candidates = AvitoCandidatesJsonParser.ParseCandidates(root, account);

                var existingIds = await repository.GetExistingSourceResponseIdsAsync(
                    candidates.Select(x => x.SourceResponseId),
                    cancellationToken);

                return candidates
                    .Where(x => !existingIds.Contains(x.SourceResponseId))
                    .OrderByDescending(x => x.CreatedAt)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"JSON parse error on candidates extraction for {account.DisplayName} (attempt {attempt}/{maxAttempts}): {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Некорректный JSON ответа страницы кандидатов после нескольких попыток";
                    return [];
                }
            }
            catch (TimeoutException ex)
            {
                _ = GlobalLogger.Instance.LogAsync(
                    $"Timeout waiting for candidates page for {account.DisplayName} (attempt {attempt}/{maxAttempts}): {ex.Message}",
                    DeskLinkAuditLogLevel.Warning);
                if (attempt == maxAttempts)
                {
                    account.LastErrorMessage = "Таймаут загрузки страницы кандидатов после нескольких попыток";
                    return [];
                }
            }
        }

        account.LastErrorMessage = "Не удалось получить отклики со страницы кандидатов после нескольких попыток";
        return [];
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

        throw new TimeoutException("Таймаут загрузки страницы кандидатов Авито.");
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
                const root = button.closest?.("[data-marker='job-application/item']") ?? findCardRoot(button);
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

                const t = href.trim();
                if (!t || t === "#") {
                    return "";
                }

                if (t.startsWith("//")) {
                    return `https:${t}`;
                }

                if (t.startsWith("/")) {
                    return `${window.location.origin}${t}`;
                }

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

                        // Plain URL in attribute.
                        const direct = pick(raw);
                        if (direct && /(messenger|chat|dialog)/i.test(direct)) {
                            return direct;
                        }

                        // URL embedded into JSON/text attributes.
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
                        if (h) {
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
                /* Вакансия: якорь «название · город», не «Резюме» (job-crm/response/cv-button). */
                const vacancyListingAnchor = root.querySelector("[data-marker='job-application/link/to-resume']");
                let vacancyUrl = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                if (/\/profile\/candidates(?:[/?#]|$)/i.test(vacancyUrl)) {
                    vacancyUrl = "";
                }
                const vacancyLine = vacancyListingAnchor?.textContent?.replace(/\s+/g, " ").trim() ?? "";
                const vacancyParts = vacancyLine.split("·").map((x) => x.trim()).filter(Boolean);
                const vacancy = vacancyParts[0] ?? "";
                const city = vacancyParts.length > 1 ? vacancyParts[1] : "";
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                const messengerUrl = resolveMessengerUrl(root);
                const stablePayload = [name, phone, vacancy, city, vacancyUrl, messengerUrl]
                    .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                    .join("\u001f");
                // vacancyUrl is often shared by multiple candidates for the same job;
                // prefer a per-dialog link and fall back to a stable content hash.
                const sourceResponseId = messengerUrl || `avito:${fnv1a32Hex(stablePayload)}`;

                return {
                    fullName: name,
                    phone,
                    age: ageText,
                    vacancy,
                    city,
                    vacancyUrl,
                    messengerUrl,
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