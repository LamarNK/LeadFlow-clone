namespace LeadFlow.Services.Avito;

/// <summary>
/// Avito показал капчу/firewall на странице во время автоматизации. Бросается из CDP-загрузчиков
/// (<see cref="LeadFlow.Services.AdsPower.IAdsPowerAvitoAutomationService"/>) и из источников откликов;
/// мониторинг ловит и переводит аккаунт в <c>RequiresManualAction</c>, чтобы не долбить сайт.
/// </summary>
public sealed class AvitoCaptchaDetectedException : Exception
{
    public AvitoCaptchaDetectedException(string kind, string? url, string? html)
        : base(BuildMessage(kind, url))
    {
        Kind = kind;
        Url = url;
        // Намеренно НЕ храним полный HTML в исключении — он может быть мегабайтным и попасть в логи.
        // Сохраняем только короткий префикс на случай диагностики.
        HtmlPreview = string.IsNullOrEmpty(html)
            ? null
            : html.Substring(0, Math.Min(html.Length, 512));
    }

    /// <summary>«hCaptcha», «geetest», «image-captcha», «firewall» или «captcha».</summary>
    public string Kind { get; }

    /// <summary>URL страницы, на которой нас встретила капча (если известен).</summary>
    public string? Url { get; }

    public string? HtmlPreview { get; }

    private static string BuildMessage(string kind, string? url) =>
        string.IsNullOrEmpty(url)
            ? $"Avito показал капчу/блок IP ({kind})."
            : $"Avito показал капчу/блок IP ({kind}) на странице {url}.";
}
