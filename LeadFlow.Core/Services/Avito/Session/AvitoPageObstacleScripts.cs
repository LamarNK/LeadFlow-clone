namespace LeadFlow.Core.Services.Avito.Session;

/// <summary>
/// Единый JS-детект препятствий Avito (капча/блок IP/вход/ошибка страницы/ручное подтверждение)
/// для оркестратора сессии. Заменяет разрозненные проверки в скриптах кандидатов и состояния страницы.
/// </summary>
/// <remarks>
/// Ключевое отличие от старых probe-скриптов: текст видимой капча-модалки читается напрямую из
/// узла диалога, а не из среза <c>body.innerText</c> — Avito рендерит порталы (модалку капчи)
/// в конце DOM, и на странице откликов первые 12 000 символов текста принадлежат списку карточек,
/// поэтому текст модалки в срез не попадал и капча поверх рабочего списка не детектилась.
/// GeeTest v4 (gt4.js) рисует виджет в shadow-root/iframe и не создаёт <c>#geetest_captcha</c>,
/// поэтому v3-id здесь недостаточно: дополнительно ищем geetest-разметку и смонтированный v4-скрипт
/// в паре с видимым диалогом. Сам по себе gt4.js не считается активной капчей: скрипт остаётся
/// в DOM после закрытия проверки.
/// </remarks>
public static class AvitoPageObstacleScripts
{
    public static string BuildProbeScript() =>
        """
        (() => {
            const url = window.location.href ?? "";
            const title = (document.title ?? "").trim();
            const bodyText = document.body?.innerText ?? "";
            const probeText = title + "\n" + bodyText;
            const signals = [];

            const isVisibleEl = (el) => {
                try {
                    if (!el) return false;
                    const style = window.getComputedStyle(el);
                    if (style.display === "none" || style.visibility === "hidden") return false;
                    const rect = el.getBoundingClientRect();
                    return rect.width > 2 && rect.height > 2;
                } catch {
                    return false;
                }
            };

            // Виджеты классической разметки (v3 id, hCaptcha, картинка).
            const hasOldWidget = !!(
                document.getElementById("geetest_captcha")
                || document.getElementById("inner-captcha")
                || document.getElementById("h-captcha")
                || document.querySelector(".h-captcha[data-sitekey]")
            );
            if (hasOldWidget) signals.push("legacy-widget");

            // GeeTest DOM (v3 и v4 в light DOM): панели, overlay, iframe.
            const hasGeeTestDom = !!document.querySelector(
                ".geetest_boxShow, .geetest_popup_wrap, [class*='geetest_box'], [class*='geetest_panel'], [class*='geetest_holder'], iframe[src*='geetest']");
            if (hasGeeTestDom) signals.push("geetest-dom");

            // GeeTest v4 смонтирован (Avito подгружает gt4 только при показе капчи).
            const hasGeeTestV4Mounted = !!(
                document.querySelector("script[src*='/s/captcha/gt4']")
                || document.querySelector("script[src*='gcaptcha4.geetest.com']")
                || document.querySelector("script[src*='static.geetest.com/v4']")
                || document.getElementById("js-geetest-captcha-script")
            );
            if (hasGeeTestV4Mounted) signals.push("geetest-v4-mounted");

            // Капча в модалке поверх РАБОЧЕГО списка: читаем текст видимого диалога напрямую,
            // не из среза body.innerText (модалка живёт в конце DOM).
            const dialogs = Array.from(document.querySelectorAll(
                "[role='dialog'][aria-modal='true'], [aria-modal='true'], [data-scroll-lock-ignore='true']"));
            let hasCaptchaDialog = false;
            for (const dialog of dialogs) {
                if (!isVisibleEl(dialog)) continue;
                const text = ((dialog.innerText || dialog.textContent || "") + "").replace(/\s+/g, " ");
                if (/капч/i.test(text)) { hasCaptchaDialog = true; break; }
                if (/не\s+робот/i.test(text)) { hasCaptchaDialog = true; break; }
                if (hasGeeTestV4Mounted && /Продолжить/i.test(text)) { hasCaptchaDialog = true; break; }
            }
            if (hasCaptchaDialog) signals.push("captcha-dialog");

            const itemCount = document.querySelectorAll("[data-marker='job-application/item']").length;
            const statusCount = document.querySelectorAll("[data-marker='job-application/response/status-select-button']").length;

            const hasFirewallDom = !!document.querySelector(
                ".firewall-container, .js-firewall-form, .firewall-title, form.js-firewall-form"
            ) || location.hash === "#block"
              || !!document.querySelector('a[href*="support.avito.ru/request/720"]');
            if (hasFirewallDom) signals.push("firewall-dom");

            // Текстовые сигналы применяем только без списка карточек: на рабочей странице
            // слова «капча/Продолжить» могут встретиться в тексте чата.
            const hasFirewallText = /Доступ\s+ограничен|проблема\s+с\s+IP|firewallCaptcha|Отключить\s+VPN|самол[её]те/i.test(probeText);
            const listMissing = itemCount === 0 && statusCount === 0;
            if (hasFirewallText && listMissing) signals.push("firewall-text");

            const hasCaptchaChallenge = hasOldWidget
                || hasGeeTestDom
                || hasCaptchaDialog
                || (listMissing && (hasFirewallDom || hasFirewallText))
                || (listMissing && /капч|captcha/i.test(probeText) && /Продолжить/i.test(probeText));

            // Блок IP — только когда это НЕ решаемая капча (зеркалим классификацию C#-детектора).
            const hasIpText = /Доступ\s+ограничен/i.test(probeText) && /проблема\s+с\s+IP/i.test(probeText);
            const hasStaticIpBlock = location.hash === "#block"
              && !!document.querySelector('a[href*="support.avito.ru/request/720"]')
              && /Отключить\s+VPN|самол[её]те/i.test(probeText);
            const hasIpBlock = !hasCaptchaChallenge && (hasIpText || hasStaticIpBlock);
            if (hasIpBlock) signals.push("ip-block");

            let captchaKind = null;
            if (hasCaptchaChallenge) {
                captchaKind = "captcha";
                if (document.getElementById("geetest_captcha")
                    || hasGeeTestDom
                    || (hasGeeTestV4Mounted && hasCaptchaDialog)) {
                    captchaKind = "geetest";
                } else if (document.getElementById("h-captcha")
                    || document.querySelector(".h-captcha[data-sitekey]")) {
                    captchaKind = "hCaptcha";
                } else if (document.getElementById("inner-captcha")) {
                    captchaKind = "image-captcha";
                }
            }

            // Ручное подтверждение: Avito сбросил пароль, нужен SMS-код владельца.
            const passwordResetForm = document.querySelector(
                "[data-marker='password-was-reset'], [data-marker='password-was-reset-form']");
            const manualAction = !!passwordResetForm
              && /Сработала\s+защита\s+профиля/i.test((passwordResetForm.textContent || ""));
            if (manualAction) signals.push("password-reset");

            const hasLoggedInProfile = !!(
                document.querySelector("[data-marker='header/profile-name']")
                || document.querySelector("[data-marker='profile-switch/link']")
                || document.querySelector("[data-marker='osp-sidebar/tools/profile/name']")
                || document.querySelector("[data-marker='osp-sidebar/tools/money']")
            );
            const hasLoginDom = !!(
                document.querySelector("[data-marker='auth-app-root']")
                || document.querySelector("form[data-marker='login-form']")
                || document.querySelector("[data-marker='login-form/login']")
                || document.querySelector("[data-marker='login-form/password']")
                || document.querySelector("input[name='password'][autocomplete='current-password']")
                || document.querySelector("[data-marker='users-list']")
                || document.querySelector("[data-marker^='users-list(']")
                || document.querySelector("[data-marker='user/link']")
                || document.querySelector("[data-marker='login-form-with-avatar']")
            );
            const hasGuestLoginButton = !!document.querySelector("[data-marker='header/login-button']");
            const loginRequired = !hasLoggedInProfile && (hasLoginDom || hasGuestLoginButton);
            if (loginRequired) signals.push("login-form");

            const transientError = /Попробуйте\s+обновить\s+страницу\s+или\s+загляните\s+позже/i.test(probeText)
                || /обязательно\s+всё\s+починим/i.test(probeText);
            if (transientError) signals.push("transient-error");

            let kind = "none";
            if (manualAction) kind = "manualAction";
            else if (hasCaptchaChallenge) kind = "captcha";
            else if (hasIpBlock) kind = "ipBlocked";
            else if (loginRequired) kind = "loginRequired";
            else if (transientError) kind = "transientError";

            return JSON.stringify({
                kind,
                captchaKind,
                url,
                title,
                itemCount,
                statusCount,
                signals
            });
        })();
        """;
}
