namespace LeadFlow.Core.Services.Avito;

/// <summary>JS-шаги автовхода Avito через сохранённый пароль в профиле AdsPower.</summary>
internal static class AvitoAutoLoginScripts
{
    /// <summary>Возвращает объект состояния для принятия решения о следующем шаге.</summary>
    public static string BuildProbeScript() =>
        """
        (() => {
            const url = window.location.href ?? "";
            const bodyText = (document.body?.innerText ?? "").slice(0, 8000);
            const htmlSnippet = (document.documentElement?.innerHTML ?? "").slice(0, 16000);
            const probeText = bodyText + "\n" + htmlSnippet;

            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form, h2.firewall-title"
            );
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha/i.test(probeText);
            const hasCaptchaWidget = !!(
                document.getElementById("geetest_captcha") ||
                document.getElementById("inner-captcha") ||
                document.getElementById("h-captcha") ||
                document.querySelector(".h-captcha[data-sitekey]")
            );
            const hasCaptcha = hasFirewallDom || hasFirewallText || hasCaptchaWidget;

            const hasLoginDom = !!(
                document.querySelector("[data-marker='auth-app-root']") ||
                document.querySelector("[data-marker='login-form']") ||
                document.querySelector("[data-marker='login-form-with-avatar']") ||
                document.querySelector("[data-marker='login-form/login']") ||
                document.querySelector("[data-marker='login-form/password']") ||
                document.querySelector("[data-marker='login-form/submit']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']")
            );
            const hasUsersList = !!document.querySelector("[data-marker='users-list']");
            const hasGuestLoginButton = !!document.querySelector("[data-marker='header/login-button']");
            const hasLoggedInProfile = !!(
                document.querySelector("[data-marker='header/profile-name']") ||
                document.querySelector("[data-marker='profile-switch/link']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/profile/name']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/profile/avatar']") ||
                document.querySelector("[data-marker='osp-sidebar/tools/money']")
            );
            const urlSuggestsLogin =
                /\/profile\/login|\/profile\/auth|avito\.ru\/login|#login\b|\/auth\b/i.test(url);

            const passwordInput =
                document.querySelector("[data-marker='login-form/password/input']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']") ||
                document.querySelector("form[data-marker='login-form'] input[name='password']");
            const hasPasswordValue = !!(passwordInput && String(passwordInput.value || "").trim());

            const needsLogin =
                hasLoginDom ||
                hasUsersList ||
                urlSuggestsLogin ||
                (hasGuestLoginButton && !hasLoggedInProfile);

            const isAuthorized =
                !needsLogin &&
                !hasCaptcha &&
                (hasLoggedInProfile || /avito\.ru\/profile/i.test(url));

            return {
                needsLogin,
                isAuthorized,
                hasCaptcha,
                hasLoginForm: hasLoginDom,
                hasUsersList,
                hasGuestLoginButton,
                hasLoggedInProfile,
                hasPasswordValue,
                hasSubmitButton: !!document.querySelector("[data-marker='login-form/submit']"),
                url
            };
        })();
        """;

    /// <summary>Открывает модалку входа: кнопка «Вход и регистрация» в шапке или ссылка #login.</summary>
    public static string BuildOpenLoginScript() =>
        """
        (() => {
            const tryClick = (el) => {
                if (!el) return false;
                try {
                    el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof el.click === "function") el.click();
                    return true;
                } catch {
                    return false;
                }
            };

            const loginBtn = document.querySelector("[data-marker='header/login-button']");
            if (tryClick(loginBtn)) return { clicked: true, step: "header_login" };

            const hashLogin = Array.from(document.querySelectorAll("a[href*='#login'], a[href*='/login']"))
                .find((el) => /вход|регистрац/i.test((el.textContent || "").trim()));
            if (tryClick(hashLogin)) return { clicked: true, step: "hash_login_link" };

            return { clicked: false, step: "none" };
        })();
        """;

    /// <summary>Выбирает первый сохранённый профиль в списке «Вход».</summary>
    public static string BuildSelectSavedUserScript() =>
        """
        (() => {
            const tryClick = (el) => {
                if (!el) return false;
                try {
                    el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof el.click === "function") el.click();
                    return true;
                } catch {
                    return false;
                }
            };

            const userBtn =
                document.querySelector("[data-marker='users-list'] [data-marker='user/link']") ||
                document.querySelector("[data-marker^='users-list('] [data-marker='user/link']") ||
                document.querySelector("[data-marker='users-list'] button[data-marker='user/link']");
            if (tryClick(userBtn)) return { clicked: true };

            return { clicked: false };
        })();
        """;

    /// <summary>Фокус на поле пароля (для автозаполнения) и отправка формы, если пароль уже в браузере.</summary>
    public static string BuildSubmitPasswordFormScript() =>
        """
        (() => {
            const pwd =
                document.querySelector("[data-marker='login-form/password/input']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']") ||
                document.querySelector("form[data-marker='login-form'] input[name='password']");
            if (pwd) {
                try {
                    pwd.focus();
                    pwd.dispatchEvent(new Event("focus", { bubbles: true }));
                    pwd.dispatchEvent(new Event("input", { bubbles: true }));
                } catch { /* best effort */ }
            }

            const hasPassword = !!(pwd && String(pwd.value || "").trim().length > 0);
            if (!hasPassword) return { submitted: false, reason: "no_password" };

            const submit =
                document.querySelector("[data-marker='login-form/submit']") ||
                document.querySelector("form[data-marker='login-form'] button[type='submit']");
            if (!submit) return { submitted: false, reason: "no_submit" };

            try {
                submit.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                if (typeof submit.click === "function") submit.click();
                return { submitted: true };
            } catch {
                return { submitted: false, reason: "click_failed" };
            }
        })();
        """;
}