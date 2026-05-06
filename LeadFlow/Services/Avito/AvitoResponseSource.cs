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
        await GlobalLogger.Instance.LogAsync(
            $"Preparing browser session for account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Debug);
        var session = await browserSessionService.CreateSessionAsync(account, cancellationToken);

        await GlobalLogger.Instance.LogAsync(
            $"Creating background browser host for account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Debug);
        await using var host = await backgroundWebViewHostFactory.CreateAsync(cancellationToken);

        await GlobalLogger.Instance.LogAsync(
            $"Attaching browser session for account {account.DisplayName}.",
            DeskLinkAuditLogLevel.Debug);
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
                await GlobalLogger.Instance.LogAsync(
                    $"Navigating to candidates page for account {account.DisplayName} (attempt {attempt}/{maxAttempts}).",
                    DeskLinkAuditLogLevel.Debug);
                await automationService.NavigateAsync(session, CandidatesPageUrl, cancellationToken);
                await WaitForCandidatesPageAsync(session, cancellationToken);
                account.LastAuthCheckAt = DateTime.UtcNow;

                await GlobalLogger.Instance.LogAsync(
                    $"Candidates page loaded for account {account.DisplayName}, starting extraction.",
                    DeskLinkAuditLogLevel.Debug);
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

                await GlobalLogger.Instance.LogAsync(
                    $"Candidate extraction finished for account {account.DisplayName}: parsed {candidates.Count} responses.",
                    DeskLinkAuditLogLevel.Debug);
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

                const chatEl = root.querySelector("[data-marker='job-application/link/to-chat']");
                if (chatEl) {
                    if (chatEl.tagName === "A") {
                        const h = pick(chatEl.getAttribute("href"));
                        if (h) {
                            return h;
                        }
                    }

                    const parentA = chatEl.closest("a");
                    if (parentA) {
                        const h = pick(parentA.getAttribute("href"));
                        if (h) {
                            return h;
                        }
                    }

                    for (const attr of ["data-href", "data-url", "data-to"]) {
                        const h = pick(chatEl.getAttribute(attr));
                        if (h) {
                            return h;
                        }
                    }
                }

                for (const a of root.querySelectorAll("a[href*='messenger']")) {
                    const h = pick(a.getAttribute("href"));
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
                const vacancyUrl = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                const vacancyLine = vacancyListingAnchor?.textContent?.replace(/\s+/g, " ").trim() ?? "";
                const vacancyParts = vacancyLine.split("·").map((x) => x.trim()).filter(Boolean);
                const vacancy = vacancyParts[0] ?? "";
                const city = vacancyParts.length > 1 ? vacancyParts[1] : "";
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                const messengerUrl = resolveMessengerUrl(root);
                const stablePayload = [name, phone, vacancy, city, messengerUrl]
                    .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                    .join("\u001f");
                const sourceResponseId = vacancyUrl || `avito:${fnv1a32Hex(stablePayload)}`;

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