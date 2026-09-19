using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Детектит «капча/firewall»-страницы Avito, которые встречают нас вместо нормального HTML.
/// Источник правил — реальные снимки HTML:
///   • капча: firewall с кнопкой «Продолжить» / «для решения капчи» / GeeTest/hCaptcha
///     (заголовок может быть «Доступ ограничен: проблема с IP» — это всё равно капча);
///   • блок IP: статическая страница «Доступ ограничен: проблема с IP» без виджета и без «Продолжить»
///     (h1 + советы про VPN/«в самолёте», ссылка <c>support.avito.ru/request/720</c>,
///     авто-reload с <c>#block</c>);
///   • hCaptcha (<c>&lt;div class="h-captcha"&gt;</c>, <c>data-sitekey</c>),
///   • geetest (<c>id="geetest_captcha"</c>),
///   • старая капча с картинкой (<c>id="inner-captcha"</c>),
///   • просто упоминание слова «капч/captcha/проверочный код/подтвердите».
/// Используется во всех CDP-загрузках страниц Avito, чтобы не лезть дальше парсить мусор и сразу
/// поднять на уровень мониторинга «требуется ручное действие».
/// </summary>
public static class AvitoCaptchaDetector
{
    private static readonly Regex StrongMarkers = new(
        // Сильные маркеры — однозначно капча/блок IP. Любого из них достаточно.
        @"\bfirewall-container\b" +
        @"|\bjs-firewall-form\b" +
        @"|\bfirewall-title\b" +
        @"|location\.hash\s*!=\s*[""']#block[""']" +
        @"|support\.avito\.ru/request/720" +
        @"|Отключить\s+VPN" +
        @"|В\s+самол[её]те" +
        @"|id=""geetest_captcha""" +
        @"|initGeetest" +
        @"|geetest\.com" +
        @"|gt_captcha" +
        @"|id=""inner-captcha""" +
        @"|id=""h-captcha""" +
        @"|class=""h-captcha""" +
        @"|data-hcaptcha-widget-id" +
        @"|hcaptcha\.com",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextMarkers = new(
        // Текстовые маркеры — fallback, когда HTML отдан без классов (минимальный шаблон):
        // «Доступ ограничен», «проблема с IP», «капч/captcha/проверочный код/подтвердите».
        @"Доступ\s+ограничен" +
        @"|проблема\s+с\s+IP" +
        @"|проверочный\s+код" +
        @"|введите\s+символы\s+с\s+картинки" +
        @"|поставьте\s+галочку\s+и\s+нажмите" +
        @"|капч[аеиыу]" +
        @"|\bcaptcha\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Возвращает true, если в HTML есть признак капчи/firewall.
    /// </summary>
    public static bool IsCaptchaHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (StrongMarkers.IsMatch(html))
        {
            return true;
        }

        // Текстовые маркеры применяем только если на странице нет «нормальных» Avito-маркеров —
        // иначе в шапке или подвале сайта могут быть упоминания капчи в подсказках помощи и т.п.
        if (HasNormalAvitoMarkers(html))
        {
            return false;
        }

        return TextMarkers.IsMatch(html);
    }

    /// <summary>
    /// Краткое имя обнаруженной капчи: «hCaptcha», «geetest», «image-captcha», «firewall»
    /// или null, если HTML без капчи.
    /// </summary>
    public static string? Classify(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (HasIpBlockChallenge(html))
        {
            return "firewall";
        }

        if (HasSolvableCaptchaChallenge(html) && HasGeeTestWidget(html))
        {
            return "geetest";
        }

        if (Regex.IsMatch(html, @"\bh-captcha\b|hcaptcha\.com|data-hcaptcha-widget-id", RegexOptions.IgnoreCase))
        {
            return "hCaptcha";
        }

        if (Regex.IsMatch(
                html,
                @"id=""geetest_captcha""|initGeetest|geetest\.com|gt_captcha",
                RegexOptions.IgnoreCase))
        {
            return "geetest";
        }

        if (Regex.IsMatch(html, @"id=""inner-captcha""", RegexOptions.IgnoreCase))
        {
            return "image-captcha";
        }

        if (StrongMarkers.IsMatch(html))
        {
            return "captcha";
        }

        if (TextMarkers.IsMatch(html) && !HasNormalAvitoMarkers(html))
        {
            return "captcha";
        }

        return null;
    }

    /// <summary>
    /// GeeTest-виджет на странице (в том числе внутри firewall-контейнера).
    /// </summary>
    public static bool HasGeeTestWidget(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return Regex.IsMatch(
            html,
            @"id=[""']?geetest_captcha|class=[""']geetest_widget|geetest_box|geetest_nine|data-geetest|initGeetest4?|geetest\.com|gt_captcha|gt4\.js|/s/captcha/gt4",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Живая проверка Avito: GeeTest/hCaptcha/картинка, текст «для решения капчи»
    /// или кнопка «Продолжить» на firewall-экране. Заголовок «проблема с IP» тут не важен —
    /// это капча, её можно решать.
    /// </summary>
    public static bool HasSolvableCaptchaChallenge(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        if (HasGeeTestWidget(html))
        {
            return true;
        }

        if (Regex.IsMatch(
                html,
                @"\bh-captcha\b|hcaptcha\.com|data-hcaptcha-widget-id|id=[""']inner-captcha",
                RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (Regex.IsMatch(html, @"решени[еюя]\s+капч", RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (!html.Contains("Продолжить", StringComparison.Ordinal))
        {
            return false;
        }

        return Regex.IsMatch(
                   html,
                   @"firewall-container|js-firewall-form|firewall-title|role=[""']dialog[""']",
                   RegexOptions.IgnoreCase)
               || Regex.IsMatch(html, @"капч", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Статическая страница «Доступ ограничен: проблема с IP» без виджета и без «Продолжить»
    /// (иллюстрация, VPN/«в самолёте», авто-reload <c>#block</c>). Это блок IP, а не капча.
    /// Экран с кнопкой «Продолжить» / «для решения капчи» — капча, даже при том же заголовке.
    /// Классификация нужна для issue-статуса аккаунта; попытка автопрохода капчи теперь
    /// выполняется и для этой страницы (капчу запрашивает activate-probe у сервера).
    /// </summary>
    public static bool HasIpBlockChallenge(string? html)
    {
        if (string.IsNullOrWhiteSpace(html) || HasSolvableCaptchaChallenge(html))
        {
            return false;
        }

        var hasTitle = Regex.IsMatch(html, @"Доступ\s+ограничен", RegexOptions.IgnoreCase);
        var hasIp = Regex.IsMatch(html, @"проблема\s+с\s+IP", RegexOptions.IgnoreCase);
        var hasStaticIpMarkers = Regex.IsMatch(
                html,
                @"location\.hash\s*!=\s*[""']#block[""']",
                RegexOptions.IgnoreCase)
            && html.Contains("support.avito.ru/request/720", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(html, @"Отключить\s+VPN|В\s+самол[её]те", RegexOptions.IgnoreCase);

        // Снимок модалки «Выбор профиля» часто тащит dormant firewall-текст.
        // Статическую страницу #block без модалки это не маскирует.
        if (LooksLikeProfileSwitchSnapshot(html) && !hasStaticIpMarkers)
        {
            return false;
        }

        return (hasTitle && hasIp) || hasStaticIpMarkers;
    }

    private static bool LooksLikeProfileSwitchSnapshot(string html) =>
        html.Contains("component-profile-switch", StringComparison.OrdinalIgnoreCase)
        && (html.Contains("Выбор профиля", StringComparison.OrdinalIgnoreCase)
            || html.Contains("component-profile-switch/profile-", StringComparison.OrdinalIgnoreCase));

    public static bool CanAttemptGeeTestSolve(string? html) =>
        !HasIpBlockChallenge(html) && HasGeeTestWidget(html);

    /// <summary>
    /// captcha_id GeeTest v4 со страницы Avito. Если в HTML нет — фиксированное значение домена.
    /// </summary>
    public static string ExtractGeeTestCaptchaId(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return AvitoGeeTestCaptchaId;
        }

        var dataAttr = Regex.Match(
            html,
            @"data-geetest\s*=\s*[""']([0-9a-f]{32})[""']",
            RegexOptions.IgnoreCase);
        if (dataAttr.Success)
        {
            return dataAttr.Groups[1].Value;
        }

        var initParam = Regex.Match(
            html,
            @"captcha_id[""']?\s*[:=]\s*[""']([0-9a-f]{32})[""']",
            RegexOptions.IgnoreCase);
        if (initParam.Success)
        {
            return initParam.Groups[1].Value;
        }

        var policy = Regex.Match(
            html,
            @"captcha_v4/policy/([0-9a-f]{32})",
            RegexOptions.IgnoreCase);
        return policy.Success ? policy.Groups[1].Value : AvitoGeeTestCaptchaId;
    }

    /// <summary>Фиксированный captcha_id GeeTest v4 для avito.ru (RuCaptcha / 2captcha).</summary>
    public const string AvitoGeeTestCaptchaId = "2d9c743cf7d63dbc9db578a608196bcd";

    /// <summary>На странице уже форма входа Avito — firewall-капча позади, дальше автовход.</summary>
    public static bool ShowsLoginForm(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("data-marker=\"login-form", StringComparison.OrdinalIgnoreCase)
               || html.Contains("data-marker='login-form", StringComparison.OrdinalIgnoreCase)
               || html.Contains("data-marker=\"users-list", StringComparison.OrdinalIgnoreCase)
               || html.Contains("data-marker='users-list", StringComparison.OrdinalIgnoreCase)
               || html.Contains("login-form-with-avatar", StringComparison.OrdinalIgnoreCase)
               || html.Contains("data-marker=\"user/link", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Есть ли в HTML признаки «обычной» страницы Avito — это страховка против ложных срабатываний
    /// текстовых маркеров (вроде слова «капча» в подсказке/футере).
    /// </summary>
    private static bool HasNormalAvitoMarkers(string html) =>
        html.Contains("data-marker=\"item-snippet/", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("data-marker=\"profile-items-tab", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("data-marker=\"job-application/item", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("personal-items-root-element", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("data-marker=\"component-profile-switch", StringComparison.OrdinalIgnoreCase);
}
