namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// JS-снимки страницы <c>/profile/candidates</c>: подготовка списка (скролл) и извлечение карточек.
/// </summary>
public static class AvitoCandidatesPageScripts
{
    /// <summary>Быстрый детект firewall/капчи (без ожидания списка откликов).</summary>
    public static string BuildFirewallProbeScript() =>
        """
        (() => {
            const itemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;
            const title = (document.title ?? "").trim();
            const bodyText = (document.body?.innerText ?? "").slice(0, 12000);
            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form"
            );
            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha/i.test(title + "\n" + bodyText);
            const blocked =
                itemCount === 0 &&
                statusCount === 0 &&
                (hasFirewallDom || (hasFirewallText && hasCaptchaWidget) || hasFirewallText);

            let kind = "firewall";
            if (blocked && document.getElementById("geetest_captcha")) {
                kind = "geetest";
            } else if (blocked && document.getElementById("inner-captcha")) {
                kind = "image-captcha";
            } else if (blocked && (document.getElementById("h-captcha") || document.querySelector(".h-captcha[data-sitekey]"))) {
                kind = "hCaptcha";
            }

            return JSON.stringify({
                blocked,
                kind,
                title,
                url: window.location.href,
                itemCount,
                readyState: document.readyState
            });
        })();
        """;

    /// <summary>
    /// Снимок готовности списка: complete, нет loader, сигнатура первых карточек (для стабильности после reload / смены суб-профиля).
    /// </summary>
    public static string BuildWaitForReadyProbeScript() =>
        """
        (() => {
            const bodyText = (document.body?.innerText ?? "").trim();
            const bodyLength = bodyText.length;
            const itemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;
            const title = (document.title ?? "").trim();
            const hasFirewallDom = !!document.querySelector(".firewall-container, .js-firewall-form, .firewall-title");
            const blocked =
                itemCount === 0 &&
                statusCount === 0 &&
                (hasFirewallDom || /Доступ\s+ограничен|проблема\s+с\s+IP/i.test(title));

            const hasListData = itemCount > 0 || statusCount > 0;
            const listRoot =
                document.querySelector("[data-marker='job-applications/list']") ||
                document.querySelector("main") ||
                document.body;
            const loading = !hasListData && !!(
                listRoot.querySelector(
                    "[data-marker*='job-application'][data-marker*='loader'], [data-marker='job-applications/loader']"
                ) ||
                listRoot.querySelector("[class*='spinner' i], [class*='loader' i]")
            );

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']")).slice(0, 3);
            const signatureParts = items.map((el) => {
                const name = (el.querySelector("h3, h4")?.textContent ?? "").trim();
                const status =
                    (el.querySelector("[data-marker='job-application/response/status-select-button']")?.textContent ?? "").trim();
                return `${name}@${status}`;
            });
            const listSignature = `${itemCount}|${signatureParts.join(";")}`;

            const emptyConfirmed =
                itemCount === 0 &&
                statusCount === 0 &&
                !loading &&
                (
                    /нет\s+отклик|откликов\s+нет|пока\s+нет|ничего\s+не\s+найдено/i.test(bodyText) ||
                    !!document.querySelector("[data-marker*='empty'], [class*='empty-state' i]")
                );

            const contentReady =
                document.readyState === "complete" &&
                (hasListData || emptyConfirmed || (!loading && itemCount === 0 && statusCount === 0));

            return JSON.stringify({
                readyState: document.readyState,
                bodyLength,
                itemCount,
                statusCount,
                loading,
                listSignature,
                emptyConfirmed,
                blocked,
                contentReady,
                ready: contentReady
            });
        })();
        """;

    /// <summary>Один шаг прокрутки вниз по контейнеру списка откликов.</summary>
    public static string BuildScrollStepScript() =>
        """
        (() => {
            const countItems = () => document.querySelectorAll("[data-marker='job-application/item']").length;
            const findScroller = () => {
                const first = document.querySelector("[data-marker='job-application/item']");
                if (first) {
                    let node = first.parentElement;
                    while (node && node !== document.body) {
                        const style = window.getComputedStyle(node);
                        const overflowY = style.overflowY;
                        if ((overflowY === "auto" || overflowY === "scroll") && node.scrollHeight > node.clientHeight + 40) {
                            return node;
                        }
                        node = node.parentElement;
                    }
                }

                const hints = document.querySelector("[class*='scrollable'], [data-marker='job-applications/list'], main");
                if (hints) {
                    let node = hints;
                    while (node && node !== document.body) {
                        const style = window.getComputedStyle(node);
                        if ((style.overflowY === "auto" || style.overflowY === "scroll") && node.scrollHeight > node.clientHeight + 40) {
                            return node;
                        }
                        node = node.parentElement;
                    }
                }

                return document.scrollingElement || document.documentElement;
            };

            const scroller = findScroller();
            const beforeTop = scroller.scrollTop;
            const delta = Math.max(Math.floor(scroller.clientHeight * 0.9), 500);
            scroller.scrollBy(0, delta);
            const atEnd = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 8;
            return JSON.stringify({
                itemCount: countItems(),
                scrollTop: scroller.scrollTop,
                scrollHeight: scroller.scrollHeight,
                moved: Math.abs(scroller.scrollTop - beforeTop) > 2,
                atEnd
            });
        })();
        """;

    public static string BuildScrollToTopScript() =>
        """
        (() => {
            const first = document.querySelector("[data-marker='job-application/item']");
            let scroller = document.scrollingElement || document.documentElement;
            if (first) {
                let node = first.parentElement;
                while (node && node !== document.body) {
                    const style = window.getComputedStyle(node);
                    if ((style.overflowY === "auto" || style.overflowY === "scroll") && node.scrollHeight > node.clientHeight + 40) {
                        scroller = node;
                        break;
                    }
                    node = node.parentElement;
                }
            }
            scroller.scrollTop = 0;
            return JSON.stringify({ ok: true });
        })();
        """;

    /// <summary>Телефон раскрыт: в кнопке нет маски «**» и достаточно цифр для РФ-номера.</summary>
    public static string BuildPhonesReadyProbeScript() =>
        """
        (() => {
            const isRevealedPhone = (raw) => {
                const text = (raw ?? "").trim();
                if (!text || /\*/.test(text)) {
                    return false;
                }

                const digits = text.replace(/\D/g, "");
                return digits.length >= 10;
            };

            const getPhoneRaw = (item) =>
                item.querySelector("[data-marker='job-application/phone']")?.textContent ??
                item.querySelector("[data-marker='job-application/call-button']")?.textContent ??
                "";

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            if (items.length === 0) {
                return JSON.stringify({ ready: false, items: 0, withPhone: 0, masked: 0 });
            }

            let withPhone = 0;
            let masked = 0;
            for (const item of items) {
                const raw = getPhoneRaw(item);
                if (/\*/.test(raw)) {
                    masked++;
                    continue;
                }

                if (isRevealedPhone(raw)) {
                    withPhone++;
                }
            }

            const ratio = withPhone / items.length;
            return JSON.stringify({
                ready: ratio >= 0.92 || (items.length <= 3 && withPhone === items.length),
                items: items.length,
                withPhone,
                masked
            });
        })();
        """;

    /// <summary>Блокирует копирование в буфер на странице (клик «телефон» на Avito часто вызывает copy).</summary>
    public static string BuildEnableClipboardGuardScript() =>
        """
        (() => {
            if (window.__leadflowClipboardGuard) {
                return JSON.stringify({ ok: true, already: true });
            }

            const guard = { orig: {} };
            const clip = navigator.clipboard;
            if (clip) {
                if (typeof clip.writeText === "function") {
                    guard.orig.writeText = clip.writeText.bind(clip);
                    clip.writeText = async () => {};
                }

                if (typeof clip.write === "function") {
                    guard.orig.write = clip.write.bind(clip);
                    clip.write = async () => {};
                }
            }

            guard.origExecCommand = document.execCommand.bind(document);
            document.execCommand = function (cmd, ...args) {
                if (String(cmd ?? "").toLowerCase() === "copy") {
                    return true;
                }

                return guard.origExecCommand(cmd, ...args);
            };

            guard.copyHandler = (event) => {
                event.preventDefault();
                event.stopImmediatePropagation();
            };
            document.addEventListener("copy", guard.copyHandler, true);
            window.__leadflowClipboardGuard = guard;
            return JSON.stringify({ ok: true });
        })();
        """;

    public static string BuildDisableClipboardGuardScript() =>
        """
        (() => {
            const guard = window.__leadflowClipboardGuard;
            if (!guard) {
                return JSON.stringify({ ok: true });
            }

            const clip = navigator.clipboard;
            if (clip) {
                if (guard.orig.writeText) {
                    clip.writeText = guard.orig.writeText;
                }

                if (guard.orig.write) {
                    clip.write = guard.orig.write;
                }
            }

            if (guard.origExecCommand) {
                document.execCommand = guard.origExecCommand;
            }

            if (guard.copyHandler) {
                document.removeEventListener("copy", guard.copyHandler, true);
            }

            delete window.__leadflowClipboardGuard;
            return JSON.stringify({ ok: true });
        })();
        """;

    /// <summary>Клик по кнопкам с замаскированным номером, чтобы Avito подставил полный телефон.</summary>
    public static string BuildRevealMaskedPhonesStepScript() =>
        """
        (() => {
            const pickPhoneClickTarget = (btn) => {
                const text = btn.querySelector(".styles-module-text");
                if (text) {
                    return text;
                }

                for (const span of btn.querySelectorAll("span")) {
                    if (!span.className.includes("styles-module-icon")) {
                        return span;
                    }
                }

                return btn;
            };

            const getPhoneButton = (item) =>
                item.querySelector("[data-marker='job-application/phone']") ??
                item.querySelector("[data-marker='job-application/call-button']");

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let masked = 0;
            let clicked = 0;
            for (const item of items) {
                const btn = getPhoneButton(item);
                if (!btn) {
                    continue;
                }

                const raw = btn.textContent ?? "";
                if (!/\*/.test(raw)) {
                    continue;
                }

                masked++;
                try {
                    btn.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                const target = pickPhoneClickTarget(btn);
                try {
                    target.click();
                    clicked++;
                } catch {
                }
            }

            return JSON.stringify({ items: items.length, masked, clicked });
        })();
        """;

    public static string BuildExtractionScript() =>
        """
        (() => {
            const itemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;
            const title = (document.title ?? "").trim();
            const bodyText = document.body?.innerText ?? "";
            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form"
            );
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha/i.test(title + "\n" + bodyText);
            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasCaptcha =
                (itemCount === 0 && statusCount === 0 && (hasFirewallDom || (hasFirewallText && hasCaptchaWidget) || hasFirewallText)) ||
                (!hasFirewallDom && /капч|captcha|подтвердите|проверочный код/i.test(bodyText));

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
            const getNameNode = (root) => root?.querySelector("h3, h4");
            const getPhoneNode = (root) =>
                root?.querySelector("[data-marker='job-application/phone']") ??
                root?.querySelector("[data-marker='job-application/call-button']");

            const findCardRoot = (element) => {
                let current = element;
                while (current) {
                    const name = getNameNode(current);
                    const phone = getPhoneNode(current);
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

                const name = getNameNode(root);
                const phone = getPhoneNode(root);
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

            // Все карточки в DOM (после полного скролла) — status-кнопок может быть меньше, чем item.
            for (const item of document.querySelectorAll("[data-marker='job-application/item']")) {
                addRoot(item);
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

            const parseVacancyAndCity = (root, vacancyListingAnchor) => {
                const fromAnchor = (vacancyListingAnchor?.textContent ?? "").replace(/\s+/g, " ").trim();
                if (fromAnchor) {
                    const vacancyParts = fromAnchor.split("·").map((x) => x.trim()).filter(Boolean);
                    return {
                        vacancy: vacancyParts[0] ?? "",
                        city: vacancyParts.length > 1 ? vacancyParts[1] : ""
                    };
                }

                const lines = Array.from(root.querySelectorAll("p"))
                    .map((x) => (x.textContent ?? "").replace(/\s+/g, " ").trim())
                    .filter(Boolean);
                for (const line of lines) {
                    const match = line.match(/·\s*«([^»]+)»\s*·\s*([^·]+)/);
                    if (match) {
                        return {
                            vacancy: (match[1] ?? "").trim(),
                            city: (match[2] ?? "").trim()
                        };
                    }
                }

                return { vacancy: "", city: "" };
            };

            const parseAgeText = (root, rawText) => {
                const oldAge = root.querySelector("p[data-marker='undefined/container'] span")?.textContent?.trim();
                if (oldAge) {
                    return oldAge;
                }

                const ageMatch = rawText.match(/(?:^|\s|·)(\d{1,2})\s*г(?:ода|од|лет)\b/i);
                return ageMatch ? `${ageMatch[1]} лет` : "";
            };

            const candidates = roots.map((root) => {
                const name = getNameNode(root)?.textContent?.trim() ?? "";
                const phone = getPhoneNode(root)?.textContent?.trim() ?? "";
                const vacancyListingAnchor = root.querySelector("[data-marker='job-application/link/to-resume']");
                let vacancyUrl = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                if (/\/profile\/candidates(?:[/?#]|$)/i.test(vacancyUrl)) {
                    vacancyUrl = "";
                }
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                const ageText = parseAgeText(root, rawText);
                const vacancyAndCity = parseVacancyAndCity(root, vacancyListingAnchor);
                const vacancy = vacancyAndCity.vacancy;
                const city = vacancyAndCity.city;
                const messengerUrl = resolveMessengerUrl(root);
                const stablePayload = [name, phone, vacancy, city, vacancyUrl, messengerUrl]
                    .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                    .join("\u001f");
                const sourceResponseId = `avito:${fnv1a32Hex(stablePayload)}`;

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
            }).filter((item) => {
                if (!item.fullName || !item.phone) {
                    return false;
                }

                if (/\*/.test(item.phone)) {
                    return false;
                }

                return item.phone.replace(/\D/g, "").length >= 10;
            });

            return {
                url: window.location.href,
                hasCaptcha,
                hasLogin,
                candidates,
                domItemCount: document.querySelectorAll("[data-marker='job-application/item']").length,
                domStatusCount: statusButtons.length
            };
        })();
        """;

    /// <summary>
    /// То же извлечение, но как одно выражение для Puppeteer <c>EvaluateExpressionAsync</c> (без лишней <c>;</c> внутри <c>JSON.stringify(...)</c>).
    /// </summary>
    public static string BuildExtractionScriptForPuppeteer() =>
        "JSON.stringify(" + TrimTrailingStatementSemicolon(BuildExtractionScript()) + ")";

    private static string TrimTrailingStatementSemicolon(string script)
    {
        var trimmed = script.Trim();
        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }
}
