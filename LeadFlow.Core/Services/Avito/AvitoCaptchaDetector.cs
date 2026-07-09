using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Детектит «капча/firewall»-страницы Avito, которые встречают нас вместо нормального HTML.
/// Источник правил — реальные снимки HTML:
///   • <c>&lt;div class="firewall-container"&gt;</c> с заголовком «Доступ ограничен: проблема с IP»
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

        if (Regex.IsMatch(
                html,
                @"\bfirewall-container\b|\bjs-firewall-form\b|\bfirewall-title\b|Доступ\s+ограничен|проблема\s+с\s+IP",
                RegexOptions.IgnoreCase))
        {
            return "firewall";
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

        if (TextMarkers.IsMatch(html) && !HasNormalAvitoMarkers(html))
        {
            return "captcha";
        }

        return null;
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
