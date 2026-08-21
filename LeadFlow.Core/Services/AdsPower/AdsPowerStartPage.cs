using System.Net;
using System.Text.RegularExpressions;

namespace LeadFlow.Core.Services.AdsPower;

internal enum AdsPowerStartPageProxyStatus
{
    Unknown = 0,
    Failed = 1,
    Ok = 2
}

/// <summary>
/// Стартовая вкладка AdsPower с проверкой прокси: <c>https://start.adspower.net/?id=…&amp;host=…</c>.
/// </summary>
internal static partial class AdsPowerStartPage
{
    public static bool IsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("start.adspower.net", StringComparison.OrdinalIgnoreCase)
               || host.Equals("start.adspower.com", StringComparison.OrdinalIgnoreCase)
               || (host.StartsWith("start.", StringComparison.OrdinalIgnoreCase)
                   && host.Contains("adspower", StringComparison.OrdinalIgnoreCase));
    }

    public static AdsPowerStartPageProxyStatus Parse(string? htmlOrText)
    {
        if (string.IsNullOrWhiteSpace(htmlOrText))
        {
            return AdsPowerStartPageProxyStatus.Unknown;
        }

        if (LooksFailed(htmlOrText))
        {
            return AdsPowerStartPageProxyStatus.Failed;
        }

        return LooksOk(htmlOrText)
            ? AdsPowerStartPageProxyStatus.Ok
            : AdsPowerStartPageProxyStatus.Unknown;
    }

    private static bool LooksFailed(string s) =>
        s.Contains("proxy__fail", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Proxy failure", StringComparison.OrdinalIgnoreCase)
        || s.Contains("Proxy failed", StringComparison.OrdinalIgnoreCase)
        || s.Contains("代理失败", StringComparison.Ordinal)
        || s.Contains("прокси не работает", StringComparison.OrdinalIgnoreCase);

    private static bool LooksOk(string s) => TryFindPublicIpv4(s) is not null;

    internal static string? TryFindPublicIpv4(string text)
    {
        foreach (Match match in Ipv4Regex().Matches(text))
        {
            if (!IPAddress.TryParse(match.Value, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            var octets = address.GetAddressBytes();
            if (octets[0] == 127 || octets[0] == 0 || octets[0] == 255)
            {
                continue;
            }

            return address.ToString();
        }

        return null;
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();
}
