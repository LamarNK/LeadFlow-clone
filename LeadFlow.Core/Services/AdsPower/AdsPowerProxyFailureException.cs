namespace LeadFlow.Core.Services.AdsPower;

/// <summary>
/// Стартовая страница AdsPower (<c>start.adspower.net</c>) показала, что прокси профиля не работает.
/// Дальше идти на Avito нет смысла: запросы пойдут в никуда.
/// </summary>
public sealed class AdsPowerProxyFailureException : InvalidOperationException
{
    public const string ErrorKey = "ads_power.proxy_failure";

    public const string UserDetail =
        "проверьте прокси в AdsPower — без рабочего прокси продолжать нет смысла.";

    public AdsPowerProxyFailureException(string? pageUrl, string? detail = null)
        : base(FormatMessage(pageUrl, detail))
    {
        PageUrl = pageUrl;
        UserMessage = string.IsNullOrWhiteSpace(detail) ? UserDetail : detail.Trim();
    }

    public string? PageUrl { get; }

    public string UserMessage { get; }

    public static bool LooksLikeMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && message.Contains("прокси", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("AdsPower", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Proxy failure", StringComparison.OrdinalIgnoreCase));

    private static string FormatMessage(string? pageUrl, string? detail)
    {
        var text = string.IsNullOrWhiteSpace(detail)
            ? $"AdsPower: прокси не работает. {UserDetail}"
            : $"AdsPower: прокси не работает. {detail.Trim()}";

        return string.IsNullOrWhiteSpace(pageUrl)
            ? text
            : $"{text} Страница: {pageUrl}";
    }
}
