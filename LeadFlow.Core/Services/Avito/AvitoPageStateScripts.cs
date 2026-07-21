namespace LeadFlow.Core.Services.Avito;

/// <summary>JS-probe полного состояния страницы Avito (SPA: URL часто не отражает открытую модалку).</summary>
public static class AvitoPageStateScripts
{
    public static string BuildProbeScript() =>
        """
        (() => {
            const url = window.location.href ?? "";
            const title = (document.title ?? "").trim();
            const bodyText = (document.body?.innerText ?? "").slice(0, 8000);
            const htmlSnippet = (document.documentElement?.innerHTML ?? "").slice(0, 16000);
            const probeText = title + "\n" + bodyText + "\n" + htmlSnippet;

            const profileSwitchModalOpen = !!document.querySelector("[data-marker='component-profile-switch/root']");
            const profileCards = document.querySelectorAll("[data-marker^='component-profile-switch/profile-']");
            const profileCardsCount = profileCards.length;

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
            const hasLoginDom = !!(
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
            const hasLoginHtml = /data-marker=['"]auth-app-root|data-marker=['"]login-form|AuthorizationMainScreen-module|login-form\/login|login-form\/password/i.test(htmlSnippet);
            // Тексты именно формы входа (не промо-кнопки баннеров в Pro-кабинете).
            const containsAuthText = (value) =>
                /телефон или почта|забыли пароль|запомнить пароль|продолжить через|нет аккаунта на|войти в авито/i.test(value ?? "");
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

            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form, h2.firewall-title"
            );
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha/i.test(probeText);
            const hasFirewallIp = hasFirewallDom || hasFirewallText;

            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasCaptcha = hasFirewallIp || hasCaptchaWidget;

            let pageKind = "unknown";
            if (hasLoginForm) {
                pageKind = "login";
            } else if (hasCaptcha) {
                pageKind = "captcha";
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
                hasFirewallIp
            });
        })();
        """;
}