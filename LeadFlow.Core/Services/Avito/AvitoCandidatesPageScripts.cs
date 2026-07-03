namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// JS-снимки страниц откликов Avito (<c>/profile/candidates</c> и CRM <c>/profile/job/responses</c>).
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
                document.querySelector(".styles-page-cyvKh") ||
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

                const hints = document.querySelector(
                    "[class*='scrollable'], [data-marker='job-applications/list'], .styles-page-cyvKh, main"
                );
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

    /// <summary>Клик по карточке отклика в списке (открывает панель «Данные» справа).</summary>
    public static string BuildClickCandidateItemByIndexScript(int index) =>
        $$"""
        (() => {
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const index = {{index}};
            if (index < 0 || index >= items.length) {
                return JSON.stringify({ ok: false, reason: "index_out_of_range", items: items.length, index });
            }

            const item = items[index];
            try {
                item.scrollIntoView({ block: "center", inline: "nearest" });
            } catch {
            }

            try {
                item.click();
                return JSON.stringify({ ok: true, index, items: items.length });
            } catch (error) {
                return JSON.stringify({ ok: false, reason: String(error), index, items: items.length });
            }
        })();
        """;

    /// <summary>Снимок открытой панели отклика: ссылка «на вакансию», возраст и т.д.</summary>
    public static string BuildReadDetailPanelScript() =>
        """
        (() => {
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
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

            const phoneEl =
                document.querySelector("[data-marker='job-application/call-button']") ??
                document.querySelector("[data-marker='job-application/phone']");
            const phone = normalize(phoneEl?.textContent ?? "");
            const phoneDigits = phone.replace(/\D/g, "");

            const responseRoot =
                phoneEl?.closest?.("[class*='styles-module-response']") ??
                document.querySelector("[class*='styles-module-response']");
            const searchRoot = responseRoot ?? document;

            let vacancyUrl = "";
            let vacancy = "";
            let city = "";
            for (const paragraph of searchRoot.querySelectorAll("p")) {
                const text = normalize(paragraph.textContent);
                if (!/на вакансию/i.test(text)) {
                    continue;
                }

                const anchor = paragraph.querySelector("a[href]");
                if (!anchor) {
                    continue;
                }

                const href = normalizeUrl(anchor.getAttribute("href") ?? "");
                if (!href || !/\/\d{5,}/.test(href)) {
                    continue;
                }

                vacancyUrl = href;
                vacancy = normalize(anchor.textContent);
                const tail = text.slice(text.toLowerCase().indexOf(anchor.textContent.toLowerCase()) + anchor.textContent.length);
                const cityParts = tail.split(/[·]/).map((x) => x.trim()).filter(Boolean);
                if (cityParts.length > 0) {
                    city = cityParts[cityParts.length - 1];
                }

                break;
            }

            const agePattern = /(\d{1,2})\s*(?:лет|года|год)/i;
            let age = "";
            for (const paragraph of searchRoot.querySelectorAll("p")) {
                const match = normalize(paragraph.textContent).match(agePattern);
                if (match) {
                    age = `${match[1]} лет`;
                    break;
                }
            }

            return JSON.stringify({
                phoneDigits,
                phone,
                vacancyUrl,
                vacancy,
                city,
                age,
                hasPanel: !!responseRoot
            });
        })();
        """;

    public static string BuildApplyDetailEnrichmentScript(string enrichmentJson) =>
        $"window.__leadflowDetailEnrichment = {enrichmentJson}; JSON.stringify({{ ok: true, count: Object.keys(window.__leadflowDetailEnrichment || {{}}).length }});";

    /// <summary>Телефоны из списка карточек (без клика в детальную панель).</summary>
    public static string BuildCollectListItemPhonesScript() =>
        """
        (() => {
            const readPhone = (item) => {
                const raw =
                    item.querySelector("[data-marker='job-application/phone']")?.textContent ??
                    item.querySelector("[data-marker='job-application/call-button']")?.textContent ??
                    "";
                return (raw ?? "").replace(/\D/g, "");
            };

            const normalizePhoneKey = (digits) => {
                if (!digits) {
                    return "";
                }

                if (digits.length === 11 && digits.startsWith("8")) {
                    return `7${digits.slice(1)}`;
                }

                if (digits.length === 10) {
                    return `7${digits}`;
                }

                return digits;
            };

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            return JSON.stringify(
                items.map((item, index) => ({
                    index,
                    phoneDigits: normalizePhoneKey(readPhone(item))
                }))
            );
        })();
        """;

    /// <summary>Мини-панель в углу или полноэкранный канал после клика «Перейти в чат».</summary>
    public static string BuildMessengerUiVisibleExpression() =>
        """
        () => !!document.querySelector("[data-marker='messagesHistory/list']")
            || !!document.querySelector("a[data-marker='mini-messenger/messenger-page-link']")
            || /\/profile\/messenger\/channel\//i.test(window.location.href)
        """;

    /// <summary>URL канала: из шапки мини-чата или из адреса полноэкранного мессенджера.</summary>
    public static string BuildResolveMessengerChannelUrlExpression() =>
        """
        (() => {
            const mini = document.querySelector("a[data-marker='mini-messenger/messenger-page-link']");
            const miniHref = (mini?.href ?? "").trim();
            if (miniHref && /\/profile\/messenger\//i.test(miniHref)) {
                return miniHref;
            }

            const url = (window.location.href ?? "").trim();
            if (/\/profile\/messenger\/channel\//i.test(url)) {
                return url;
            }

            return "";
        })()
        """;

    /// <summary>Прокрутка истории мини-чата и сбор сообщений (после клика «Перейти в чат»).</summary>
    public static string BuildScrollAndCollectMiniMessengerMessagesScript() =>
        """
        (() => {
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
            const list = document.querySelector("[data-marker='messagesHistory/list']");
            if (!list) {
                return JSON.stringify({ ok: false, reason: "no_messages_list", messages: [] });
            }

            const countMessages = () => list.querySelectorAll("[data-marker='message']").length;
            let lastCount = countMessages();
            let stableRounds = 0;
            for (let round = 0; round < 24; round++) {
                const prevTop = list.scrollTop;
                list.scrollTop = 0;
                if (Math.abs(list.scrollTop - prevTop) < 1) {
                    list.scrollTop = Math.max(0, list.scrollHeight - list.clientHeight);
                }

                const count = countMessages();
                if (count === lastCount) {
                    stableRounds++;
                    if (stableRounds >= 3) {
                        break;
                    }
                } else {
                    stableRounds = 0;
                    lastCount = count;
                }
            }

            const readText = (message) => {
                const direct = message.querySelector("[data-marker='messageText']");
                if (direct) {
                    return normalize(direct.innerText ?? direct.textContent ?? "");
                }

                const platform = message.querySelector("[data-marker='platformMessage/text']");
                if (platform) {
                    return normalize(platform.innerText ?? platform.textContent ?? "");
                }

                return "";
            };

            const messages = [];
            for (const message of list.querySelectorAll("[data-marker='message']")) {
                const text = readText(message);
                if (!text) {
                    continue;
                }

                const className = message.className ?? "";
                const side = className.includes("message-base-module-right") ? "right" : "left";
                const isPlatform = !!message.querySelector("[data-marker='platformMessage/text']");
                const timeEl = message.querySelector("time[datetime]");
                const at = timeEl?.getAttribute("datetime") ?? "";

                messages.push({ text, at, side, isPlatform });
            }

            return JSON.stringify({ ok: true, messages, count: messages.length });
        })();
        """;

    /// <summary>Есть ли на карточке признак непрочитанного чата (бейдж/точка у кнопки «Перейти в чат»).</summary>
    public static string BuildReadCandidateChatUnreadScript(int index) =>
        $$"""
        (() => {
            const idx = {{index}};
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const item = items[idx];
            if (!item) {
                return JSON.stringify({ ok: false, unread: false });
            }

            const chat = item.querySelector("[data-marker='job-application/link/to-chat']");
            if (!chat) {
                return JSON.stringify({ ok: true, unread: false, hasChatButton: false });
            }

            const aria = (chat.getAttribute("aria-label") ?? "").toLowerCase();
            if (/непрочит|нов(ое|ые|ых)?\s+сообщ|unread/i.test(aria)) {
                return JSON.stringify({ ok: true, unread: true, hasChatButton: true });
            }

            const contacts = item.querySelector("[class*='styles-module-contacts']") ?? item;
            const dotSelectors = [
                "[class*='pulse-dot'][class*='dot_red']",
                "[class*='dot_red']",
                "[class*='badge']",
                "[class*='unread']"
            ];
            for (const selector of dotSelectors) {
                const hit = contacts.querySelector(selector);
                if (hit && hit !== chat) {
                    return JSON.stringify({ ok: true, unread: true, hasChatButton: true });
                }
            }

            return JSON.stringify({ ok: true, unread: false, hasChatButton: true });
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
            const getVacancyAnchor = (root) =>
                root?.querySelector("[data-marker='job-application/link/to-resume']");

            const findCardRoot = (element) => {
                const itemRoot = element?.closest?.("[data-marker='job-application/item']");
                if (itemRoot && getNameNode(itemRoot) && getPhoneNode(itemRoot)) {
                    return itemRoot;
                }

                let current = element;
                while (current) {
                    const name = getNameNode(current);
                    const phone = getPhoneNode(current);
                    if (name && phone) {
                        return current;
                    }

                    current = current.parentElement;
                }

                return itemRoot ?? null;
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

            const normalizePhoneKey = (phone) => {
                let digits = (phone ?? "").replace(/\D/g, "");
                if (digits.length === 11 && digits.startsWith("8")) {
                    digits = `7${digits.slice(1)}`;
                } else if (digits.length === 10) {
                    digits = `7${digits}`;
                }

                return digits;
            };

            const buildSourceResponseId = (name, phone, vacancy, city, vacancyUrl) => {
                const phoneKey = normalizePhoneKey(phone);
                const vacancyIdMatch = (vacancyUrl ?? "").match(/\/(\d{5,})(?:\?|$|\/)/);
                if (vacancyIdMatch && phoneKey.length >= 10) {
                    return `avito:${vacancyIdMatch[1]}:${phoneKey}`;
                }

                const stablePayload = [name, phone, vacancy, city]
                    .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                    .join("\u001f");
                return `avito:${fnv1a32Hex(stablePayload)}`;
            };

            const parseVacancyLink = (root, vacancyListingAnchor) => {
                const directHref = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                if (
                    directHref
                    && /\/\d{5,}/.test(directHref)
                    && !/\/profile\/candidates(?:[/?#]|$)/i.test(directHref)
                ) {
                    return directHref;
                }

                for (const paragraph of root.querySelectorAll("p")) {
                    const text = (paragraph.textContent ?? "").replace(/\s+/g, " ").trim();
                    if (!/на вакансию/i.test(text)) {
                        continue;
                    }

                    const anchor = paragraph.querySelector("a[href]");
                    if (!anchor) {
                        continue;
                    }

                    const href = normalizeUrl(anchor.getAttribute("href") ?? "");
                    if (href && /\/\d{5,}/.test(href)) {
                        return href;
                    }
                }

                const legacyHref = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                if (legacyHref && !/\/profile\/candidates(?:[/?#]|$)/i.test(legacyHref) && /\/\d{5,}/.test(legacyHref)) {
                    return legacyHref;
                }

                return "";
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

                // Новый формат карточки: «Мужчина · 54 года · …» / «Женщина · 37 лет · …».
                // \b в JS не работает с кириллицей; «лет» — отдельное слово, не «г»+«лет».
                const agePattern = /(\d{1,2})\s*(?:лет|года|год)/i;
                const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();

                for (const line of Array.from(root.querySelectorAll("p"))) {
                    const match = normalize(line.textContent).match(agePattern);
                    if (match) {
                        return `${match[1]} лет`;
                    }
                }

                const ageMatch = normalize(rawText).match(agePattern);
                return ageMatch ? `${ageMatch[1]} лет` : "";
            };

            const listItems = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const candidates = roots.map((root) => {
                const name = getNameNode(root)?.textContent?.trim() ?? "";
                const phone = getPhoneNode(root)?.textContent?.trim() ?? "";
                const vacancyListingAnchor = getVacancyAnchor(root);
                let vacancyUrl = parseVacancyLink(root, vacancyListingAnchor);
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                let ageText = parseAgeText(root, rawText);
                const vacancyAndCity = parseVacancyAndCity(root, vacancyListingAnchor);
                let vacancy = vacancyAndCity.vacancy;
                let city = vacancyAndCity.city;

                const phoneKey = phone.replace(/\D/g, "");
                const rootIndex = listItems.indexOf(root);
                const enriched =
                    (typeof window !== "undefined" && window.__leadflowDetailEnrichment)
                        ? (window.__leadflowDetailEnrichment[phoneKey] ?? window.__leadflowDetailEnrichment[String(rootIndex)])
                        : null;
                if (enriched) {
                    if (enriched.vacancyUrl) {
                        vacancyUrl = enriched.vacancyUrl;
                    }

                    if (enriched.vacancy) {
                        vacancy = enriched.vacancy;
                    }

                    if (enriched.city) {
                        city = enriched.city;
                    }

                    if (enriched.age && !ageText) {
                        ageText = enriched.age;
                    }
                }
                const messengerUrl = resolveMessengerUrl(root);
                const sourceResponseId = buildSourceResponseId(name, phone, vacancy, city, vacancyUrl);

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

            const isJobCrmResponsesPage = !!(
                document.querySelector("[data-marker='filters/status-list-content']")
                || document.querySelector("[data-marker='job-crm/response/cv-button']")
            );

            return {
                url: window.location.href,
                hasCaptcha,
                hasLogin,
                candidates,
                pageVariant: isJobCrmResponsesPage ? "job-crm" : "legacy",
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
