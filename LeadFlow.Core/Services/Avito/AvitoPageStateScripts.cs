using LeadFlow.Core.Services.Avito.Session;

namespace LeadFlow.Core.Services.Avito;

/// <summary>JS-probe полного состояния страницы Avito (SPA: URL часто не отражает открытую модалку).</summary>
public static class AvitoPageStateScripts
{
    public static string BuildProbeScript() =>
        $$"""
        (() => {
            const url = window.location.href ?? "";
            const title = (document.title ?? "").trim();
            const bodyText = (document.body?.innerText ?? "").slice(0, 8000);
            const htmlSnippet = (document.documentElement?.innerHTML ?? "").slice(0, 16000);
            const probeText = title + "\n" + bodyText + "\n" + htmlSnippet;

            const profileCards = document.querySelectorAll("[data-marker^='component-profile-switch/profile-']");

            let currentSubProfileId = null;
            let currentSubProfileName = null;
            for (const card of profileCards) {
                const marker = card.getAttribute("data-marker") || "";
                const idMatch = marker.match(/profile-(\d+)/);
                if (!idMatch) continue;
                const isCurrent = /isCurrent/i.test(card.className || "");
                if (isCurrent) {
                    currentSubProfileId = idMatch[1];
                    const nameEl = card.querySelector("h5");
                    currentSubProfileName = (nameEl?.textContent ?? "").trim() || null;
                    break;
                }
            }

            const sidebarNameEl =
                document.querySelector("[data-marker='header/profile-name']") ||
                document.querySelector("[data-marker='profile-switch/link'] h5") ||
                document.querySelector("nav h5");
            if (!currentSubProfileName && sidebarNameEl) {
                const sidebarName = (sidebarNameEl.textContent ?? "").trim();
                if (sidebarName) currentSubProfileName = sidebarName;
            }

            const candidatesItemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const urlSuggestsLogin =
                /\/profile\/login|\/profile\/auth|avito\.ru\/login|#login\b|\/auth\b/i.test(url);
            const titleSuggestsLogin = /^вход$/i.test(title);
            // Экран сохранённых профилей (users-list → клик по карточке → пароль).
            const hasSavedUsersList = !!(
                document.querySelector("[data-marker='users-list']") ||
                document.querySelector("[data-marker^='users-list(']") ||
                document.querySelector("[data-marker='user/link']") ||
                document.querySelector("[data-marker='users-list/button']") ||
                document.querySelector("[data-marker='login-form-with-avatar']")
            );
            // Avito сбросил пароль при подозрении на взлом. Это терминальное состояние:
            // нажимать «Получить код» не нужно — код должен ввести владелец аккаунта.
            const passwordResetForm = document.querySelector(
                "[data-marker='password-was-reset'], [data-marker='password-was-reset-form']");
            const passwordResetText = (passwordResetForm?.textContent ?? "").trim();
            const requiresPasswordResetSms = !!passwordResetForm
                && /Сработала\s+защита\s+профиля/i.test(passwordResetText)
                && /Получить\s+код\s+по\s+смс/i.test(passwordResetText);
            const passwordResetPhoneSource = passwordResetForm?.querySelector(
                "[class*='PhoneNumber-module-phone'] strong, [class*='PhoneNumber-module-phone']")?.textContent ?? "";
            const passwordResetDigits = String(passwordResetPhoneSource).replace(/\D/g, "");
            const passwordResetSmsPhone = requiresPasswordResetSms && passwordResetDigits.length >= 2
                ? `+${passwordResetDigits.slice(0, 1)} *** ***-**-${passwordResetDigits.slice(-2)}`
                : null;
            const hasLoginDom = !!(
                hasSavedUsersList ||
                passwordResetForm ||
                document.querySelector("[data-marker='auth-app-root']") ||
                document.querySelector("form[data-marker='login-form']") ||
                document.querySelector("[data-marker='login-form/login']") ||
                document.querySelector("[data-marker='login-form/login/input']") ||
                document.querySelector("[data-marker='login-form/password']") ||
                document.querySelector("[data-marker='login-form/submit']") ||
                document.querySelector("[data-marker='registration-link']") ||
                document.querySelector("[data-marker='social-auth']") ||
                document.querySelector("input[name='login'][autocomplete='username']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']") ||
                document.querySelector("[class*='AuthorizationMainScreen']")
            );
            const hasLoginHtml = /data-marker=['"]auth-app-root|data-marker=['"]login-form|data-marker=['"]users-list|data-marker=['"]user\/link|AuthorizationMainScreen-module|login-form\/login|login-form\/password/i.test(htmlSnippet);
            // Тексты именно формы входа (не промо-кнопки баннеров в Pro-кабинете).
            const containsAuthText = (value) =>
                /телефон или почта|забыли пароль|запомнить пароль|продолжить через|нет аккаунта на|войти в авито|войти в другой профиль|введите пароль от/i.test(value ?? "");
            const hasLoginText = containsAuthText(probeText);
            const hasGuestLoginButton = !!document.querySelector("[data-marker='header/login-button']");
            // Pro: osp-sidebar/* — основной маркер кабинета; header/profile-name — обычный профиль.
            const hasLoggedInProfile = !!(
                document.querySelector("[data-marker='header/profile-name']") ||
                document.querySelector("[data-marker='profile-switch/link']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/profile/name']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/profile/avatar']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/money']")
            );
            const guestNeedsLogin = hasGuestLoginButton && !hasLoggedInProfile && !hasLoginDom;
            // Soft-сигналы не перебивают уже открытый кабинет (промо-баннеры и т.п.).
            const softLoginSignals = hasLoginHtml || hasLoginText || titleSuggestsLogin || urlSuggestsLogin;
            const hasLoginForm = hasLoginDom || guestNeedsLogin || (softLoginSignals && !hasLoggedInProfile);

            // Не классифицируем скрытый HTML и устаревший title: препятствие должно
            // быть видимым на текущем кадре, как в оркестраторе сессии.
            const obstacle = JSON.parse({{AvitoPageObstacleScripts.BuildProbeExpression()}});
            const hasFirewallIp = obstacle.kind === "ipBlocked";
            const hasCaptcha = hasFirewallIp || obstacle.kind === "captcha";
            const profileSwitchModalOpen = !!obstacle.profileSwitchOpen;
            const profileCardsCount = obstacle.profileSwitchCardCount | 0;
            // Баннер Avito Pro: скрытые объявления из-за нулевого/недостаточного аванса.
            // Оба текста обязательны, чтобы не принять обычный блок баланса за ошибку.
            const hasInsufficientAdvance =
                /объявления\s+не\s+видны\s+в\s+поиске/i.test(probeText)
                && /на\s+авансе\s+недостаточно\s+денег/i.test(probeText);
            const hasEmailConfirmationRequired =
                /подтвердите\s+почту\s+по\s+ссылке\s+из\s+письма/i.test(probeText);
            // Заглушка SPA: «Ошибка / Попробуйте обновить страницу…» — URL при этом остаётся /profile/pro/items.
            const hasTransientError =
                /Попробуйте\s+обновить\s+страницу\s+или\s+загляните\s+позже/i.test(probeText)
                || /обязательно\s+всё\s+починим/i.test(probeText);

            let pageKind = "unknown";
            if (hasCaptcha) {
                pageKind = "captcha";
            } else if (hasLoginForm) {
                pageKind = "login";
            } else if (hasTransientError) {
                pageKind = "transientError";
            } else if (profileSwitchModalOpen) {
                pageKind = "profileSwitchModal";
            } else if (
                url.includes("/profile/candidates")
                || url.includes("/profile/job/responses")
            ) {
                pageKind = "candidates";
            } else if (
                document.querySelector("[data-marker='filters/status-list-content']")
                || document.querySelector("[data-marker='job-crm/response/cv-button']")
            ) {
                pageKind = "candidates";
            } else if (url.includes("/profile/pro/items")) {
                pageKind = "profileItems";
            } else if (url.includes("dashboard") || url.includes("profile/switch")) {
                pageKind = "dashboard";
            } else if (candidatesItemCount > 0) {
                pageKind = "candidates";
            }

            return JSON.stringify({
                pageKind,
                url,
                title,
                profileSwitchModalOpen,
                profileCardsCount,
                currentSubProfileId,
                currentSubProfileName,
                candidatesItemCount,
                hasLoginForm,
                hasCaptcha,
                hasFirewallIp,
                hasInsufficientAdvance,
                hasEmailConfirmationRequired,
                hasTransientError,
                requiresPasswordResetSms,
                passwordResetSmsPhone
            });
        })();
        """;

    /// <summary>Кликает кнопку «Обновить» на заглушке Avito, только если виден её текст.</summary>
    public static string BuildClickRefreshOnTransientErrorScript() =>
        """
        (() => {
            const probeText = ((document.title ?? "") + "\n" + (document.body?.innerText ?? "")).slice(0, 8000);
            const isTransient =
                /Попробуйте\s+обновить\s+страницу\s+или\s+загляните\s+позже/i.test(probeText)
                || /обязательно\s+всё\s+починим/i.test(probeText);
            if (!isTransient) return false;
            const buttons = Array.from(document.querySelectorAll("button, a, [role='button']"));
            const btn = buttons.find((el) => /^\s*Обновить\s*$/i.test((el.textContent || "").trim()));
            if (!btn) return false;
            try { btn.click(); return true; } catch { return false; }
        })()
        """;
}
