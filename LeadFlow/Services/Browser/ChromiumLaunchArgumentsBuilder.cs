using System.Globalization;
using System.Text;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

/// <summary>
/// Собирает дополнительные аргументы процесса Chromium (WebView2) из прокси и пользовательских флагов.
/// </summary>
public static class ChromiumLaunchArgumentsBuilder
{
    /// <summary>Возвращает полную строку аргументов (без внешних кавычек).</summary>
    public static string Build(AvitoAccount account)
    {
        var sb = new StringBuilder();
        var proxyArg = BuildProxyServerSwitchValue(account);
        if (!string.IsNullOrEmpty(proxyArg))
        {
            sb.Append("--proxy-server=").Append(proxyArg);
        }

        var extra = account.BrowserLaunchArgs?.Trim();
        if (!string.IsNullOrEmpty(extra))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(extra);
        }

        var webrtc = account.WebRtcLaunchFlags?.Trim();
        if (!string.IsNullOrEmpty(webrtc))
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(webrtc);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Значение для <c>--proxy-server=...</c> (без префикса флага).
    /// Учётные данные намеренно не включаются: Chromium/WebView2 часто возвращает
    /// <c>ERR_NO_SUPPORTED_PROXIES</c> для <c>http://user:pass@host:port</c>.
    /// Для HTTP-прокси с логином ответ на 407 Proxy-Authenticate обрабатывается в
    /// <see cref="BrowserAccountSession"/> через <c>BasicAuthenticationRequested</c>.
    /// </summary>
    public static string? BuildProxyServerSwitchValue(AvitoAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.ProxyAddress))
        {
            return null;
        }

        var addr = account.ProxyAddress.Trim();
        var hasAuth = !string.IsNullOrWhiteSpace(account.ProxyUsername);

        if (account.ProxyType == "socks5")
        {
            if (hasAuth)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Proxy] SOCKS5 с логином: Chromium не поддерживает авторизацию в --proxy-server; используйте HTTP-прокси или локальный проброс.");
            }

            if (!TryParseProxyHostPort(addr, out var host, out var port))
            {
                return addr.Contains("://", StringComparison.Ordinal) ? addr : $"socks5://{addr}";
            }

            return port is { } p ? $"socks5://{host}:{p}" : $"socks5://{host}";
        }

        // HTTP CONNECT-прокси
        if (!TryParseProxyHostPort(addr, out var httpHost, out var httpPort))
        {
            return addr.Contains("://", StringComparison.OrdinalIgnoreCase) ? addr : $"http://{addr}";
        }

        return httpPort is { } hp ? $"http://{httpHost}:{hp}" : $"http://{httpHost}";
    }

    /// <summary>
    /// Достаёт host и порт из строки прокси (без встраивания userinfo в URL Chromium).
    /// </summary>
    private static bool TryParseProxyHostPort(string addr, out string host, out int? port)
    {
        host = "";
        port = null;
        if (string.IsNullOrWhiteSpace(addr))
        {
            return false;
        }

        var a = addr.Trim();

        if (Uri.TryCreate(a, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            if (uri.Scheme != Uri.UriSchemeHttp
                && uri.Scheme != Uri.UriSchemeHttps
                && !string.Equals(uri.Scheme, "socks5", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var h = uri.Host;
            if (h.Contains(':') && !h.StartsWith('['))
            {
                h = $"[{h}]";
            }

            host = h;
            port = !uri.IsDefaultPort && uri.Port > 0 ? uri.Port : null;
            return true;
        }

        if (a.StartsWith('['))
        {
            var endBracket = a.IndexOf(']', StringComparison.Ordinal);
            if (endBracket > 0 && endBracket + 1 < a.Length && a[endBracket + 1] == ':')
            {
                host = a[..(endBracket + 1)];
                var tail = a[(endBracket + 2)..];
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0)
                {
                    port = p;
                }

                return true;
            }
        }

        var idx = a.LastIndexOf(':');
        if (idx > 0 && idx < a.Length - 1)
        {
            var tail = a[(idx + 1)..];
            if (tail.Length > 0 && tail.All(static c => c is >= '0' and <= '9'))
            {
                host = a[..idx];
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0)
                {
                    port = p;
                }

                return true;
            }
        }

        host = a;
        return true;
    }
}
