namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// JS-снимки страниц откликов Avito (<c>/profile/candidates</c> и CRM <c>/profile/job/responses</c>).
/// </summary>
public static class AvitoCandidatesPageScripts
{
    /// <summary>
    /// Хранилище данных текущего прохода в Symbol-ключе; сбрасывается перед обходом списка.
    /// </summary>
    private const string StateStoreHelpersJs =
        """
        const lfState = () => {
            const sym = Symbol.for("dom.cache.v1");
            let store = window[sym];
            if (!store || typeof store !== "object") {
                store = {};
                try {
                    Object.defineProperty(window, sym, { value: store, enumerable: false, configurable: true, writable: true });
                } catch {
                    window[sym] = store;
                }
            }

            return store;
        };
        """;

    /// <summary>Общие хелперы: Symbol-state + кэш раскрытых номеров и чтение popup «Временный номер».</summary>
    private const string ContactsPhoneHelpersJs = StateStoreHelpersJs + ContactsPhoneHelpersCoreJs;

    /// <summary>Хелперы чтения телефонов (без state-хранилища).</summary>
    private const string ContactsPhoneHelpersCoreJs =
        """
        const initRevealedPhonesStore = () => {
            const state = lfState();
            if (!state.revealedPhones || typeof state.revealedPhones !== "object") {
                state.revealedPhones = {};
            }

            return state.revealedPhones;
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
            const store = lfState().revealedPhones;
            if (!store || typeof store !== "object") {
                return "";
            }

            return normalizePhoneText(store[String(index)] ?? store[index] ?? "");
        };

        const clearCachedPhone = (index) => {
            const store = lfState().revealedPhones;
            if (!store || typeof store !== "object") {
                return;
            }

            try {
                delete store[String(index)];
                delete store[index];
            } catch {
            }
        };

        const readInlinePhone = (item) => {
            const phoneEl = item.querySelector("[data-marker='job-application/phone']");
            if (!phoneEl) {
                return "";
            }

            return normalizePhoneText(phoneEl.textContent ?? "");
        };

        const isMaskedPhoneText = (raw) => /\*/.test(normalizePhoneText(raw));

        const readItemPhone = (item, index) => {
            // Маска в DOM — старый кэш невалиден (временный номер Avito мог смениться).
            const inline = readInlinePhone(item);
            if (isMaskedPhoneText(inline)) {
                clearCachedPhone(index);
                return "";
            }

            const cached = getCachedPhone(index);
            if (isRevealedPhoneText(cached)) {
                return cached;
            }

            if (isRevealedPhoneText(inline)) {
                return inline;
            }

            const callBtn = item.querySelector("[data-marker='job-application/call-button']");
            if (callBtn) {
                const callText = normalizePhoneText(callBtn.textContent ?? "");
                if (isMaskedPhoneText(callText)) {
                    clearCachedPhone(index);
                    return "";
                }

                if (isRevealedPhoneText(callText)) {
                    return callText;
                }
            }

            return "";
        };

        const shouldSkipPhoneReveal = (index) => {
            const skips = lfState().skipPhoneReveal;
            if (!skips || typeof skips !== "object") {
                return false;
            }

            return !!(skips[String(index)] || skips[index]);
        };

        // Карточка, чей клик не дал номера в этом проходе: не тыкаем повторно до
        // следующего прохода (иначе одна «мёртвая» кнопка съедает весь бюджет).
        const markPhoneRevealFailed = (index) => {
            if (index < 0) {
                return;
            }

            const state = lfState();
            if (!state.failedPhoneReveal || typeof state.failedPhoneReveal !== "object") {
                state.failedPhoneReveal = {};
            }

            state.failedPhoneReveal[String(index)] = true;
            advanceRevealCursor();
        };

        const hasFailedPhoneReveal = (index) => {
            const failed = lfState().failedPhoneReveal;
            return !!(failed && typeof failed === "object"
                && (failed[String(index)] || failed[index]));
        };

        // Round-robin курсор выбора целей: живёт в lfState и НЕ сбрасывается между
        // проходами. Дополнительно зеркалим в sessionStorage — он переживает
        // перезагрузку страницы в той же вкладке, иначе каждый reload сбрасывал бы
        // ротацию в начало. Иначе при inflow >= бюджета глубокие замаскированные
        // карточки голодают до 6-дневного cutoff.
        const revealCursorStorageKey = "lf.revealCursor.v1";

        const readStoredRevealCursor = () => {
            try {
                const value = Number(window.sessionStorage?.getItem(revealCursorStorageKey));
                if (Number.isFinite(value) && value > 0) {
                    return value;
                }
            } catch {
            }

            return 0;
        };

        const getRevealCursor = () => {
            const value = Number(lfState().revealCursor);
            const inMemory = Number.isFinite(value) && value > 0 ? value : 0;
            return Math.max(inMemory, readStoredRevealCursor());
        };

        const advanceRevealCursor = () => {
            const next = getRevealCursor() + 1;
            lfState().revealCursor = next;
            try {
                window.sessionStorage?.setItem(revealCursorStorageKey, String(next));
            } catch {
            }
        };

        // Порядок обхода карточек для раскрытия: phone-watch всегда первыми,
        // остальные — со сдвигом на курсор (round-robin).
        const orderedRevealIndexes = (items) => {
            const ordered = Array.from(items.keys()).sort((a, b) =>
                Number(isPhoneWatchPriority(b)) - Number(isPhoneWatchPriority(a)) || a - b);
            const rest = ordered.filter((index) => !isPhoneWatchPriority(index));
            if (rest.length === 0) {
                return ordered;
            }

            const by = getRevealCursor() % rest.length;
            const rotatedRest = rest.slice(by).concat(rest.slice(0, by));
            const result = [];
            let rotatedIndex = 0;
            for (const index of ordered) {
                if (isPhoneWatchPriority(index)) {
                    result.push(index);
                } else {
                    result.push(rotatedRest[rotatedIndex++]);
                }
            }

            return result;
        };

        const isPhoneWatchPriority = (index) => {
            const priorities = lfState().phoneWatchPriority;
            return !!(priorities && typeof priorities === "object"
                && (priorities[String(index)] || priorities[index]));
        };

        // Skip «уже известен» не действует, если номер под маской — иначе phone-watch не увидит смену.
        const needsPhoneReveal = (item, index) => {
            const inline = readInlinePhone(item);
            if (isMaskedPhoneText(inline)) {
                clearCachedPhone(index);
                return true;
            }

            const callBtn = item.querySelector("[data-marker='job-application/call-button']");
            if (callBtn && isMaskedPhoneText(callBtn.textContent ?? "")) {
                clearCachedPhone(index);
                return true;
            }

            if (shouldSkipPhoneReveal(index)) {
                return false;
            }

            return !isRevealedPhoneText(readItemPhone(item, index));
        };

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

        const humanClick = (element) => {
            if (!element) {
                return false;
            }

            try {
                const rect = element.getBoundingClientRect();
                const x = rect.left + Math.max(rect.width, 1) * (0.32 + Math.random() * 0.36);
                const y = rect.top + Math.max(rect.height, 1) * (0.32 + Math.random() * 0.36);
                const base = {
                    bubbles: true,
                    cancelable: true,
                    view: window,
                    clientX: x,
                    clientY: y,
                    button: 0,
                    buttons: 1
                };
                try {
                    element.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerdown", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mousedown", base));
                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerup", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mouseup", { ...base, buttons: 0 }));
                element.dispatchEvent(new MouseEvent("click", { ...base, buttons: 0 }));
                return true;
            } catch {
                return false;
            }
        };

        const closeContactsPopup = () => {
            const closeBtn = document.querySelector("[data-marker='job-application/response/contacts-popup/close']");
            if (closeBtn && humanClick(closeBtn)) {
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
                humanClick(callBtn);
                return { clicked: true, kind: "call-button" };
            }

            const phoneBtn = item.querySelector("[data-marker='job-application/phone']");
            const raw = phoneBtn?.textContent ?? "";
            if (phoneBtn && /\*/.test(raw)) {
                const text = phoneBtn.querySelector(".styles-module-text");
                humanClick(text ?? phoneBtn);
                return { clicked: true, kind: "masked-phone" };
            }

            return { clicked: false, kind: "none" };
        };

        const snapshotContactsPopupState = () => {
            if (hasContactsPopupLoadError()) {
                return { state: "error", phone: "" };
            }

            if (!isContactsPopupOpen()) {
                return { state: "closed", phone: "" };
            }

            const phone = readContactsPopupPhone();
            if (phone) {
                return { state: "ready", phone };
            }

            return { state: "open", phone: "" };
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

    /// <summary>
    /// Название и город вакансии: ссылка «на вакансию», текст «на вакансию «…» · город» и старый «· «…» · город».
    /// </summary>
    private const string VacancyParseHelpersJs =
        """
        const stripVacancyQuotes = (value) => (value ?? "").trim().replace(/^[«"„“]+|[»"”]+$/g, "").trim();

        const parseVacancyLineFromText = (value) => {
            const normalized = (value ?? "").replace(/\s+/g, " ").trim();
            if (!normalized) {
                return { vacancy: "", city: "" };
            }

            const quotedParts = normalized.match(/·\s*«([^»]+)»\s*·\s*([^·]+)/);
            if (quotedParts) {
                return {
                    vacancy: (quotedParts[1] ?? "").trim(),
                    city: (quotedParts[2] ?? "").trim()
                };
            }

            const vacancyLine = normalized.match(/на\s+вакансию\s+([^·]+?)(?:\s*[·]\s*([^·]+))?\s*$/i);
            if (!vacancyLine) {
                return { vacancy: "", city: "" };
            }

            return {
                vacancy: stripVacancyQuotes(vacancyLine[1] ?? ""),
                city: (vacancyLine[2] ?? "").trim()
            };
        };

        const parseVacancyAndCityFromRoot = (root, vacancyListingAnchor) => {
            const fromAnchor = (vacancyListingAnchor?.textContent ?? "").replace(/\s+/g, " ").trim();
            if (fromAnchor) {
                const vacancyParts = fromAnchor.split("·").map((x) => x.trim()).filter(Boolean);
                return {
                    vacancy: stripVacancyQuotes(vacancyParts[0] ?? ""),
                    city: vacancyParts.length > 1 ? vacancyParts[1] : ""
                };
            }

            for (const paragraph of root.querySelectorAll("p")) {
                const text = (paragraph.textContent ?? "").replace(/\s+/g, " ").trim();
                if (!/на\s+вакансию/i.test(text)) {
                    continue;
                }

                const anchor = paragraph.querySelector("a[href]");
                const fromLink = stripVacancyQuotes((anchor?.textContent ?? "").replace(/\s+/g, " ").trim());
                if (fromLink) {
                    const anchorIndex = text.toLowerCase().indexOf(fromLink.toLowerCase());
                    const tail = anchorIndex >= 0 ? text.slice(anchorIndex + fromLink.length) : "";
                    const cityParts = tail.split("·").map((x) => x.trim()).filter(Boolean);
                    return {
                        vacancy: fromLink,
                        city: cityParts.at(-1) ?? ""
                    };
                }

                const fromText = parseVacancyLineFromText(text);
                if (fromText.vacancy || fromText.city) {
                    return fromText;
                }
            }

            return { vacancy: "", city: "" };
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
            ) || location.hash === "#block"
              || !!document.querySelector('a[href*="support.avito.ru/request/720"]');
            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha|Отключить\s+VPN|самол[её]те/i.test(title + "\n" + bodyText);
            const probeText = title + "\n" + bodyText;
            const hasIpText = /Доступ\s+ограничен/i.test(probeText)
              && /проблема\s+с\s+IP/i.test(probeText);
            const hasStaticIpBlock = location.hash === "#block"
              && !!document.querySelector('a[href*="support.avito.ru/request/720"]')
              && /Отключить\s+VPN|самол[её]те/i.test(probeText);
            const hasCaptchaContinue = /Продолжить/i.test(probeText)
              && (/капч/i.test(probeText)
                  || !!document.querySelector('.firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form, [role="dialog"][aria-modal="true"]'));
            const hasCaptchaChallenge = hasCaptchaWidget
              || /решени[еюя]\s+капч/i.test(probeText)
              || hasCaptchaContinue;
            const hasIpBlock = !hasCaptchaChallenge && (hasIpText || hasStaticIpBlock);
            const blocked =
                hasIpBlock ||
                hasCaptchaChallenge ||
                (itemCount === 0 &&
                statusCount === 0 &&
                (hasFirewallDom || (hasFirewallText && hasCaptchaWidget) || hasFirewallText));

            let kind = hasIpBlock ? "firewall" : "captcha";
            if (!hasIpBlock && blocked && document.getElementById("geetest_captcha")) {
                kind = "geetest";
            } else if (!hasIpBlock && blocked && document.getElementById("inner-captcha")) {
                kind = "image-captcha";
            } else if (!hasIpBlock && blocked && (document.getElementById("h-captcha") || document.querySelector(".h-captcha[data-sitekey]"))) {
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
            const hasFirewallDom = !!document.querySelector(".firewall-container, .js-firewall-form, .firewall-title")
                || location.hash === "#block"
                || !!document.querySelector('a[href*="support.avito.ru/request/720"]');
            const hasIpDialog = !!document.querySelector('[role="dialog"][aria-modal="true"], [aria-modal="true"]')
                && /Доступ\s+ограничен|проблема\s+с\s+IP/i.test(title + "\n" + bodyText);
            const blocked =
                hasIpDialog ||
                (itemCount === 0 &&
                statusCount === 0 &&
                (hasFirewallDom || /Доступ\s+ограничен|проблема\s+с\s+IP|Отключить\s+VPN|самол[её]те/i.test(title + "\n" + bodyText)));

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

    /// <summary>Геометрия скроллера списка откликов: для расчёта дельты CDP-колеса.</summary>
    public static string BuildScrollGeometryScript() =>
        """
        (() => {
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
                        if ((style.overflowY === "auto" || style.overflowY === "scroll")
                            && node.scrollHeight > node.clientHeight + 40) {
                            return node;
                        }
                        node = node.parentElement;
                    }
                }

                return document.scrollingElement || document.documentElement;
            };

            const scroller = findScroller();
            return JSON.stringify({
                scrollTop: scroller.scrollTop,
                clientHeight: scroller.clientHeight,
                scrollHeight: scroller.scrollHeight
            });
        })();
        """;

    /// <summary>Снимок после шага прокрутки без самого прокрутки (движение делает CDP-колесо).</summary>
    public static string BuildScrollStepProbeScript(int previousItemCount = 0) =>
        BuildScrollStepScript(previousItemCount, includeScroll: false);

    /// <summary>Один шаг прокрутки вниз по контейнеру списка откликов.</summary>
    public static string BuildScrollStepScript(int previousItemCount = 0) =>
        BuildScrollStepScript(previousItemCount, includeScroll: true);

    private static string BuildScrollStepScript(int previousItemCount, bool includeScroll)
    {
        var scrollBlock = includeScroll
            ? """
            const ratio = 0.32 + Math.random() * 0.28;
            const delta = Math.max(Math.floor(scroller.clientHeight * ratio), 180);
            try {
                // The result is sampled immediately below, so animation would report moved=false
                // before the first frame and make the C# loop stop after three false stable rounds.
                scroller.scrollBy({ top: delta, left: 0, behavior: "auto" });
            } catch {
                scroller.scrollBy(0, delta);
            }
            """
            : "";
        return $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{AgeParseHelpersJs}}
        {{CardFingerprintJs}}
        {{VacancyParseHelpersJs}}
        {{SourceResponseIdJs}}
            const previousItemCount = {{Math.Max(0, previousItemCount)}};
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
            {{scrollBlock}}
            const atEnd = scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 8;
            initRevealedPhonesStore();
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const normalizeUrl = (href) => {
                const value = (href ?? "").trim();
                if (!value || value === "#") return "";
                if (value.startsWith("//")) return `https:${value}`;
                if (value.startsWith("/")) return `${window.location.origin}${value}`;
                return value;
            };
            const resolveMessengerUrl = (root) => {
                if (!root) return "";
                const attrCandidates = ["href", "data-href", "data-url", "data-to", "data-link", "data-state", "onclick"];
                const fromAttributes = (element) => {
                    if (!element) return "";
                    for (const attr of attrCandidates) {
                        const raw = element.getAttribute?.(attr);
                        if (!raw) continue;
                        const direct = normalizeUrl(raw);
                        if (direct && /(messenger|chat|dialog)/i.test(direct)) return direct;
                        const match = String(raw).match(/https?:\/\/[^"'\\\s]*(messenger|chat|dialog)[^"'\\\s]*/i);
                        if (match?.[0]) return normalizeUrl(match[0]);
                    }
                    return "";
                };
                const chatElement = root.querySelector("[data-marker='job-application/link/to-chat']");
                const direct = fromAttributes(chatElement);
                if (direct) return direct;
                const parent = chatElement?.closest("a");
                const parentUrl = fromAttributes(parent);
                if (parentUrl) return parentUrl;
                for (const element of root.querySelectorAll("[href],[data-href],[data-url],[data-to],[data-link],[data-state],[onclick]")) {
                    const url = fromAttributes(element);
                    if (url) return url;
                }
                return "";
            };
            const parseVacancyLink = (root, vacancyAnchor) => {
                const direct = normalizeUrl(vacancyAnchor?.getAttribute("href") ?? "");
                if (direct && /\/\d{5,}/.test(direct) && !/\/profile\/candidates(?:[/?#]|$)/i.test(direct)) {
                    return direct;
                }
                for (const paragraph of root.querySelectorAll("p")) {
                    if (!/на вакансию/i.test(normalizeCardText(paragraph.textContent))) continue;
                    const href = normalizeUrl(paragraph.querySelector("a[href]")?.getAttribute("href") ?? "");
                    if (href && /\/\d{5,}/.test(href)) return href;
                }
                return direct;
            };
            const parseAgeText = (root) => {
                for (const line of root.querySelectorAll("p")) {
                    const age = extractAgeYearsFromText(line.textContent);
                    if (age) return age;
                }
                return "";
            };
            const parseGenderText = (root) => {
                for (const line of root.querySelectorAll("p")) {
                    const text = normalizeCardText(line.textContent);
                    if (/(?:^|[\s·•|,\-—])мужчина(?![а-яё])/i.test(text)) return "male";
                    if (/(?:^|[\s·•|,\-—])женщина(?![а-яё])/i.test(text)) return "female";
                }
                return "";
            };
            const mapItem = (item, index) => {
                const fullName = normalizeCardText(item.querySelector("h3, h4")?.textContent ?? "");
                const vacancyAnchor = item.querySelector("[data-marker='job-application/link/to-resume']");
                const parsed = parseVacancyAndCityFromRoot(item, vacancyAnchor);
                const vacancyUrl = parseVacancyLink(item, vacancyAnchor);
                const messengerUrl = resolveMessengerUrl(item);
                const age = parseAgeText(item);
                const phone = readItemPhone(item, index);
                return {
                    index,
                    fullName,
                    cardFingerprint: buildCardFingerprint(fullName, parsed.vacancy, parsed.city, vacancyUrl, messengerUrl, age),
                    city: parsed.city,
                    age,
                    gender: parseGenderText(item),
                    phoneDigits: normalizePhoneKeyForSourceId(phone)
                };
            };
            // Empty/shrinking lists have no boundary card at the old index.
            const itemKey = (item) => !item ? "" : [
                normalizeCardText(item?.querySelector("h3, h4")?.textContent ?? ""),
                normalizeUrl(item?.querySelector("[data-marker='job-application/link/to-resume']")?.getAttribute("href") ?? ""),
                resolveMessengerUrl(item)
            ].join("\u001f");
            const boundary = lfState().scrollBoundary;
            const domChanged = previousItemCount > 0 && (
                items.length < previousItemCount
                || !boundary
                || boundary.count !== previousItemCount
                || boundary.firstKey !== itemKey(items[0])
                || boundary.lastKey !== itemKey(items[previousItemCount - 1])
            );
            const candidateItems = domChanged || items.length < previousItemCount
                ? items
                : items.slice(previousItemCount);
            const structureValid = candidateItems.every((item) =>
                !!normalizeCardText(item.querySelector("h3, h4")?.textContent ?? ""));
            const fullRescan = items.length < previousItemCount || domChanged || !structureValid;
            const newItems = (fullRescan ? items : items.slice(previousItemCount))
                .map((item, offset) => mapItem(item, fullRescan ? offset : previousItemCount + offset));
            lfState().scrollBoundary = {
                count: items.length,
                firstKey: itemKey(items[0]),
                lastKey: itemKey(items.at(-1))
            };
            return JSON.stringify({
                itemCount: items.length,
                scrollTop: scroller.scrollTop,
                scrollHeight: scroller.scrollHeight,
                clientHeight: scroller.clientHeight,
                moved: Math.abs(scroller.scrollTop - beforeTop) > 2,
                atEnd,
                fullRescan,
                structureValid,
                newItems
            });
        })();
        """;
    }

    /// <summary>Короткий скролл вверх — как будто перечитали предыдущие карточки.</summary>
    public static string BuildScrollBackScript() =>
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

                return document.scrollingElement || document.documentElement;
            };

            const scroller = findScroller();
            const beforeTop = scroller.scrollTop;
            const ratio = 0.18 + Math.random() * 0.22;
            const delta = -Math.max(Math.floor(scroller.clientHeight * ratio), 120);
            try {
                scroller.scrollBy({ top: delta, left: 0, behavior: "smooth" });
            } catch {
                scroller.scrollBy(0, delta);
            }
            return JSON.stringify({
                itemCount: countItems(),
                scrollTop: scroller.scrollTop,
                moved: Math.abs(scroller.scrollTop - beforeTop) > 2,
                atEnd: false
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
            try {
                scroller.scrollTo({ top: 0, behavior: "smooth" });
            } catch {
                scroller.scrollTop = 0;
            }

            if (scroller.scrollTop > 2) {
                scroller.scrollTop = 0;
            }

            return JSON.stringify({ ok: true });
        })();
        """;

    /// <summary>Телефон раскрыт: inline, в кэше после popup или в кнопке без маски «**». failed — карточки, чей клик не дал номера в этом проходе.</summary>
    public static string BuildPhonesReadyProbeScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            initRevealedPhonesStore();

            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            if (items.length === 0) {
                return JSON.stringify({ ready: false, items: 0, withPhone: 0, masked: 0, failed: 0, priorityPending: 0 });
            }

            let withPhone = 0;
            let masked = 0;
            let failed = 0;
            let priorityPending = 0;
            for (let index = 0; index < items.length; index++) {
                const item = items[index];
                if (!needsPhoneReveal(item, index)) {
                    withPhone++;
                    continue;
                }

                if (hasFailedPhoneReveal(index)) {
                    failed++;
                    continue;
                }

                masked++;
                if (isPhoneWatchPriority(index)) {
                    priorityPending++;
                }
            }

            const ratio = withPhone / items.length;
            return JSON.stringify({
                // General readiness must never leave an active phone-watch unopened.
                ready: priorityPending === 0
                    && (ratio >= 0.92 || (items.length <= 3 && withPhone === items.length)),
                items: items.length,
                withPhone,
                masked,
                failed,
                priorityPending
            });
        })();
        """;

    /// <summary>Очищает привязанный к DOM-индексам кэш перед новым проходом/субпрофилем.</summary>
    /// <remarks><c>revealCursor</c> намеренно НЕ сбрасывается: round-robin между проходами.</remarks>
    public static string BuildResetCandidateCollectionStateScript() =>
        $$"""
        (() => {
        {{StateStoreHelpersJs}}
            const state = lfState();
            state.revealedPhones = {};
            state.skipPhoneReveal = {};
            state.failedPhoneReveal = {};
            state.phoneWatchPriority = {};
            state.scrollBoundary = null;
            state.detailEnrichment = {};
            state.messengerMarkedBefore = new WeakSet();
            return JSON.stringify({ ok: true });
        })();
        """;

    /// <summary>Phone-watch раскрываются раньше остальных карточек в ограниченном бюджете кликов.</summary>
    public static string BuildApplyPhoneWatchPriorityScript(IReadOnlyCollection<int> priorityIndices)
    {
        var priorityJson = System.Text.Json.JsonSerializer.Serialize(
            priorityIndices
                .Where(static x => x >= 0)
                .Distinct()
                .ToDictionary(static x => x.ToString(), static _ => true));
        return $$"""
        (() => {
        {{StateStoreHelpersJs}}
            const state = lfState();
            state.phoneWatchPriority = {{priorityJson}};
            return JSON.stringify({ ok: true, prioritized: Object.keys(state.phoneWatchPriority).length });
        })();
        """;
    }

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

            const humanClick = (element) => {
                if (!element) {
                    return false;
                }

                const rect = element.getBoundingClientRect();
                const x = rect.left + Math.max(rect.width, 1) * (0.32 + Math.random() * 0.36);
                const y = rect.top + Math.max(rect.height, 1) * (0.32 + Math.random() * 0.36);
                const base = {
                    bubbles: true,
                    cancelable: true,
                    view: window,
                    clientX: x,
                    clientY: y,
                    button: 0,
                    buttons: 1
                };
                try {
                    element.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerdown", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mousedown", base));
                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerup", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mouseup", { ...base, buttons: 0 }));
                // Native click() runs <a href> default action; dispatchEvent(click) does not.
                if (typeof element.click === "function") {
                    element.click();
                } else {
                    element.dispatchEvent(new MouseEvent("click", { ...base, buttons: 0 }));
                }
                return true;
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

            const target = chat.querySelector("button, a, [role='button']") || chat;
            try {
                humanClick(target);
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
            const humanClick = (element) => {
                if (!element) {
                    return false;
                }

                const rect = element.getBoundingClientRect();
                const x = rect.left + Math.max(rect.width, 1) * (0.32 + Math.random() * 0.36);
                const y = rect.top + Math.max(rect.height, 1) * (0.32 + Math.random() * 0.36);
                const base = {
                    bubbles: true,
                    cancelable: true,
                    view: window,
                    clientX: x,
                    clientY: y,
                    button: 0,
                    buttons: 1
                };
                try {
                    element.scrollIntoView({ block: "center", inline: "nearest" });
                } catch {
                }

                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerdown", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mousedown", base));
                if (typeof PointerEvent === "function") {
                    element.dispatchEvent(new PointerEvent("pointerup", {
                        ...base,
                        pointerType: "mouse",
                        isPrimary: true,
                        pointerId: 1
                    }));
                }

                element.dispatchEvent(new MouseEvent("mouseup", { ...base, buttons: 0 }));
                element.dispatchEvent(new MouseEvent("click", { ...base, buttons: 0 }));
                return true;
            };

            try {
                humanClick(item);
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
        {{VacancyParseHelpersJs}}
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
            for (const paragraph of searchRoot.querySelectorAll("p")) {
                const text = normalize(paragraph.textContent);
                if (!/на\s+вакансию/i.test(text)) {
                    continue;
                }

                const href = normalizeUrl(paragraph.querySelector("a[href]")?.getAttribute("href") ?? "");
                if (href && /\/\d{5,}/.test(href)) {
                    vacancyUrl = href;
                    break;
                }
            }

            const parsedVacancy = parseVacancyAndCityFromRoot(searchRoot, null);
            const vacancy = parsedVacancy.vacancy;
            const city = parsedVacancy.city;

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
        $$"""
        (() => {
        {{StateStoreHelpersJs}}
            const state = lfState();
            state.detailEnrichment = {{enrichmentJson}};
            return JSON.stringify({ ok: true, count: Object.keys(state.detailEnrichment || {}).length });
        })();
        """;

    /// <summary>
    /// Поиск карточки, которой не хватило телефона после основного reveal-цикла
    /// (в списке номера нет — только в панели «Данные о кандидате»). Цель — та же
    /// семантика, что у popup-раскрытия: needsPhoneReveal и не помечена failed.
    /// Возвращает и имя кандидата — проба панели сверяет его, чтобы не закэшировать
    /// телефон чужой карточки под этим индексом.
    /// </summary>
    public static string BuildFindPanelPhoneTargetScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let pending = 0;
            for (let index = 0; index < items.length; index++) {
                if (!hasFailedPhoneReveal(index) && needsPhoneReveal(items[index], index)) {
                    pending++;
                }
            }

            const orderedIndexes = orderedRevealIndexes(items);
            for (const index of orderedIndexes) {
                if (hasFailedPhoneReveal(index)) {
                    continue;
                }

                if (!needsPhoneReveal(items[index], index)) {
                    continue;
                }

                const targetName = normalize(items[index].querySelector("h3, h4")?.textContent ?? "");
                return JSON.stringify({ items: items.length, pending, targetIndex: index, targetName });
            }

            return JSON.stringify({ items: items.length, pending, targetIndex: -1, targetName: "" });
        })();
        """;

    /// <summary>
    /// Корень панели «Данные о кандидате»: диалог/drawer/aside или response-контейнер,
    /// но НЕ карточка списка (исключаем корни внутри job-application/item и содержащие их).
    /// </summary>
    private const string CandidatePanelRootJs =
        """
        const findCandidatePanelRoot = () => {
            const roots = Array.from(document.querySelectorAll(
                "[role='dialog'], aside[class*='drawer'], aside[class*='Drawer'], [class*='styles-module-response']"));
            return roots.find((root) =>
                !root.closest("[data-marker='job-application/item']")
                && !root.querySelector("[data-marker='job-application/item']"))
                ?? null;
        };
        """;

    /// <summary>
    /// Клик «Показать номер» в открытой панели «Данные о кандидате»
    /// (новый UX Avito: телефон карточки списка недоступен, только в панели).
    /// </summary>
    public static string BuildRevealPanelPhoneScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{CandidatePanelRootJs}}
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();

            const panel = findCandidatePanelRoot();
            if (!panel) {
                return JSON.stringify({ clicked: false, reason: "no_panel" });
            }

            const callBtn = panel.querySelector("[data-marker='job-application/call-button']");
            if (callBtn) {
                humanClick(callBtn);
                return JSON.stringify({ clicked: true, kind: "call-button" });
            }

            for (const button of Array.from(panel.querySelectorAll("button, a, [role='button']"))) {
                const text = normalize(button.textContent);
                if (/показа(ть|ние)\s+(номер|телефон)/i.test(text)) {
                    humanClick(button);
                    return JSON.stringify({ clicked: true, kind: "show-phone" });
                }
            }

            return JSON.stringify({ clicked: false, reason: "no_phone_button" });
        })();
        """;

    /// <summary>
    /// Прочитать телефон из открытой панели кандидата. Номер кэшируется по индексу
    /// карточки ТОЛЬКО при совпадении имени панели с ожидаемым (защита от устаревшей
    /// панели чужого кандидата). При успехе сдвигает round-robin курсор.
    /// </summary>
    public static string BuildCandidatePanelPhoneProbeScript(int targetIndex, string expectedNameJson) =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{CandidatePanelRootJs}}
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
            const normalizeName = (text) => normalize(text).toLowerCase();
            const expectedName = normalizeName({{expectedNameJson}});

            const panel = findCandidatePanelRoot();
            if (!panel) {
                return JSON.stringify({ panelOpen: false, nameMatch: false, phone: "", revealed: false });
            }

            let panelName = "";
            for (const heading of Array.from(panel.querySelectorAll("h2, h3, h4"))) {
                const text = normalize(heading.textContent);
                if (text) {
                    panelName = text;
                    break;
                }
            }

            const normalizedPanelName = normalizeName(panelName);
            const nameMatch = !!expectedName
                && !!normalizedPanelName
                && (normalizedPanelName.includes(expectedName) || expectedName.includes(normalizedPanelName));
            if (!nameMatch) {
                return JSON.stringify({ panelOpen: true, nameMatch: false, phone: "", revealed: false, panelName });
            }

            const candidates = [
                panel.querySelector("[data-marker='job-application/phone']"),
                panel.querySelector("[data-marker='job-application/call-button']"),
                ...Array.from(panel.querySelectorAll("h3, h4, [class*='phone'], a[href^='tel:']"))
            ];

            let phone = readContactsPopupPhone();
            if (!isRevealedPhoneText(phone)) {
                for (const element of candidates) {
                    const text = normalize(element?.textContent ?? "");
                    if (isRevealedPhoneText(text)) {
                        phone = text;
                        break;
                    }
                }
            }

            if (isRevealedPhoneText(phone)) {
                const store = initRevealedPhonesStore();
                if ({{targetIndex}} >= 0) {
                    store[String({{targetIndex}})] = phone;
                }

                advanceRevealCursor();
                return JSON.stringify({ panelOpen: true, nameMatch: true, phone, revealed: true });
            }

            return JSON.stringify({ panelOpen: true, nameMatch: true, phone: "", revealed: false });
        })();
        """;

    /// <summary>Ключи карточек списка для пропуска detail-enrich (sourceResponseId + телефон для enrichment map).</summary>
    public static string BuildCollectListItemSkipKeysScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{CardFingerprintJs}}
        {{VacancyParseHelpersJs}}
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

                for (const paragraph of root.querySelectorAll("p")) {
                    const text = normalizeCardText(paragraph.textContent);
                    if (!/на\s+вакансию/i.test(text)) {
                        continue;
                    }

                    const href = normalizeUrl(paragraph.querySelector("a[href]")?.getAttribute("href") ?? "");
                    if (href && /\/\d{5,}/.test(href)) {
                        return href;
                    }
                }

                return directHref;
            };

            const parseVacancyAndCity = parseVacancyAndCityFromRoot;

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
        {{VacancyParseHelpersJs}}
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

            const parseVacancyAndCity = parseVacancyAndCityFromRoot;

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
        return $$"""
        (() => {
        {{StateStoreHelpersJs}}
            const state = lfState();
            state.skipPhoneReveal = Object.assign(state.skipPhoneReveal && typeof state.skipPhoneReveal === "object" ? state.skipPhoneReveal : {}, {{skipJson}});
            return JSON.stringify({ ok: true, skipped: Object.keys(state.skipPhoneReveal || {}).length });
        })();
        """;
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

                return humanClick(element);
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

    /// <summary>
    /// Помечает уже видимые истории до открытия карточки. После клика по кандидату новый мини-чат
    /// выбирается по отсутствию в WeakSet «был до открытия», а не по первому глобальному виджету мессенджера.
    /// Никаких DOM-атрибутов и window-глобалов: состояние только в Symbol-хранилище.
    /// </summary>
    public static string BuildMarkMessengerRootsBeforeOpenScript() =>
        $$"""
        (() => {
        {{StateStoreHelpersJs}}
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
            const isHistoryVisible = (history) => isVisible(history)
                || isVisible(history?.querySelector("[data-marker='messagesHistory/list']"));
            const state = lfState();
            state.messengerMarkedBefore = new WeakSet();

            let marked = 0;
            for (const history of document.querySelectorAll("[data-marker='messagesHistory']")) {
                if (!isHistoryVisible(history)) {
                    continue;
                }

                state.messengerMarkedBefore.add(history);
                marked++;
            }

            return JSON.stringify({ ok: true, marked });
        })();
        """;

    /// <summary>История чата, которой не было в WeakSet до клика (общий фрагмент).</summary>
    private const string MessengerOpenedHistoryJs =
        """
        const isMessengerHistoryOpenedAfterMark = (history) => {
            const state = lfState();
            const markedBefore = state.messengerMarkedBefore;
            const visible = isVisible(history) || isVisible(history?.querySelector("[data-marker='messagesHistory/list']"));
            return visible && !(markedBefore instanceof WeakSet && markedBefore.has(history));
        };
        """;

    /// <summary>Мини-панель в углу или полноэкранный канал после клика «Перейти в чат».</summary>
    public static string BuildMessengerUiVisibleExpression() =>
        $$"""
        () => {
        {{StateStoreHelpersJs}}
        {{MessengerOpenedHistoryJs}}
            if (/\/profile\/messenger\/channel\//i.test(window.location.href)) {
                return true;
            }

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

            // В разметке Avito ссылка «Открыть сообщения во весь экран» и messagesHistory —
            // соседи внутри channel-module-root. Сам контейнер истории может переиспользоваться
            // между карточками, поэтому метка до клика не является признаком старого чата.
            const activeMiniRoot = Array.from(document.querySelectorAll("a[data-marker='mini-messenger/messenger-page-link']"))
                .map((link) => isVisible(link) ? link.closest("[class*='channel-module-root']") : null)
                .find((root) => isVisible(root) && !!root.querySelector("[data-marker='messagesHistory']"));
            if (activeMiniRoot) {
                return true;
            }

            return Array.from(document.querySelectorAll("[data-marker='messagesHistory']"))
                .some((history) => isMessengerHistoryOpenedAfterMark(history));
        }
        """;

    /// <summary>В оболочке мини-чата уже есть хотя бы одно <c>data-marker=message</c>.</summary>
    public static string BuildMessengerMessagesPresentExpression() =>
        $$"""
        () => {
        {{StateStoreHelpersJs}}
        {{MessengerOpenedHistoryJs}}
            const hasMessage = (root) => !!root?.querySelector("[data-marker='message']");
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
            const activeMiniRoot = Array.from(document.querySelectorAll("a[data-marker='mini-messenger/messenger-page-link']"))
                .map((link) => isVisible(link) ? link.closest("[class*='channel-module-root']") : null)
                .find((root) => isVisible(root) && !!root.querySelector("[data-marker='messagesHistory']"));
            if (hasMessage(activeMiniRoot)) {
                return true;
            }

            const openedHistory = Array.from(document.querySelectorAll("[data-marker='messagesHistory']"))
                .find((history) => isMessengerHistoryOpenedAfterMark(history));
            if (hasMessage(openedHistory)) {
                return true;
            }

            if (/\/profile\/messenger\/channel\//i.test(window.location.href)) {
                const history = document.querySelector("[data-marker='messagesHistory']");
                return hasMessage(history || document);
            }

            return false;
        }
        """;

    /// <summary>URL канала: из шапки мини-чата или из адреса полноэкранного мессенджера.</summary>
    public static string BuildResolveMessengerChannelUrlExpression() =>
        """
        (() => {
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

            // У Avito ссылка канала находится в шапке, а messagesHistory — в соседнем блоке.
            // Нельзя искать ссылку как потомка истории: в таком случае URL всегда пустой.
            const mini = Array.from(document.querySelectorAll("a[data-marker='mini-messenger/messenger-page-link']"))
                .find((link) => {
                    if (!isVisible(link)) {
                        return false;
                    }

                    const root = link.closest("[class*='channel-module-root']");
                    return isVisible(root) && !!root.querySelector("[data-marker='messagesHistory']");
                });
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

    /// <summary>
    /// Прокрутка истории мини-чата и сбор сообщений (после клика «Перейти в чат»).
    /// На странице откликов в DOM часто два списка: пустой глобальный виджет и оверлей кандидата —
    /// выбираем корень по видимой ссылке канала. История может не пересоздаваться при переходе
    /// между карточками, поэтому метка до клика служит только резервным вариантом.
    /// </summary>
    public static string BuildScrollAndCollectMiniMessengerMessagesScript() =>
        $$"""
        (() => {
        {{StateStoreHelpersJs}}
            const normalize = (text) => (text ?? "").replace(/\s+/g, " ").trim();
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

            const readNodeText = (node) => {
                if (!node) {
                    return "";
                }

                return normalize((node.textContent ?? "") || (node.innerText ?? ""));
            };

            const readText = (message) => {
                const selectors = [
                    "[data-marker='messageText']",
                    "[data-marker='platformMessage/text']",
                    "[data-marker='messageChunk']"
                ];
                for (const selector of selectors) {
                    const text = readNodeText(message.querySelector(selector));
                    if (text) {
                        return text;
                    }
                }

                return "";
            };

            // The responses list has two layouts and only one of them exposes the
            // candidate photo. The active chat header is available in both layouts.
            // Accept only a real Avito image; generated stub avatars are initials,
            // for which Orbita already has its own fallback.
            const normalizeAvatarUrl = (value) => {
                const raw = String(value ?? "").trim();
                if (!raw) {
                    return "";
                }

                try {
                    const parsed = new URL(raw, window.location.href);
                    const host = parsed.hostname.toLowerCase();
                    return parsed.protocol === "https:"
                        && (host === "img.avito.st" || host.endsWith(".img.avito.st"))
                        && parsed.pathname.startsWith("/image/")
                        ? parsed.href
                        : "";
                } catch (_) {
                    return "";
                }
            };

            const readAvatarUrl = (channelRoot) => {
                const image = channelRoot?.querySelector("img[data-marker='avatar/image']");
                if (!image) {
                    return "";
                }

                const direct = [image.currentSrc, image.getAttribute("src")]
                    .map(normalizeAvatarUrl)
                    .find(Boolean);
                if (direct) {
                    return direct;
                }

                const candidates = String(image.getAttribute("srcset") ?? "")
                    .split(",")
                    .map((entry) => normalizeAvatarUrl(entry.trim().split(/\s+/)[0]))
                    .filter(Boolean);
                return candidates.length > 0 ? candidates[candidates.length - 1] : "";
            };

            const collectFrom = (root) => {
                const messages = [];
                if (!root) {
                    return messages;
                }

                for (const message of root.querySelectorAll("[data-marker='message']")) {
                    const text = readText(message);
                    if (!text) {
                        continue;
                    }

                    const className = String(message.className ?? "");
                    const side = /message-base-module-right/i.test(className) ? "right" : "left";
                    const isPlatform = !!message.querySelector("[data-marker='platformMessage/text']");
                    const timeEl = message.querySelector("time[datetime]");
                    const at = timeEl?.getAttribute("datetime") ?? "";
                    messages.push({ text, at, side, isPlatform });
                }

                return messages;
            };

            const links = Array.from(document.querySelectorAll("a[data-marker='mini-messenger/messenger-page-link']"));
            const miniLink = links.find((link) => {
                if (!isVisible(link)) {
                    return false;
                }

                const root = link.closest("[class*='channel-module-root']");
                return isVisible(root) && !!root.querySelector("[data-marker='messagesHistory']");
            });
            const miniRoot = miniLink?.closest("[class*='channel-module-root']") || null;
            const histories = Array.from(document.querySelectorAll("[data-marker='messagesHistory']"));
            const visibleHistoryCount = histories.filter((history) => isVisible(history)
                || isVisible(history.querySelector("[data-marker='messagesHistory/list']"))).length;
            const markedBefore = lfState().messengerMarkedBefore;
            const openedHistory = histories
                .find((history) => (isVisible(history)
                        || isVisible(history.querySelector("[data-marker='messagesHistory/list']")))
                    && !(markedBefore instanceof WeakSet && markedBefore.has(history)));
            const isChannelPage = /\/profile\/messenger\/channel\//i.test(window.location.href);
            const channelRoot = miniRoot
                || openedHistory?.closest("[class*='channel-module-root']")
                || (isChannelPage ? document : null);
            const root = miniRoot
                || openedHistory
                || (isChannelPage
                    ? document.querySelector("[data-marker='messagesHistory']") || document
                    : null);
            const avatarUrl = readAvatarUrl(channelRoot);
            const diagnostics = {
                hasMiniLink: !!miniLink,
                hasMiniRoot: !!miniRoot,
                usedMarkedHistoryFallback: !miniRoot && !!openedHistory,
                isChannelPage,
                historyCount: histories.length,
                visibleHistoryCount,
                rootMessageNodeCount: root?.querySelectorAll("[data-marker='message']").length ?? 0,
                hasMessagesList: !!root?.querySelector("[data-marker='messagesHistory/list']")
            };
            if (!root) {
                return JSON.stringify({ ok: false, reason: "no_active_candidate_messenger", diagnostics, avatarUrl, messages: [] });
            }

            const messages = collectFrom(root);
            const list = root.querySelector("[data-marker='messagesHistory/list']");
            if (!list && messages.length === 0) {
                return JSON.stringify({ ok: false, reason: "no_messages_list", diagnostics, avatarUrl, messages: [] });
            }

            // Скролл истории вверх делает C# CDP-колесом по listRect; JS не двигает страницу.
            var listRect = null;
            if (list) {
                const rect = list.getBoundingClientRect();
                listRect = {
                    x: rect.x,
                    y: rect.y,
                    width: rect.width,
                    height: rect.height,
                    clientHeight: list.clientHeight
                };
            }

            return JSON.stringify({
                ok: true,
                reason: messages.length === 0 ? "no_message_text" : null,
                diagnostics,
                avatarUrl,
                listRect,
                messages,
                count: messages.length
            });
        })();
        """;

    /// <summary>Легаси-прокрутка истории вверх (fallback, если колесо недоступно).</summary>
    public static string BuildScrollMessengerHistoryBackScript() =>
        """
        (() => {
            const roots = Array.from(document.querySelectorAll("[data-marker='messagesHistory']"));
            const isVisible = (element) => {
                if (!element) return false;
                const style = window.getComputedStyle(element);
                if (style.display === "none" || style.visibility === "hidden") return false;
                const rect = element.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };
            const list = roots
                .map((root) => root.querySelector("[data-marker='messagesHistory/list']"))
                .find((element) => isVisible(element));
            if (!list) {
                return JSON.stringify({ ok: false });
            }

            const step = Math.max(Math.floor(list.clientHeight * 0.65), 180);
            try {
                list.scrollBy({ top: -step, left: 0, behavior: "auto" });
            } catch {
                list.scrollTop = Math.max(0, list.scrollTop - step);
            }

            return JSON.stringify({ ok: true });
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

    /// <summary>
    /// Поиск первой замаскированной кнопки «телефон» без клика (trusted-клик делает C# через CDP).
    /// Возвращает { items, masked, targetIndex }; targetIndex = -1 — цели нет.
    /// </summary>
    public static string BuildFindMaskedPhoneTargetScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            const orderedIndexes = orderedRevealIndexes(items);
            let masked = 0;
            let failed = 0;
            let targetIndex = -1;
            for (const index of orderedIndexes) {
                if (hasFailedPhoneReveal(index)) {
                    failed++;
                    continue;
                }

                const item = items[index];
                const btn = item.querySelector("[data-marker='job-application/phone']");
                if (!btn || btn.closest?.("[data-marker^='download-report-button']")) {
                    continue;
                }

                const raw = btn.textContent ?? "";
                if (!/\*/.test(raw)) {
                    continue;
                }

                masked++;
                clearCachedPhone(index);
                if (targetIndex < 0) {
                    targetIndex = index;
                }
            }

            return JSON.stringify({ items: items.length, masked, failed, targetIndex });
        })();
        """;

    /// <summary>Легаси-вариант: найти и кликнуть маску одним evaluate (без CDP-указателя). Возвращает index клика.</summary>
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
            const orderedIndexes = orderedRevealIndexes(items);
            let masked = 0;
            let clicked = 0;
            let clickedIndex = -1;
            for (const index of orderedIndexes) {
                if (hasFailedPhoneReveal(index)) {
                    continue;
                }

                const item = items[index];
                const btn = item.querySelector("[data-marker='job-application/phone']");
                if (!btn || btn.closest?.("[data-marker^='download-report-button']")) {
                    continue;
                }

                const raw = btn.textContent ?? "";
                if (!/\*/.test(raw)) {
                    continue;
                }

                // Под маской всегда кликаем — даже если карточка «известна» (phone-watch / смена номера).
                masked++;
                clearCachedPhone(index);
                if (clicked > 0) {
                    continue;
                }

                humanClick(pickPhoneClickTarget(btn));
                clicked++;
                clickedIndex = index;
            }

            return JSON.stringify({ items: items.length, masked, clicked, index: clickedIndex });
        })();
        """;

    /// <summary>
    /// Поиск цели popup-раскрытия без клика (trusted-клик делает C#). kind: call-button | masked-phone.
    /// closedExisting=true, если был открыт старый popup (C# закрывает trusted-способом и повторяет).
    /// </summary>
    public static string BuildFindContactsPopupTargetScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let pending = 0;
            for (let index = 0; index < items.length; index++) {
                if (!hasFailedPhoneReveal(index) && needsPhoneReveal(items[index], index)) {
                    pending++;
                }
            }

            if (pending === 0) {
                return JSON.stringify({ items: items.length, pending: 0, targetIndex: -1, kind: "none" });
            }

            if (isContactsPopupOpen()) {
                return JSON.stringify({
                    items: items.length,
                    pending,
                    targetIndex: -1,
                    kind: "none",
                    closedExisting: true
                });
            }

            const orderedIndexes = orderedRevealIndexes(items);
            for (const index of orderedIndexes) {
                if (hasFailedPhoneReveal(index)) {
                    continue;
                }

                const item = items[index];
                if (!needsPhoneReveal(item, index)) {
                    continue;
                }

                if (item.querySelector("[data-marker='job-application/call-button']")) {
                    return JSON.stringify({ items: items.length, pending, targetIndex: index, kind: "call-button" });
                }

                if (/\*/.test(item.querySelector("[data-marker='job-application/phone']")?.textContent ?? "")) {
                    return JSON.stringify({ items: items.length, pending, targetIndex: index, kind: "masked-phone" });
                }
            }

            return JSON.stringify({ items: items.length, pending, targetIndex: -1, kind: "none", reason: "no_popup_target" });
        })();
        """;

    /// <summary>
    /// Клик по одному номеру через popup «Показать номер телефона» (новый UX: call-button → contacts-popup).
    /// Ожидание результата — на стороне C# (<see cref="BuildContactsPopupProbeScript"/>), без busy-wait в JS.
    /// </summary>
    public static string BuildRevealNextContactsPopupPhoneScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const items = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let pending = 0;
            for (let index = 0; index < items.length; index++) {
                if (!hasFailedPhoneReveal(index) && needsPhoneReveal(items[index], index)) {
                    pending++;
                }
            }

            if (pending === 0) {
                return JSON.stringify({
                    items: items.length,
                    pending: 0,
                    clicked: false,
                    revealed: false,
                    index: -1
                });
            }

            if (isContactsPopupOpen()) {
                closeContactsPopup();
                return JSON.stringify({
                    items: items.length,
                    pending,
                    clicked: false,
                    revealed: false,
                    closedExisting: true,
                    index: -1
                });
            }

            let targetIndex = -1;
            let targetItem = null;
            const orderedIndexes = orderedRevealIndexes(items);
            for (const index of orderedIndexes) {
                if (hasFailedPhoneReveal(index)) {
                    continue;
                }

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
                    reason: "no_popup_target",
                    index: -1
                });
            }

            const clickResult = clickPhoneRevealTarget(targetItem);
            return JSON.stringify({
                items: items.length,
                pending,
                clicked: !!clickResult.clicked,
                revealed: false,
                index: targetIndex,
                kind: clickResult.kind
            });
        })();
        """;

    /// <summary>Снимок popup контактов; при готовом номере пишет кэш. Закрытие — C# trusted-кликом.</summary>
    public static string BuildContactsPopupProbeScript(int targetIndex, bool closeOnReady = false) =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            const index = {{targetIndex}};
            const snap = snapshotContactsPopupState();
            if (snap.state === "ready" && snap.phone) {
                const store = initRevealedPhonesStore();
                if (index >= 0) {
                    store[String(index)] = snap.phone;
                }

                // Раскрытый номер больше не цель: сдвигаем round-robin курсор,
                // чтобы следующий выбор начинался глубже списка.
                advanceRevealCursor();

                if ({{(closeOnReady ? "true" : "false")}}) {
                    closeContactsPopup();
                }
            }

            return JSON.stringify({
                state: snap.state,
                phone: snap.phone,
                revealed: snap.state === "ready" && !!snap.phone,
                index
            });
        })();
        """;

    public static string BuildCloseContactsPopupScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            closeContactsPopup();
            return JSON.stringify({ ok: true });
        })();
        """;

    /// <summary>
    /// Пометить карточку «клик не дал номера в этом проходе»: до конца прохода
    /// повторно её не кликаем (в следующем проходе store сбрасывается).
    /// </summary>
    public static string BuildMarkPhoneRevealFailedScript(int index) =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            markPhoneRevealFailed({{index}});
            return JSON.stringify({ ok: true, index: {{index}} });
        })();
        """;

    /// <summary>
    /// Сдвинуть round-robin курсор выбора целей раскрытия (например, после
    /// inline-раскрытия, которое не проходит через popup-проб).
    /// </summary>
    public static string BuildAdvanceRevealCursorScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
            advanceRevealCursor();
            return JSON.stringify({ ok: true, cursor: getRevealCursor() });
        })();
        """;

    public static string BuildExtractionScript() =>
        $$"""
        (() => {
        {{ContactsPhoneHelpersJs}}
        {{AgeParseHelpersJs}}
        {{VacancyParseHelpersJs}}
            initRevealedPhonesStore();

            const itemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;
            const title = (document.title ?? "").trim();
            const bodyText = document.body?.innerText ?? "";
            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form"
            ) || location.hash === "#block"
              || !!document.querySelector('a[href*="support.avito.ru/request/720"]');
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha|Отключить\s+VPN|самол[её]те/i.test(title + "\n" + bodyText);
            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasIpDialog = !!document.querySelector('[role="dialog"][aria-modal="true"], [aria-modal="true"]')
              && /Доступ\s+ограничен|проблема\s+с\s+IP/i.test(title + "\n" + bodyText);
            const hasCaptcha =
                hasIpDialog ||
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

            const parseVacancyAndCity = parseVacancyAndCityFromRoot;

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

            // The responses list has the authoritative response date even when the
            // mini-chat is empty.  Keep it as an ISO instant so the server does not
            // have to guess from `DateTime.UtcNow` or parse localized Russian text.
            // Второй вид страницы отдаёт старые отклики как «15 сент.» — сокращённый
            // месяц без времени; без времени берём начало суток.
            const parseResponseAt = (root) => {
                const anchor = root.querySelector("[data-marker='job-application/link/to-resume']");
                const text = (anchor?.textContent ?? root.textContent ?? "").replace(/\u00a0/g, " ").replace(/\s+/g, " ").trim();
                if (!text) return "";

                const monthNames = ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];
                const monthIndex = (token) => {
                    const normalized = (token ?? "").toLowerCase().replace(/\.$/, "");
                    return monthNames.findIndex((name) => name === normalized || name.startsWith(normalized));
                };
                // «сегодня, 09:41» / «вчера, 23:37» / «Сегодня в 00:33»
                const timeMatch = text.match(/(?:сегодня|вчера)(?:\s*,|\s+в)?\s*(\d{1,2}):(\d{2})/i);
                // «6 сентября в 01:02» — полное имя месяца, время обязательно.
                const calendarMatch = text.match(/(?:^|\s)(\d{1,2})\s+(января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)(?:\s+в)?\s+(\d{1,2}):(\d{2})/i);
                // «15 сент.» — сокращённый месяц второго вида страницы, времени может не быть.
                const shortCalendarMatch = text.match(/(?:^|\s)(\d{1,2})\s+(янв|февр|мар|апр|мая|июн|июл|авг|сент|окт|нояб|дек)\.?(?:\s+в)?\s*(?:(\d{1,2}):(\d{2}))?/i);
                const now = new Date();
                let year = now.getFullYear();
                let month = now.getMonth();
                let day = now.getDate();
                let hours = 0;
                let minutes = 0;
                if (timeMatch) {
                    hours = Number(timeMatch[1]);
                    minutes = Number(timeMatch[2]);
                    if (/вчера/i.test(timeMatch[0])) {
                        const yesterday = new Date(year, month, day - 1);
                        year = yesterday.getFullYear(); month = yesterday.getMonth(); day = yesterday.getDate();
                    }
                } else {
                    const calendar = calendarMatch ?? shortCalendarMatch;
                    if (!calendar) return "";
                    day = Number(calendar[1]);
                    month = monthIndex(calendar[2]);
                    if (month < 0) return "";
                    hours = calendar[3] === undefined ? 0 : Number(calendar[3]);
                    minutes = calendar[4] === undefined ? 0 : Number(calendar[4]);
                    const candidate = new Date(year, month, day, hours, minutes);
                    if (candidate.getTime() > now.getTime() + 86400000) year--;
                }

                const result = new Date(year, month, day, hours, minutes);
                return Number.isNaN(result.getTime()) ? "" : result.toISOString();
            };

            // A real avatar is hosted at {shard}.img.avito.st/image/… .
            // Deliberately reject Avito's generated /static/ims/ placeholders: the panel
            // falls back to candidate initials when the person did not upload a photo.
            const normalizeAvatarUrl = (value) => {
                const normalized = normalizeUrl(value ?? "");
                if (!normalized) {
                    return "";
                }

                try {
                    const parsed = new URL(normalized, window.location.href);
                    const host = parsed.hostname.toLowerCase();
                    return parsed.protocol === "https:"
                        && (host === "img.avito.st" || host.endsWith(".img.avito.st"))
                        && parsed.pathname.startsWith("/image/")
                        ? parsed.href
                        : "";
                } catch (_) {
                    return "";
                }
            };

            const extractBackgroundImageUrl = (value) => {
                const match = String(value ?? "").match(/url\(\s*['"]?(.+?)['"]?\s*\)/i);
                return match ? normalizeAvatarUrl(match[1]) : "";
            };

            const resolveAvatarUrl = (root, fullName) => {
                const images = Array.from(root.querySelectorAll("img[src]"));
                const exactNameImage = images.find((image) =>
                    (image.getAttribute("alt") ?? "").trim() === fullName);
                const fromNamedImage = normalizeAvatarUrl(exactNameImage?.getAttribute("src"));
                if (fromNamedImage) {
                    return fromNamedImage;
                }

                // In another Avito card variant the photo is a div background, not img.
                for (const element of root.querySelectorAll("[style*='background-image']")) {
                    const fromBackground = extractBackgroundImageUrl(element.style?.backgroundImage ?? element.getAttribute("style"));
                    if (fromBackground) {
                        return fromBackground;
                    }
                }

                return "";
            };

            const listItems = Array.from(document.querySelectorAll("[data-marker='job-application/item']"));
            let missingPhoneCount = 0;
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
                const enrichmentState = lfState().detailEnrichment;
                const enriched = enrichmentState
                    ? (enrichmentState[phoneKey] ?? enrichmentState[String(rootIndex)])
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
                const avatarUrl = resolveAvatarUrl(root, name);
                const sourceResponseId = buildSourceResponseId(name, phone, vacancy, city, vacancyUrl);
                const responseAt = parseResponseAt(root);

                return {
                    fullName: name,
                    phone,
                    age: ageText,
                    gender,
                    vacancy,
                    city,
                    vacancyUrl,
                    messengerUrl,
                    avatarUrl,
                    sourceResponseId,
                    responseAt,
                    domIndex: rootIndex,
                    rawText
                };
            }).filter((item) => {
                if (!item.fullName) {
                    return false;
                }

                // Карточка без раскрытого номера: не попадает в выборку. Считаем её
                // «ожидающей раскрытия» только если это карточка списка (domIndex >= 0;
                // корни вне списка — панель кандидата — не являются целью раскрытия)
                // и она не помечена skip (известный дубль/профиль/фильтр).
                if (!item.phone
                    || /\*/.test(item.phone)
                    || item.phone.replace(/\D/g, "").length < 10) {
                    if (item.domIndex >= 0 && !shouldSkipPhoneReveal(item.domIndex)) {
                        missingPhoneCount++;
                    }

                    return false;
                }

                return true;
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
                domStatusCount: statusButtons.length,
                missingPhoneCount
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
