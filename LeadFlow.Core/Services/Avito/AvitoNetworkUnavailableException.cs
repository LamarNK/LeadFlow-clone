namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Страница Avito не загрузилась: у воркера нет интернета или DNS не разрешает адрес
/// (токены net::ERR_INTERNET_DISCONNECTED, net::ERR_NAME_NOT_RESOLVED и т.п.).
/// Наследует <see cref="InvalidOperationException"/>, чтобы попадать в существующую
/// ветку «автовозможная ошибка субпрофиля» и не блокировать аккаунт.
/// </summary>
public sealed class AvitoNetworkUnavailableException : InvalidOperationException
{
    public AvitoNetworkErrorKind Kind { get; }

    /// <summary>Сетевой токен Chrome (например, <c>ERR_INTERNET_DISCONNECTED</c>).</summary>
    public string Token { get; }

    public string? PageUrl { get; }

    public string UserMessage { get; }

    public AvitoNetworkUnavailableException(
        AvitoNetworkErrorKind kind,
        string token,
        string? pageUrl = null,
        Exception? inner = null)
        : base(FormatMessage(kind, token, pageUrl), inner)
    {
        Kind = kind;
        Token = token;
        PageUrl = pageUrl;
        UserMessage = FormatMessage(kind, token, pageUrl);
    }

    private static string FormatMessage(AvitoNetworkErrorKind kind, string token, string? pageUrl)
    {
        var cause = kind switch
        {
            AvitoNetworkErrorKind.DnsFailure =>
                "не удаётся разрешить адрес Avito (DNS) — проверьте интернет или прокси",
            _ => "нет доступа в интернет — страница Avito не загрузилась, проверьте сеть"
        };

        var text = $"{cause} (net::{token}).";
        return string.IsNullOrWhiteSpace(pageUrl)
            ? text
            : $"{text} Страница: {pageUrl}";
    }
}
