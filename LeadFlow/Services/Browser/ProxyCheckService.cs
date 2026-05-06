using System.Net;
using System.Net.Http;
using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public sealed class ProxyCheckService : IProxyCheckService
{
    private static readonly Uri IpEndpoint = new("https://api.ipify.org", UriKind.Absolute);

    public async Task<string> CheckPublicIpAsync(AvitoAccount account, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(account.ProxyAddress))
        {
            throw new InvalidOperationException("Адрес прокси не задан.");
        }

        if (string.Equals(account.ProxyType, "socks5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Проверка через SOCKS5 в этом режиме не поддерживается.");
        }

        var handler = new HttpClientHandler { UseProxy = true };
        var hostPort = account.ProxyAddress.Trim();
        if (!hostPort.Contains("://", StringComparison.Ordinal))
        {
            hostPort = "http://" + hostPort;
        }

        var proxyUri = new Uri(hostPort);
        handler.Proxy = new WebProxy(proxyUri)
        {
            Credentials = string.IsNullOrEmpty(account.ProxyUsername)
                ? null
                : new NetworkCredential(account.ProxyUsername, account.ProxyPassword ?? string.Empty)
        };

        using var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        var ip = await client.GetStringAsync(IpEndpoint, cancellationToken).ConfigureAwait(false);
        return ip.Trim();
    }

    public async Task RequestRotationUrlAsync(string? rotationUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rotationUrl))
        {
            throw new InvalidOperationException("URL смены IP не задан.");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(rotationUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
