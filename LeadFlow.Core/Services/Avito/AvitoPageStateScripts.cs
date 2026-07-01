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
            const hasLoginForm = !!(
                document.querySelector("form[data-marker='login-form']") ||
                document.querySelector("[data-marker='login-form/login']") ||
                document.querySelector("[data-marker='login-form/login/input']") ||
                document.querySelector("[data-marker='login-form/password']") ||
                document.querySelector("input[name='login'][autocomplete='username']") ||
                document.querySelector("[class*='AuthorizationMainScreen']")
            ) || /data-marker=['"]login-form|AuthorizationMainScreen-module|login-form\/login/i.test(htmlSnippet);

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
            } else if (url.includes("/profile/candidates")) {
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