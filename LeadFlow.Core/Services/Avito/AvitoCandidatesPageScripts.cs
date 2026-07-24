namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// JS-снимки страниц откликов Avito (<c>/profile/candidates</c> и CRM <c>/profile/job/responses</c>).
/// </summary>
public static class AvitoCandidatesPageScripts
{
    /// <summary>Общие хелперы: кэш раскрытых номеров и чтение popup «Временный номер».</summary>
    private const string ContactsPhoneHelpersJs =
        """
        const initRevealedPhonesStore = () => {
            if (!window.__leadflowRevealedPhones || typeof window.__leadflowRevealedPhones !== "object") {
                window.__leadflowRevealedPhones = {};
            }

            return window.__leadflowRevealedPhones;
        };

        const normalizePhoneText = (text) => (text ?? "").replace(/\s+/g, " ").trim();

        const isRevealedPhoneText = (raw) => {
            const text = normalizePhoneText(raw);
            if (!text || /\*/.test(text)) {
                return false;
            }

            const digits = text.replace(/\D/g, "");
            return digits.length >= 10;
        };

        const getCachedPhone = (index) => {
            const store = window.__leadflowRevealedPhones;
            if (!store || typeof store !== "object") {
                return "";
            }

            return normalizePhoneText(store[String(index)] ?? store[index] ?? "");
        };

        const readInlinePhone = (item) => {
            const phoneEl = item.querySelector("[data-marker='job-application/phone']");
            if (!phoneEl) {
                return "";
            }

            return normalizePhoneText(phoneEl.textContent ?? "");
        };

        const readItemPhone = (item, index) => {
            const cached = getCachedPhone(index);
            if (isRevealedPhoneText(cached)) {
                return cached;
            }

            const inline = readInlinePhone(item);
            if (isRevealedPhoneText(inline)) {
                return inline;
            }

            const callBtn = item.querySelector("[data-marker='job-application/call-button']");
            if (callBtn) {
                const callText = normalizePhoneText(callBtn.textContent ?? "");
                if (isRevealedPhoneText(callText)) {
                    return callText;
                }
            }

            return "";
        };

        const shouldSkipPhoneReveal = (index) => {
            const skips = window.__leadflowSkipPhoneReveal;
            if (!skips || typeof skips !== "object") {
                return false;
            }

            return !!(skips[String(index)] || skips[index]);
        };

        const needsPhoneReveal = (item, index) =>
            !shouldSkipPhoneReveal(index) && !isRevealedPhoneText(readItemPhone(item, index));

        const readContactsPopupPhone = () => {
            const popup = document.querySelector("[data-marker='job-application/response/contacts-popup/popup']");
            if (!popup) {
                return "";
            }

            const paragraphs = Array.from(popup.querySelectorAll("p"));
            for (const paragraph of paragraphs) {
                if (!/временн(?:ый|ого)?\s+номер/i.test(normalizePhoneText(paragraph.textContent))) {
                    continue;
                }

                let section = paragraph.parentElement;
                while (section && section !== popup) {
                    for (const h3 of section.querySelectorAll("h3")) {
                        const text = normalizePhoneText(h3.textContent ?? "");
                        if (isRevealedPhoneText(text)) {
                            return text;
                        }
                    }

                    section = section.parentElement;
                }
            }

            for (const h3 of popup.querySelectorAll("h3")) {
                const text = normalizePhoneText(h3.textContent ?? "");
                if (isRevealedPhoneText(text)) {
                    return text;
                }
            }

            return "";
        };

        const hasContactsPopupLoadError = () => {
            const popup = document.querySelector("[data-marker='job-application/response/contacts-popup/popup']");
            const scope = popup ?? document;
            return /не\s+удалось\s+загрузить\s+контактные\s+данные/i.test(scope.textContent ?? "");
        };

        const isContactsPopupOpen = () =>
            !!document.querySelector("[data-marker='job-application/response/contacts-popup/popup']")
            || hasContactsPopupLoadError();

        const closeContactsPopup = () => {
            const closeBtn = document.querySelector("[data-marker='job-application/response/contacts-popup/close']");
            if (closeBtn) {
                closeBtn.click();
                return true;
            }

            document.dispatchEvent(new KeyboardEvent("keydown", {
                key: "Escape",
                code: "Escape",
                bubbles: true,
                cancelable: true
            }));
            return false;
        };

        const clickPhoneRevealTarget = (item) => {
            const callBtn = item.querySelector("[data-marker='job-application/call-button']");
            if (callBtn) {
                try {
                    callBtn.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                callBtn.click();
                return { clicked: true, kind: "call-button" };
            }

            const phoneBtn = item.querySelector("[data-marker='job-application/phone']");
            const raw = phoneBtn?.textContent ?? "";
            if (phoneBtn && /\*/.test(raw)) {
                try {
                    phoneBtn.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                const text = phoneBtn.querySelector(".styles-module-text");
                (text ?? phoneBtn).click();
                return { clicked: true, kind: "masked-phone" };
            }

            return { clicked: false, kind: "none" };
        };

        const waitForContactsPopup = (timeoutMs) => {
            const started = Date.now();
            while (Date.now() - started < timeoutMs) {
                if (isContactsPopupOpen()) {
                    if (hasContactsPopupLoadError()) {
                        return "error";
                    }

                    const phone = readContactsPopupPhone();
                    if (phone) {
                        return "ready";
                    }
                }

                const sliceEnd = Date.now() + 50;
                while (Date.now() < sliceEnd) {
                }
            }

            if (hasContactsPopupLoadError()) {
                return "error";
            }

            if (isContactsPopupOpen()) {
                const phone = readContactsPopupPhone();
                return phone ? "ready" : "timeout";
            }

            return "timeout";
        };
        """;

    /// <summary>
    /// Возраст из текста карточки/панели: не путать «опыт N лет» / «стаж N лет» с возрастом.
    /// </summary>
    private const string AgeParseHelpersJs =
        """
        const normalizeAgeSourceText = (text) => (text ?? "").replace(/\s+/g, " ").trim();

        const isExperienceYearsContext = (text, matchIndex) => {
            const before = text.slice(Math.max(0, matchIndex - 40), matchIndex);
            return /(?:опыт(?:\s+работы)?|стаж)\s*[:\-]?\s*$/i.test(before);
        };

        const extractAgeYearsFromText = (text) => {
            const normalized = normalizeAgeSourceText(text);
            if (!normalized) {
                return "";
            }

            const explicit = normalized.match(/возраст\s*[—\-:]\s*(\d{1,2})\b/i);
            if (explicit) {
                return `${explicit[1]} лет`;
            }

            const demographic = normalized.match(/(?:мужчина|женщина)\s*[·•|,]?\s*(\d{1,2})\s*(?:лет|года|год)/i);
            if (demographic) {
                return `${demographic[1]} лет`;
            }

            const agePattern = /(\d{1,2})\s*(?:лет|года|год)/gi;
            let match;
            while ((match = agePattern.exec(normalized)) !== null) {
                if (isExperienceYearsContext(normalized, match.index)) {
                    continue;
                }

                return `${match[1]} лет`;
            }

            return "";
        };
        """;

    private const string CardFingerprintJs =
        """
        const normalizeCardText = (text) => (text ?? "").replace(/\s+/g, " ").trim();

        const fnv1a32HexCard = (text) => {
            let h = 2166136261 >>> 0;
            for (let i = 0; i < text.length; i++) {
                h ^= text.charCodeAt(i);
                h = Math.imul(h, 16777619) >>> 0;
            }

            return h.toString(16);
        };

            const extractVacancyIdFromUrl = (vacancyUrl) => {
                const match = (vacancyUrl ?? "").trim().match(/(?:\/|_)(\d{5,})(?:\?|$|\/|$)/);
                return match ? match[1] : "";
            };

        const extractMessengerChannelKey = (messengerUrl) => {
            const match = (messengerUrl ?? "").trim().match(/\/profile\/messenger\/channel\/([^/?#]+)/i);
            return match ? match[1].trim().toLowerCase() : "";
        };

        const buildCardFingerprint = (fullName, vacancy, city, vacancyUrl, messengerUrl, ageText) => {
            const messengerKey = extractMessengerChannelKey(messengerUrl);
            if (messengerKey) {
                return `avito-card:msg:${messengerKey}`;
            }

            const vacancyId = extractVacancyIdFromUrl(vacancyUrl);
            const payload = [
                normalizeCardText(fullName),
                vacancyId || normalizeCardText(vacancy),
                normalizeCardText(city),
                normalizeCardText(ageText ?? "")
            ].join("\u001f");

            return `avito-card:${fnv1a32HexCard(payload)}`;
        };
        """;

    private const string SourceResponseIdJs =
        """
        const normalizePhoneKeyForSourceId = (phone) => {
            let digits = (phone ?? "").replace(/\D/g, "");
            if (digits.length === 11 && digits.startsWith("8")) {
                digits = `7${digits.slice(1)}`;
            } else if (digits.length === 10) {
                digits = `7${digits}`;
            }

            return digits;
        };

        const buildSourceResponseId = (name, phone, vacancy, city, vacancyUrl) => {
            const phoneKey = normalizePhoneKeyForSourceId(phone);
            const vacancyIdMatch = (vacancyUrl ?? "").match(/(?:\/|_)(\d{5,})(?:\?|$|\/)/);
            if (vacancyIdMatch && phoneKey.length >= 10) {
                return `avito:${vacancyIdMatch[1]}:${phoneKey}`;
            }

            const stablePayload = [name, phone, vacancy, city]
                .map((x) => (x ?? "").trim().replace(/\s+/g, " "))
                .join("\u001f");
            return `avito:${fnv1a32HexCard(stablePayload)}`;
        };
        """;

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

    /// <summary>Телефон раскрыт: inline, в кэше после popup или в кнопке без маски «**».</summary>
    public static string BuildPhonesReadyProbeScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            initRevealedPhonesStore();

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            if (items.length === 0) {
                return JSON.stringify({ ready: false, items: 0, withPhone: 0, masked: 0 });
            }

            let withPhone = 0;
            let masked = 0;
            for (let index = 0; index < items.length; index++) {
                const item = items[index];
                if (needsPhoneReveal(item, index)) {
                    masked++;
                    continue;
                }

                withPhone++;
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

    /// <summary>Клик по кнопке «Перейти в чат» на карточке отклика (тот же индекс, что у <c>job-application/item</c>).</summary>
    public static string BuildClickCandidateChatByIndexScript(int index) =>
        $$"""
        (() => {
            const isVisible = (element) => {
                if (!element) {
                    return false;
                }

                const style = window.getComputedStyle(element);
                if (style.display === "none" || style.visibility === "hidden" || style.pointerEvents === "none") {
                    return false;
                }

                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };

            const dispatchClick = (element) => {
                element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                if (typeof element.click === "function") {
                    element.click();
                }
            };

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const idx = {{index}};
            if (idx < 0 || idx >= items.length) {
                return JSON.stringify({ ok: false, reason: "index_out_of_range", items: items.length, index: idx });
            }

            const item = items[idx];
            const chat = item.querySelector("[data-marker='job-application/link/to-chat']");
            if (!chat) {
                return JSON.stringify({ ok: false, reason: "no_chat_button", index: idx, items: items.length });
            }

            if (!isVisible(chat)) {
                return JSON.stringify({ ok: false, reason: "chat_button_hidden", index: idx, items: items.length });
            }

            try {
                chat.scrollIntoView({ block: "center", inline: "nearest" });
            } catch {
            }

            try {
                dispatchClick(chat);
                return JSON.stringify({ ok: true, index: idx, items: items.length });
            } catch (error) {
                return JSON.stringify({ ok: false, reason: String(error), index: idx, items: items.length });
            }
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
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{AgeParseHelpersJs}}
            initRevealedPhonesStore();

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

            const popupPhone = readContactsPopupPhone();
            const phoneEl =
                document.querySelector("[data-marker='job-application/phone']") ??
                document.querySelector("[data-marker='job-application/call-button']");
            const inlinePhone = normalize(phoneEl?.textContent ?? "");
            const phone = isRevealedPhoneText(popupPhone)
                ? popupPhone
                : (isRevealedPhoneText(inlinePhone) ? inlinePhone : "");
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

            let age = "";
            for (const paragraph of searchRoot.querySelectorAll("p")) {
                const found = extractAgeYearsFromText(paragraph.textContent);
                if (found) {
                    age = found;
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

    /// <summary>Ключи карточек списка для пропуска detail-enrich (sourceResponseId + телефон для enrichment map).</summary>
    public static string BuildCollectListItemSkipKeysScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{CardFingerprintJs}}
        {{SourceResponseIdJs}}
            initRevealedPhonesStore();

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

            const parseVacancyLink = (root, vacancyListingAnchor) => {
                const directHref = normalizeUrl(vacancyListingAnchor?.getAttribute("href") ?? "");
                if (
                    directHref
                    && /\/\d{5,}/.test(directHref)
                    && !/\/profile\/candidates(?:[/?#]|$)/i.test(directHref)
                ) {
                    return directHref;
                }

                return directHref;
            };

            const parseVacancyAndCity = (root, vacancyListingAnchor) => {
                const fromAnchor = normalizeCardText(vacancyListingAnchor?.textContent ?? "");
                if (fromAnchor) {
                    const vacancyParts = fromAnchor.split("·").map((x) => x.trim()).filter(Boolean);
                    return {
                        vacancy: vacancyParts[0] ?? "",
                        city: vacancyParts.length > 1 ? vacancyParts[1] : ""
                    };
                }

                return { vacancy: "", city: "" };
            };

            const readPhone = (item, index) => readItemPhone(item, index);

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            return JSON.stringify(
                items.map((item, index) => {
                    const fullName = normalizeCardText(item.querySelector("h3, h4")?.textContent ?? "");
                    const vacancyListingAnchor = item.querySelector("[data-marker='job-application/link/to-resume']");
                    const vacancyUrl = parseVacancyLink(item, vacancyListingAnchor);
                    const vacancyAndCity = parseVacancyAndCity(item, vacancyListingAnchor);
                    const phone = readPhone(item, index);
                    const phoneDigits = normalizePhoneKeyForSourceId(phone);
                    const sourceResponseId = buildSourceResponseId(
                        fullName,
                        phone,
                        vacancyAndCity.vacancy,
                        vacancyAndCity.city,
                        vacancyUrl);

                    return {
                        index,
                        phoneDigits,
                        sourceResponseId
                    };
                })
            );
        })();
        """;

    /// <summary>Ключи карточек откликов без телефона (для сравнения с локальной базой до popup).</summary>
    public static string BuildCollectListItemCardFingerprintsScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{AgeParseHelpersJs}}
        {{CardFingerprintJs}}
        {{SourceResponseIdJs}}
            initRevealedPhonesStore();

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
                }

                for (const element of root.querySelectorAll("[href],[data-href],[data-url],[data-to],[data-link],[data-state],[onclick]")) {
                    const h = fromAttributes(element);
                    if (h) {
                        return h;
                    }
                }

                return "";
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
                    const text = normalizeCardText(paragraph.textContent);
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

                return directHref;
            };

            const parseVacancyAndCity = (root, vacancyListingAnchor) => {
                const fromAnchor = normalizeCardText(vacancyListingAnchor?.textContent ?? "");
                if (fromAnchor) {
                    const vacancyParts = fromAnchor.split("·").map((x) => x.trim()).filter(Boolean);
                    return {
                        vacancy: vacancyParts[0] ?? "",
                        city: vacancyParts.length > 1 ? vacancyParts[1] : ""
                    };
                }

                return { vacancy: "", city: "" };
            };

            const parseAgeText = (root) => {
                for (const line of Array.from(root.querySelectorAll("p"))) {
                    const found = extractAgeYearsFromText(line.textContent);
                    if (found) {
                        return found;
                    }
                }

                return "";
            };

            const parseGenderText = (root) => {
                // Demographic line only: «Мужчина · 54 года» / bare «Мужчина» — not vacancy prose.
                const malePattern = /(?:^|[\s·•|,\-—])мужчина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год))|$)/i;
                const femalePattern = /(?:^|[\s·•|,\-—])женщина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год))|$)/i;
                const bareMale = /^мужчина$/i;
                const bareFemale = /^женщина$/i;
                for (const line of Array.from(root.querySelectorAll("p"))) {
                    const text = normalizeCardText(line.textContent);
                    if (!text) {
                        continue;
                    }
                    if (bareMale.test(text) || malePattern.test(text)) {
                        return "male";
                    }

                    if (bareFemale.test(text) || femalePattern.test(text)) {
                        return "female";
                    }
                }

                return "";
            };

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            return JSON.stringify(
                items.map((item, index) => {
                    const fullName = normalizeCardText(item.querySelector("h3, h4")?.textContent ?? "");
                    const vacancyListingAnchor = item.querySelector("[data-marker='job-application/link/to-resume']");
                    const vacancyUrl = parseVacancyLink(item, vacancyListingAnchor);
                    const vacancyAndCity = parseVacancyAndCity(item, vacancyListingAnchor);
                    const messengerUrl = resolveMessengerUrl(item);
                    const age = parseAgeText(item);
                    const gender = parseGenderText(item);
                    const cardFingerprint = buildCardFingerprint(
                        fullName,
                        vacancyAndCity.vacancy,
                        vacancyAndCity.city,
                        vacancyUrl,
                        messengerUrl,
                        age
                    );

                    const phone = readItemPhone(item, index);
                    const phoneDigits = normalizePhoneKeyForSourceId(phone);

                    return {
                        index,
                        fullName,
                        cardFingerprint,
                        city: vacancyAndCity.city,
                        age,
                        gender,
                        phoneDigits
                    };
                })
            );
        })();
        """;

    public static string BuildApplyPhoneRevealSkipScript(IReadOnlyCollection<int> skipIndices)
    {
        var skipJson = System.Text.Json.JsonSerializer.Serialize(
            skipIndices.Distinct().ToDictionary(static x => x.ToString(), static _ => true));
        // Merge: fingerprint / phone / profile / collection-filter skips must accumulate, not overwrite.
        return $"window.__leadflowSkipPhoneReveal = Object.assign(window.__leadflowSkipPhoneReveal && typeof window.__leadflowSkipPhoneReveal === 'object' ? window.__leadflowSkipPhoneReveal : {{}}, {skipJson}); JSON.stringify({{ ok: true, skipped: Object.keys(window.__leadflowSkipPhoneReveal || {{}}).length }});";
    }

    /// <summary>CRM-страница откликов <c>/profile/job/responses</c> (фильтры, cv-button, «Скачать отчёт»).</summary>
    public static string BuildIsJobCrmResponsesPageScript() =>
        """
        (() => {
            const isJobCrm = !!(
                document.querySelector("[data-marker='filters/status-list-content']")
                || document.querySelector("[data-marker='job-crm/response/cv-button']")
                || document.querySelector("[data-marker='download-report-button/download']")
            );
            return JSON.stringify({ isJobCrm });
        })();
        """;

    /// <summary>Закрыть панель «Данные» справа, если она осталась открытой после detail-enrich.</summary>
    public static string BuildDismissCandidateDetailPanelScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const dispatchEscape = () => {
                document.dispatchEvent(new KeyboardEvent("keydown", {
                    key: "Escape",
                    code: "Escape",
                    bubbles: true,
                    cancelable: true
                }));
            };

            const isForbiddenClick = (element) => {
                if (!element) {
                    return true;
                }

                const marker = element.getAttribute?.("data-marker") ?? "";
                if (/^download-report-button/i.test(marker)) {
                    return true;
                }

                if (/^filters\//i.test(marker)) {
                    return true;
                }

                if (element.closest?.("[data-marker^='download-report-button']")) {
                    return true;
                }

                if (element.closest?.("[data-marker^='filters/']")) {
                    return true;
                }

                return false;
            };

            const tryClick = (element) => {
                if (isForbiddenClick(element)) {
                    return false;
                }

                try {
                    element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof element.click === "function") {
                        element.click();
                    }

                    return true;
                } catch {
                    return false;
                }
            };

            dispatchEscape();
            closeContactsPopup();

            const responsePanel = document.querySelector("[class*='styles-module-response']");
            if (responsePanel) {
                const closeInPanel = responsePanel.querySelector(
                    "[data-marker*='close'], [aria-label*='Закрыть'], [aria-label*='закрыть']"
                );
                tryClick(closeInPanel);
                dispatchEscape();
            }

            const modalRoots = Array.from(document.querySelectorAll(
                "[role='dialog'], [class*='modal'], [class*='Modal'], [class*='drawer'], [class*='Drawer']"
            ));
            for (const modal of modalRoots) {
                const closeBtn = modal.querySelector(
                    "[data-marker*='close'], [aria-label*='Закрыть'], [aria-label*='закрыть']"
                );
                tryClick(closeBtn);
            }

            return JSON.stringify({
                ok: true,
                hadResponsePanel: !!responsePanel
            });
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

    /// <summary>Отправка ответа в мини-чат через поле <c>reply/input</c>.</summary>
    public static string BuildSendMiniMessengerReplyScript(string messageText)
    {
        var jsonText = System.Text.Json.JsonSerializer.Serialize(messageText);
        return $$"""
        (() => {
            const text = {{jsonText}};
            const input = document.querySelector("[data-marker='reply/input']");
            if (!input) {
                return JSON.stringify({ ok: false, reason: "no_reply_input" });
            }

            const setNativeValue = (element, value) => {
                const proto = element instanceof HTMLTextAreaElement
                    ? HTMLTextAreaElement.prototype
                    : HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, "value")?.set;
                if (setter) {
                    setter.call(element, value);
                } else {
                    element.value = value;
                }
            };

            input.focus();
            setNativeValue(input, text);
            input.dispatchEvent(new Event("input", { bubbles: true }));
            input.dispatchEvent(new Event("change", { bubbles: true }));

            const sendSelectors = [
                "[data-marker='reply/send']",
                "[data-marker='reply/submit']",
                "[data-marker='reply/sendButton']",
                "form[data-marker='reply'] button[type='submit']"
            ];

            for (const selector of sendSelectors) {
                const button = document.querySelector(selector);
                if (!button || button.disabled) {
                    continue;
                }

                // Одно нажатие: dispatchEvent + click() вместе отправляют сообщение дважды.
                if (typeof button.click === "function") {
                    button.click();
                } else {
                    button.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                }

                return JSON.stringify({ ok: true, method: "click", selector });
            }

            input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", code: "Enter", bubbles: true }));
            input.dispatchEvent(new KeyboardEvent("keyup", { key: "Enter", code: "Enter", bubbles: true }));
            return JSON.stringify({ ok: true, method: "enter" });
        })();
        """;
    }

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

    /// <summary>Клик по inline-кнопкам с маской «**» (старый UX без popup).</summary>
    public static string BuildRevealMaskedPhonesStepScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
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

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let masked = 0;
            let clicked = 0;
            for (let index = 0; index < items.length; index++) {
                const item = items[index];
                if (shouldSkipPhoneReveal(index)) {
                    continue;
                }

                const btn = item.querySelector("[data-marker='job-application/phone']");
                if (!btn || btn.closest?.("[data-marker^='download-report-button']")) {
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

    /// <summary>
    /// Раскрывает один номер через popup «Показать номер телефона» (новый UX: call-button → contacts-popup).
    /// При ошибке «Не удалось загрузить контактные данные» повторяет клик.
    /// </summary>
    public static string BuildRevealNextContactsPopupPhoneScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const store = initRevealedPhonesStore();
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let pending = 0;
            for (let index = 0; index < items.length; index++) {
                if (needsPhoneReveal(items[index], index)) {
                    pending++;
                }
            }

            if (pending === 0) {
                return JSON.stringify({
                    items: items.length,
                    pending: 0,
                    clicked: false,
                    revealed: false
                });
            }

            if (isContactsPopupOpen()) {
                closeContactsPopup();
                const settleEnd = Date.now() + 120;
                while (Date.now() < settleEnd) {
                }
            }

            let targetIndex = -1;
            let targetItem = null;
            for (let index = 0; index < items.length; index++) {
                const item = items[index];
                if (!needsPhoneReveal(item, index)) {
                    continue;
                }

                if (!item.querySelector("[data-marker='job-application/call-button']")
                    && !/\*/.test(item.querySelector("[data-marker='job-application/phone']")?.textContent ?? "")) {
                    continue;
                }

                targetIndex = index;
                targetItem = item;
                break;
            }

            if (!targetItem) {
                return JSON.stringify({
                    items: items.length,
                    pending,
                    clicked: false,
                    revealed: false,
                    reason: "no_popup_target"
                });
            }

            let retries = 0;
            let phone = "";
            let lastState = "timeout";
            const maxRetries = 3;
            while (retries < maxRetries && !phone) {
                retries++;
                const clickResult = clickPhoneRevealTarget(targetItem);
                if (!clickResult.clicked) {
                    break;
                }

                lastState = waitForContactsPopup(3500);
                if (lastState === "error") {
                    closeContactsPopup();
                    const settleEnd = Date.now() + 180;
                    while (Date.now() < settleEnd) {
                    }

                    continue;
                }

                if (lastState === "ready") {
                    phone = readContactsPopupPhone();
                }

                if (!phone) {
                    closeContactsPopup();
                    const settleEnd = Date.now() + 180;
                    while (Date.now() < settleEnd) {
                    }
                }
            }

            if (phone) {
                store[String(targetIndex)] = phone;
                closeContactsPopup();
            }

            return JSON.stringify({
                items: items.length,
                pending,
                clicked: true,
                index: targetIndex,
                revealed: !!phone,
                phone,
                retries,
                state: lastState
            });
        })();
        """;

    public static string BuildExtractionScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{AgeParseHelpersJs}}
            initRevealedPhonesStore();

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

            // Структурные маркеры формы входа; текстовые — только без кабинета (баннеры «Зарегистрироваться» не считаем).
            const hasLoginDom = !!(
                document.querySelector("[data-marker='auth-app-root']") ||
                document.querySelector("form[data-marker='login-form']") ||
                document.querySelector("[data-marker='login-form/login']") ||
                document.querySelector("[data-marker='login-form/password']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']")
            );
            const hasLoggedInProfile = !!(
                document.querySelector("[data-marker='header/profile-name']") ||
                document.querySelector("[data-marker='profile-switch/link']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/profile/name']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/money']")
            );
            const containsAuthText = (value) =>
                /телефон или почта|забыли пароль|запомнить пароль|войти в авито|нет аккаунта на/i.test(value ?? "");
            const loginCandidates = Array.from(document.querySelectorAll("input, button, a, h1, h2, h3, label, form"));
            const hasLoginText = !hasLoggedInProfile && loginCandidates.some((element) => {
                if (!isVisible(element)) {
                    return false;
                }

                return containsAuthText(element.textContent) ||
                    containsAuthText(element.getAttribute?.("placeholder")) ||
                    containsAuthText(element.getAttribute?.("aria-label"));
            });
            const hasLogin = hasLoginDom || hasLoginText;

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
                const vacancyIdMatch = (vacancyUrl ?? "").match(/(?:\/|_)(\d{5,})(?:\?|$|\/)/);
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

            const parseGender = (root, rawText) => {
                const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
                // Demographic line only: «Мужчина · 54 года» / bare «Мужчина» — not vacancy prose.
                const malePattern = /(?:^|[\s·•|,\-—])мужчина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год))|$)/i;
                const femalePattern = /(?:^|[\s·•|,\-—])женщина(?![а-яё])(?:\s*[·•|,\-—]\s*|\s+(?=\d{1,2}\s*(?:лет|года|год))|$)/i;
                const bareMale = /^мужчина$/i;
                const bareFemale = /^женщина$/i;

                for (const line of Array.from(root.querySelectorAll("p"))) {
                    const text = normalize(line.textContent);
                    if (!text) {
                        continue;
                    }
                    if (bareMale.test(text) || malePattern.test(text)) {
                        return "male";
                    }

                    if (bareFemale.test(text) || femalePattern.test(text)) {
                        return "female";
                    }
                }

                const raw = normalize(rawText);
                if (malePattern.test(raw)) {
                    return "male";
                }

                if (femalePattern.test(raw)) {
                    return "female";
                }

                return "";
            };

            const parseAgeText = (root, rawText) => {
                const oldAge = root.querySelector("p[data-marker='undefined/container'] span")?.textContent?.trim();
                if (oldAge) {
                    const fromOld = extractAgeYearsFromText(oldAge);
                    if (fromOld) {
                        return fromOld;
                    }
                }

                // Новый формат карточки: «Мужчина · 54 года · …» / «Женщина · 37 лет · …».
                // «опыт N лет» / «стаж N лет» не считаем возрастом (см. extractAgeYearsFromText).
                for (const line of Array.from(root.querySelectorAll("p"))) {
                    const found = extractAgeYearsFromText(line.textContent);
                    if (found) {
                        return found;
                    }
                }

                return extractAgeYearsFromText(rawText);
            };

            const listItems = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const candidates = roots.map((root) => {
                const name = getNameNode(root)?.textContent?.trim() ?? "";
                const rootIndex = listItems.indexOf(root);
                const phone = readItemPhone(root, rootIndex);
                const vacancyListingAnchor = getVacancyAnchor(root);
                let vacancyUrl = parseVacancyLink(root, vacancyListingAnchor);
                const rawText = root.innerText?.replace(/\s+/g, " ").trim() ?? "";
                let ageText = parseAgeText(root, rawText);
                let gender = parseGender(root, rawText);
                const vacancyAndCity = parseVacancyAndCity(root, vacancyListingAnchor);
                let vacancy = vacancyAndCity.vacancy;
                let city = vacancyAndCity.city;

                const phoneKey = phone.replace(/\D/g, "");
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

                    if (enriched.gender && !gender) {
                        gender = enriched.gender;
                    }
                }
                const messengerUrl = resolveMessengerUrl(root);
                const sourceResponseId = buildSourceResponseId(name, phone, vacancy, city, vacancyUrl);

                return {
                    fullName: name,
                    phone,
                    age: ageText,
                    gender,
                    vacancy,
                    city,
                    vacancyUrl,
                    messengerUrl,
                    sourceResponseId,
                    domIndex: rootIndex,
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
