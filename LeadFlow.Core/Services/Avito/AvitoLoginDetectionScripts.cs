namespace LeadFlow.Core.Services.Avito;

/// <summary>Общая JS-логика детекта формы входа Avito (SPA, модалка, редирект).</summary>
internal static class AvitoLoginDetectionScripts
{
    /// <summary>Возвращает объект <c>{ hasLogin, url, title }</c>.</summary>
    public static string BuildDetectionScript() =>
        """
        (() => {
            const url = window.location.href ?? "";
            const title = (document.title ?? "").trim();
            const bodyText = (document.body?.innerText ?? "").slice(0, 12000);
            const htmlSnippet = (document.documentElement?.innerHTML ?? "").slice(0, 24000);
            const probeText = `${title}\n${bodyText}\n${htmlSnippet}`;

            const urlSuggestsLogin =
                /\/profile\/login|\/profile\/auth|avito\.ru\/login|#login\b|\/auth\b/i.test(url);

            const titleSuggestsLogin = /^вход$/i.test(title);

            // Экран сохранённых профилей (users-list → клик → пароль) — отдельный UI без login-form.
            const hasSavedUsersList = !!(
                document.querySelector("[data-marker='users-list']") ||
                document.querySelector("[data-marker^='users-list(']") ||
                document.querySelector("[data-marker='user/link']") ||
                document.querySelector("[data-marker='users-list/button']") ||
                document.querySelector("[data-marker='login-form-with-avatar']")
            );

            const hasLoginDom = !!(
                hasSavedUsersList ||
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

            // Soft-сигналы (URL/title/текст/HTML-сниппет) не перебивают уже открытый кабинет.
            const softLoginSignals = hasLoginHtml || hasLoginText || titleSuggestsLogin || urlSuggestsLogin;
            const hasLogin = hasLoginDom || guestNeedsLogin || (softLoginSignals && !hasLoggedInProfile);

            return JSON.stringify({
                hasLogin,
                url,
                title,
                urlSuggestsLogin,
                titleSuggestsLogin,
                hasLoginDom,
                hasLoginHtml,
                hasLoginText,
                hasLoggedInProfile
            });
        })();
        """;
}