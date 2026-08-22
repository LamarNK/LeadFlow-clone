namespace LeadFlow.Core.Services.Captcha;

/// <summary>
/// Контекст профиля, с которым должна быть решена GeeTest v4.
/// Пароль прокси используется только в запросе к RuCaptcha и никогда не попадает в логи.
/// </summary>
public sealed record GeeTestV4TaskOptions(string? UserAgent = null, GeeTestV4Proxy? Proxy = null)
{
    public bool UsesSuppliedProxy => Proxy is not null;

    public GeeTestV4TaskOptions WithUserAgent(string? userAgent) =>
        string.IsNullOrWhiteSpace(userAgent) ? this : this with { UserAgent = userAgent.Trim() };

    public GeeTestV4TaskOptions WithProxy(GeeTestV4Proxy? proxy) =>
        this with { Proxy = proxy };

    /// <summary>
    /// Подставляет прокси браузера (AdsPower-профиль), если он разобран.
    /// Невалидный/пустой профиль не сбрасывает уже заданный fallback аккаунта.
    /// </summary>
    public GeeTestV4TaskOptions PreferProxy(
        string? proxyType,
        string? proxyAddress,
        string? proxyLogin,
        string? proxyPassword)
    {
        var proxy = GeeTestV4Proxy.TryCreate(proxyType, proxyAddress, proxyLogin, proxyPassword);
        return proxy is null ? this : WithProxy(proxy);
    }

    public static GeeTestV4TaskOptions FromBrowserProfile(
        string? userAgent,
        string? proxyType,
        string? proxyAddress,
        string? proxyLogin,
        string? proxyPassword) =>
        new(userAgent?.Trim(), GeeTestV4Proxy.TryCreate(proxyType, proxyAddress, proxyLogin, proxyPassword));
}

public sealed record GeeTestV4Proxy(
    string Type,
    string Address,
    int Port,
    string? Login,
    string? Password)
{
    public static GeeTestV4Proxy? TryCreate(
        string? type,
        string? address,
        string? login = null,
        string? password = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var normalizedType = type?.Trim().ToLowerInvariant() switch
        {
            "socks4" => "socks4",
            "socks5" => "socks5",
            "http" or "https" or null or "" => "http",
            _ => null
        };
        if (normalizedType is null)
        {
            return null;
        }

        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "http://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is <= 0 or > 65535)
        {
            return null;
        }

        return new GeeTestV4Proxy(
            normalizedType,
            uri.Host,
            uri.Port,
            string.IsNullOrWhiteSpace(login) ? null : login.Trim(),
            string.IsNullOrWhiteSpace(password) ? null : password);
    }
}
