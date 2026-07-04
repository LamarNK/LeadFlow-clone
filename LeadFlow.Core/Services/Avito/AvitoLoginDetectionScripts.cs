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

            const containsAuthText = (value) =>
                /телефон или почта|забыли пароль|запомнить пароль|продолжить через|зарегистрироваться|нет аккаунта на/i.test(value ?? "");

            const hasLoginText = containsAuthText(probeText);

            const hasLogin = hasLoginDom || hasLoginHtml || hasLoginText || titleSuggestsLogin || urlSuggestsLogin;

            return JSON.stringify({
                hasLogin,
                url,
                title,
                urlSuggestsLogin,
                titleSuggestsLogin,
                hasLoginDom,
                hasLoginHtml,
                hasLoginText
            });
        })();
        """;
}