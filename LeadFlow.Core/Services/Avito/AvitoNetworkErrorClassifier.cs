namespace LeadFlow.Core.Services.Avito;

/// <summary>
/// Категория сетевого сбоя по данным Chrome/CDP (токены <c>net::ERR_*</c> и страницы ошибок браузера).
/// </summary>
public enum AvitoNetworkErrorKind
{
    /// <summary>Сетевых признаков нет.</summary>
    None,

    /// <summary>Прокси аккаунта не отвечает/отклоняет соединение — идти на Avito нет смысла.</summary>
    ProxyFailure,

    /// <summary>Нет доступа в интернет (воркер или канал прокси мёртв).</summary>
    NoInternet,

    /// <summary>DNS не разрешает адрес (часто следствие мёртвого прокси или сети).</summary>
    DnsFailure,

    /// <summary>Кратковременный сбой соединения — достоин одного дешёвого повтора.</summary>
    TransientNet
}

/// <summary>
/// Классификация сетевых сбоев браузера: Chrome кладёт в NavigationException токены
/// <c>net::ERR_*</c>, а страницы ошибок («Нет подключения к интернету», «Прокси-сервер
/// не отвечает») — в HTML. Классификатор отличает мёртвый прокси от отсутствия сети
/// и от кратковременного сбоя соединения, чтобы воркер выбирал правильную реакцию
/// (остывание, пауза или дешёвый повтор) вместо generic-ошибки автоматизации.
/// </summary>
public static class AvitoNetworkErrorClassifier
{
    private const string TokenPrefix = "net::";

    private static readonly string[] ProxyTokens =
    [
        "ERR_PROXY_CONNECTION_FAILED",
        "ERR_TUNNEL_CONNECTION_FAILED",
        "ERR_PROXY_AUTH_UNSUPPORTED",
        "ERR_SOCKS_CONNECTION_FAILED",
        "ERR_MANDATORY_PROXY_CONFIGURATION_FAILED",
        "ERR_PROXY_CERTIFICATE_ERROR"
    ];

    private static readonly string[] NoInternetTokens =
    [
        "ERR_INTERNET_DISCONNECTED",
        "ERR_ADDRESS_UNREACHABLE",
        "ERR_NETWORK_IO_SUSPENDED"
    ];

    private static readonly string[] DnsTokens =
    [
        "ERR_NAME_NOT_RESOLVED",
        "ERR_DNS_PROBE_FINISHED_NXDOMAIN",
        "ERR_DNS_PROBE_FINISHED_NO_INTERNET"
    ];

    private static readonly string[] TransientTokens =
    [
        "ERR_TIMED_OUT",
        "ERR_CONNECTION_TIMED_OUT",
        "ERR_CONNECTION_RESET",
        "ERR_CONNECTION_CLOSED",
        "ERR_CONNECTION_REFUSED",
        "ERR_EMPTY_RESPONSE",
        "ERR_ABORTED",
        "ERR_NETWORK_CHANGED",
        "ERR_SOCKET_NOT_CONNECTED",
        "ERR_INSUFFICIENT_RESOURCES"
    ];

    /// <summary>Классифицирует исключение (включая внутреннюю цепочку) по токенам net::ERR_*.</summary>
    public static AvitoNetworkErrorKind Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var kind = ClassifyMessage(current.Message);
            if (kind != AvitoNetworkErrorKind.None)
            {
                return kind;
            }
        }

        return AvitoNetworkErrorKind.None;
    }

    /// <summary>Классифицирует текст ошибки по токену net::ERR_* (без префикса — тоже).</summary>
    public static AvitoNetworkErrorKind ClassifyMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AvitoNetworkErrorKind.None;
        }

        var kind = Match(message, ProxyTokens, AvitoNetworkErrorKind.ProxyFailure)
            ?? Match(message, NoInternetTokens, AvitoNetworkErrorKind.NoInternet)
            ?? Match(message, DnsTokens, AvitoNetworkErrorKind.DnsFailure)
            ?? Match(message, TransientTokens, AvitoNetworkErrorKind.TransientNet);
        return kind ?? AvitoNetworkErrorKind.None;
    }

    /// <summary>
    /// Классифицирует HTML страницы: Chrome показывает собственные страницы ошибок
    /// («Нет подключения к интернету», «Прокси-сервер не отвечает») с токенами ERR_*.
    /// </summary>
    public static AvitoNetworkErrorKind ClassifyHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return AvitoNetworkErrorKind.None;
        }

        var kind = ClassifyMessage(html);
        if (kind != AvitoNetworkErrorKind.None)
        {
            return kind;
        }

        if (html.Contains("Прокси-сервер не отвечает", StringComparison.OrdinalIgnoreCase)
            || html.Contains("не удаётся подключиться к прокси", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cannot connect to the proxy", StringComparison.OrdinalIgnoreCase))
        {
            return AvitoNetworkErrorKind.ProxyFailure;
        }

        if (html.Contains("Нет подключения к интернету", StringComparison.OrdinalIgnoreCase)
            || html.Contains("нет соединения с интернетом", StringComparison.OrdinalIgnoreCase)
            || html.Contains("No internet", StringComparison.OrdinalIgnoreCase)
            || html.Contains("ERR_INTERNET_DISCONNECTED", StringComparison.Ordinal))
        {
            return AvitoNetworkErrorKind.NoInternet;
        }

        return AvitoNetworkErrorKind.None;
    }

    /// <summary>Достаёт первый найденный сетевой токен (для диагностики), либо null.</summary>
    public static string? FindNetworkToken(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var token = FindToken(current.Message);
            if (token is not null)
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>Кратковременный сбой соединения — заслуживает одного дешёвого повтора на месте.</summary>
    public static bool IsTransientRetryable(Exception exception) =>
        Classify(exception) == AvitoNetworkErrorKind.TransientNet;

    /// <summary>Терминальный сетевой сбой — повтор на месте бессмысленен, нужна пауза/действие.</summary>
    public static bool IsTerminalNetworkError(Exception exception)
    {
        var kind = Classify(exception);
        return kind is AvitoNetworkErrorKind.ProxyFailure
            or AvitoNetworkErrorKind.NoInternet
            or AvitoNetworkErrorKind.DnsFailure;
    }

    /// <summary>
    /// Строит типизированное исключение по терминальной сетевой категории:
    /// мёртвый прокси → <see cref="AdsPower.AdsPowerProxyFailureException"/> (готовая ветка обработки),
    /// нет сети/DNS → <see cref="AvitoNetworkUnavailableException"/>.
    /// </summary>
    public static Exception BuildTerminalException(Exception source, string? pageUrl)
    {
        var kind = Classify(source);
        var token = FindNetworkToken(source) ?? $"ERR_{kind.ToString().ToUpperInvariant()}";
        return kind switch
        {
            AvitoNetworkErrorKind.ProxyFailure => new AdsPower.AdsPowerProxyFailureException(
                pageUrl,
                $"{TokenPrefix}{token} — браузер не смог соединиться через прокси."),
            _ => new AvitoNetworkUnavailableException(kind, token, pageUrl, source)
        };
    }

    private static AvitoNetworkErrorKind? Match(string message, string[] tokens, AvitoNetworkErrorKind kind)
    {
        foreach (var token in tokens)
        {
            if (message.Contains(token, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    private static string? FindToken(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        // Токен целиком: ERR_INTERNET_DISCONNECTED (префикс net:: срезаем).
        var start = message.IndexOf(TokenPrefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += TokenPrefix.Length;
        var length = 0;
        for (var i = start; i < message.Length; i++)
        {
            var c = message[i];
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                length++;
            }
            else
            {
                break;
            }
        }

        return length > 0 ? message.Substring(start, length) : null;
    }
}
