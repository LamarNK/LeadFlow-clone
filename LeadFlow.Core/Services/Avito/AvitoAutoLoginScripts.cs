using System.Text.Json;

namespace LeadFlow.Core.Services.Avito;

/// <summary>JS-шаги автовхода Avito через сохранённый пароль в профиле AdsPower или credentials из Орбиты.</summary>
public static class AvitoAutoLoginScripts
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
            ) || location.hash === "#block"
              || !!document.querySelector('a[href*="support.avito.ru/request/720"]');
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha|Отключить\s+VPN|самол[её]те/i.test(probeText);
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
                document.querySelector("[data-marker='users-list']") ||
                document.querySelector("[data-marker^='users-list(']") ||
                document.querySelector("[data-marker='user/link']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']")
            );
            const hasUsersList = !!(
                document.querySelector("[data-marker='users-list']") ||
                document.querySelector("[data-marker^='users-list(']") ||
                document.querySelector("[data-marker='users-list/button']")
            );
            const hasSavedUserCard = !!(
                document.querySelector("[data-marker='user/link']") ||
                document.querySelector("[data-marker='login-form-with-avatar'] [data-marker='user/link']") ||
                document.querySelector("[data-marker='login-form-with-avatar'] button") ||
                document.querySelector("[data-marker='auth-app-root'] [data-marker='user/link']")
            );
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

            const findOtherProfileLink = () =>
                document.querySelector("[data-marker='login-form/other']") ||
                document.querySelector("[data-marker='login-form/other-profile']") ||
                document.querySelector("[data-marker*='other-profile']") ||
                document.querySelector("[data-marker='users-list/button']") ||
                document.querySelector("[data-marker='another-profile-link'] a") ||
                Array.from(document.querySelectorAll("a, button, span, div[role='button']"))
                    .find((el) => /войти\s+в\s+другой\s+профиль/i.test((el.textContent || "").trim()));

            const hasOtherProfileLink = !!findOtherProfileLink();

            const loginInput =
                document.querySelector("[data-marker='login-form/login/input']") ||
                document.querySelector("[data-marker='login-form/login'] input") ||
                document.querySelector("input[name='login']") ||
                document.querySelector("input[autocomplete='username']") ||
                document.querySelector("form[data-marker='login-form'] input:not([type='password']):not([type='hidden'])");
            const passwordInput =
                document.querySelector("[data-marker='login-form/password/input']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']") ||
                document.querySelector("form[data-marker='login-form'] input[name='password']") ||
                document.querySelector("input[type='password']");
            const hasCredentialInputs = !!(loginInput || passwordInput);
            const hasPasswordValue = !!(passwordInput && String(passwordInput.value || "").trim());

            // Экран выбора: карточка «База» / список профилей, ещё без полей логина/пароля.
            const hasProfileChooser =
                ((hasUsersList || hasSavedUserCard || hasOtherProfileLink ||
                  !!document.querySelector("[data-marker='login-form-with-avatar']")) &&
                 !hasCredentialInputs);

            const needsLogin =
                hasLoginDom ||
                hasUsersList ||
                hasSavedUserCard ||
                hasOtherProfileLink ||
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
                hasSavedUserCard,
                hasOtherProfileLink,
                hasProfileChooser,
                hasCredentialInputs,
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
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerdown", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mousedown", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerup", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mouseup", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
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

    /// <summary>Кликает карточку сохранённого профиля («База» / users-list).</summary>
    public static string BuildSelectSavedUserScript() =>
        """
        (() => {
            const tryClick = (el) => {
                if (!el) return false;
                try {
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerdown", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mousedown", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerup", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mouseup", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
                    el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof el.click === "function") el.click();
                    return true;
                } catch {
                    return false;
                }
            };

            const isOtherProfile = (el) =>
                /войти\s+в\s+другой\s+профиль/i.test((el?.textContent || "").trim());

            const candidates = [
                document.querySelector("[data-marker='users-list'] [data-marker='user/link']"),
                document.querySelector("[data-marker^='users-list('] [data-marker='user/link']"),
                document.querySelector("[data-marker='users-list'] button[data-marker='user/link']"),
                document.querySelector("[data-marker='user'] [data-marker='user/link']"),
                document.querySelector("button[data-marker='user/link']"),
                document.querySelector("[data-marker='login-form-with-avatar'] [data-marker='user/link']"),
                document.querySelector("[data-marker='auth-app-root'] [data-marker='user/link']"),
                document.querySelector("[data-marker='user/link']"),
                document.querySelector("[data-marker^='users-list(']"),
                document.querySelector("[data-marker='login-form-with-avatar'] button:not([type='submit'])"),
                document.querySelector("[data-marker='login-form-with-avatar'] [role='button']"),
                document.querySelector("[data-marker='login-form-with-avatar'] a"),
            ];

            for (const el of candidates) {
                if (el && !isOtherProfile(el) && tryClick(el)) {
                    return { clicked: true, step: "saved_user" };
                }
            }

            // Fallback: кликабельная карточка с телефоном (+7 …) внутри модалки входа.
            const roots = [
                document.querySelector("[data-marker='users-list']"),
                document.querySelector("[data-marker='login-form-with-avatar']"),
                document.querySelector("[data-marker='auth-app-root']"),
                document.querySelector("[data-marker='login-form']"),
            ].filter(Boolean);

            for (const root of roots) {
                const card = Array.from(root.querySelectorAll("button, a, [role='button'], div"))
                    .find((el) => {
                        if (isOtherProfile(el)) return false;
                        const t = (el.textContent || "").trim();
                        return /\+7[\s\d\-()]{8,}/.test(t) || /data-marker=['"]user\/link/.test(el.outerHTML || "");
                    });
                if (tryClick(card)) return { clicked: true, step: "phone_card" };
            }

            return { clicked: false, step: "none" };
        })();
        """;

    /// <summary>
    /// Кликает «Войти в другой профиль» на экране выбора сохранённого аккаунта,
    /// чтобы открыть поля логина/пароля (credentials из Орбиты).
    /// </summary>
    public static string BuildSwitchToOtherProfileScript() =>
        """
        (() => {
            const tryClick = (el) => {
                if (!el) return false;
                try {
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerdown", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mousedown", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
                    if (typeof PointerEvent === "function") {
                        el.dispatchEvent(new PointerEvent("pointerup", {
                            bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
                        }));
                    }
                    el.dispatchEvent(new MouseEvent("mouseup", {
                        bubbles: true, cancelable: true, button: 0, view: window
                    }));
                    el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof el.click === "function") el.click();
                    return true;
                } catch {
                    return false;
                }
            };

            const byMarker =
                document.querySelector("[data-marker='login-form/other']") ||
                document.querySelector("[data-marker='login-form/other-profile']") ||
                document.querySelector("[data-marker*='other-profile']") ||
                document.querySelector("[data-marker='users-list/button']") ||
                document.querySelector("[data-marker='another-profile-link'] a");
            if (tryClick(byMarker)) return { clicked: true, step: "marker" };

            const byText = Array.from(document.querySelectorAll("a, button, span, div[role='button']"))
                .find((el) => /войти\s+в\s+другой\s+профиль|вернуться\s+к\s+списку/i.test((el.textContent || "").trim()));
            if (tryClick(byText)) return { clicked: true, step: "text" };

            return { clicked: false, step: "none" };
        })();
        """;

    /// <summary>Заполняет login/password из credentials Орбиты и отправляет форму.</summary>
    public static string BuildFillCredentialsAndSubmitScript(string login, string password)
    {
        var loginJson = JsonSerializer.Serialize(login);
        var passwordJson = JsonSerializer.Serialize(password);
        return $$"""
        (() => {
            const login = {{loginJson}};
            const password = {{passwordJson}};

            const setNativeValue = (el, value) => {
                if (!el) return false;
                try {
                    el.focus();
                    const proto = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, "value");
                    if (proto && typeof proto.set === "function") {
                        proto.set.call(el, value);
                    } else {
                        el.value = value;
                    }
                    try {
                        const tracker = el._valueTracker;
                        if (tracker && typeof tracker.setValue === "function") tracker.setValue("");
                    } catch { /* React tracker is optional */ }
                    el.dispatchEvent(new InputEvent("input", {
                        bubbles: true, data: value, inputType: "insertText"
                    }));
                    el.dispatchEvent(new Event("input", { bubbles: true }));
                    el.dispatchEvent(new Event("change", { bubbles: true }));
                    el.dispatchEvent(new Event("blur", { bubbles: true }));
                    return String(el.value || "") === String(value);
                } catch {
                    try { el.value = value; return true; } catch { return false; }
                }
            };

            const findLogin = () =>
                document.querySelector("[data-marker='login-form/login/input']") ||
                document.querySelector("[data-marker='login-form/login'] input") ||
                document.querySelector("input[name='login'][autocomplete='username']") ||
                document.querySelector("input[name='login']") ||
                document.querySelector("input[autocomplete='username']") ||
                document.querySelector("input[type='tel']") ||
                document.querySelector("input[type='email']") ||
                document.querySelector("form[data-marker='login-form'] input:not([type='password']):not([type='hidden'])");

            const findPassword = () =>
                document.querySelector("[data-marker='login-form/password/input']") ||
                document.querySelector("input[name='password'][autocomplete='current-password']") ||
                document.querySelector("form[data-marker='login-form'] input[name='password']") ||
                document.querySelector("input[type='password']");

            const findSubmit = () =>
                document.querySelector("[data-marker='login-form/submit']") ||
                document.querySelector("form[data-marker='login-form'] button[type='submit']") ||
                document.querySelector("button[name='submit'][type='submit']") ||
                document.querySelector("button[type='submit']");

            const findForm = () =>
                document.querySelector("form[data-marker='login-form']") ||
                (findPassword() && findPassword().closest("form")) ||
                null;

            const loginInput = findLogin();
            const passwordInput = findPassword();
            let filledLogin = false;
            let filledPassword = false;

            // После выбора сохранённого профиля login уже привязан (часто readonly/hidden) —
            // не затираем его; на этом экране нужен только пароль из Орбиты.
            const loginLocked = !!(
                loginInput &&
                (loginInput.readOnly ||
                 loginInput.disabled ||
                 loginInput.type === "hidden" ||
                 (loginInput.style && loginInput.style.display === "none") ||
                 getComputedStyle(loginInput).display === "none")
            );
            const passwordOnlyForm = !!(passwordInput && (!loginInput || loginLocked));

            if (loginInput && login && !passwordOnlyForm) {
                filledLogin = setNativeValue(loginInput, login);
                try { loginInput.focus(); } catch { /* best effort */ }
            }

            if (passwordInput && password) {
                filledPassword = setNativeValue(passwordInput, password);
                try {
                    passwordInput.focus();
                    passwordInput.dispatchEvent(new Event("focus", { bubbles: true }));
                    passwordInput.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, key: "a" }));
                    passwordInput.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true, key: "a" }));
                } catch { /* best effort */ }
            }

            const loginValue = loginInput ? String(loginInput.value || "").trim() : "";
            const passwordValue = passwordInput ? String(passwordInput.value || "").trim() : "";
            const hasPassword = passwordValue.length > 0;
            const hasLogin = loginValue.length > 0;
            const digits = (value) => String(value || "").replace(/\D/g, "");
            const loginMatches =
                passwordOnlyForm ||
                (digits(loginValue).length >= 10 && digits(loginValue) === digits(login));
            const passwordMatches = passwordValue === String(password);
            // Login-only step (phone first): submit without password field / value.
            // Password-only (saved profile): достаточно заполненного пароля.
            const canSubmit = hasPassword || (hasLogin && !passwordInput);

            if (!canSubmit || !passwordMatches || !loginMatches) {
                return {
                    submitted: false,
                    reason: !passwordMatches ? "orbit_password_not_applied" :
                        (!loginMatches ? "orbit_login_not_applied" :
                            (passwordInput ? "no_password" : "no_fields")),
                    filledLogin,
                    filledPassword,
                    hasLogin,
                    hasPassword,
                    loginMatches,
                    passwordMatches
                };
            }

            const submit = findSubmit();
            const form = findForm();
            if (!submit && !form) {
                return { submitted: false, reason: "no_submit", filledLogin, filledPassword, hasLogin, hasPassword };
            }

            try {
                let method = "click";
                if (form && typeof form.requestSubmit === "function") {
                    if (submit) form.requestSubmit(submit);
                    else form.requestSubmit();
                    method = "requestSubmit";
                } else if (submit) {
                    submit.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: window }));
                    if (typeof submit.click === "function") submit.click();
                } else {
                    form.dispatchEvent(new Event("submit", { bubbles: true, cancelable: true }));
                    method = "submit_event";
                }
                return {
                    submitted: true,
                    reason: hasPassword ? "orbit_login_password" : "orbit_login_only",
                    method,
                    filledLogin,
                    filledPassword,
                    hasLogin,
                    hasPassword,
                    loginMatches,
                    passwordMatches
                };
            } catch {
                return { submitted: false, reason: "click_failed", filledLogin, filledPassword, hasLogin, hasPassword };
            }
        })();
        """;
    }
}
