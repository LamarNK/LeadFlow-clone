namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Avito показал капчу/firewall на странице во время автоматизации. Бросается из CDP-загрузчиков
/// (<see cref="LeadFlow.Services.AdsPower.IAdsPowerAvitoAutomationService"/>) и из источников откликов;
/// мониторинг ловит: блок IP — <c>RequiresManualAction</c>, обычная капча — только текущий субпрофиль.
/// </summary>
public sealed class AvitoCaptchaDetectedException : Exception
{
    public AvitoCaptchaDetectedException(
        string kind,
        string? url,
        string? html,
        byte[]? screenshotPng = null,
        string? subProfileId = null,
        string? subProfileName = null,
        IReadOnlyList<string>? signals = null,
        string? pageTitle = null)
        : base(BuildMessage(kind, url))
    {
        Kind = kind;
        Url = url;
        SubProfileId = subProfileId;
        SubProfileName = subProfileName;
        Signals = signals;
        PageTitle = pageTitle;
        ScreenshotPng = screenshotPng is { Length: > 0 } ? screenshotPng : null;
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

    public byte[]? ScreenshotPng { get; }

    public string? SubProfileId { get; }

    public string? SubProfileName { get; }

    public IReadOnlyList<string>? Signals { get; }

    public string? PageTitle { get; }

    private static string BuildMessage(string kind, string? url)
    {
        var subject = string.Equals(kind, "firewall", StringComparison.OrdinalIgnoreCase)
            ? "Avito ограничил доступ из-за IP"
            : "Avito показал капчу";
        return string.IsNullOrEmpty(url)
            ? $"{subject} ({kind})."
            : $"{subject} ({kind}) на странице {url}.";
    }
}
