using System.Text;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

/// <summary>
/// Собирает <see cref="CoreWebView2EnvironmentOptions.AdditionalBrowserArguments"/> из прокси и пользовательских флагов.
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

    /// <summary>Значение для <c>--proxy-server=...</c> (без префикса).</summary>
    public static string? BuildProxyServerSwitchValue(AvitoAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.ProxyAddress))
        {
            return null;
        }

        var addr = account.ProxyAddress.Trim();
        var user = account.ProxyUsername?.Trim();
        var pass = account.ProxyPassword ?? string.Empty;
        var hasAuth = !string.IsNullOrEmpty(user);

        if (account.ProxyType == "socks5")
        {
            if (hasAuth)
            {
                var u = Uri.EscapeDataString(user!);
                var p = Uri.EscapeDataString(pass);
                if (addr.Contains("://", StringComparison.Ordinal))
                {
                    var uri = new Uri(addr);
                    return $"socks5://{u}:{p}@{uri.Host}:{uri.Port}";
                }

                return $"socks5://{u}:{p}@{addr}";
            }

            return addr.Contains("://", StringComparison.Ordinal) ? addr : $"socks5://{addr}";
        }

        // http
        if (hasAuth)
        {
            var u = Uri.EscapeDataString(user!);
            var p = Uri.EscapeDataString(pass);
            if (addr.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || addr.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(addr);
                var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
                return $"http://{u}:{p}@{uri.Host}{port}";
            }

            return $"http://{u}:{p}@{addr}";
        }

        return addr.Contains("://", StringComparison.OrdinalIgnoreCase) ? addr : $"http://{addr}";
    }
}
