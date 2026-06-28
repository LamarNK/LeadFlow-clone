using System.Net;
using System.Net.Http;
using System.Text.Json;


namespace LeadFlow.Services.Browser;

public sealed class ProxyCheckService : IProxyCheckService
{
    private static readonly Uri IpEndpoint = new("https://api.ipify.org", UriKind.Absolute);
    private static readonly Uri TimezoneEndpointTemplate = new("https://ipwho.is/", UriKind.Absolute);

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

    public async Task<string> ResolveTimezoneByIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        var ip = ipAddress?.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            throw new InvalidOperationException("Внешний IP не задан для определения часового пояса.");
        }

        if (!IPAddress.TryParse(ip, out _))
        {
            throw new InvalidOperationException($"Некорректный IP: {ip}");
        }

        var requestUri = new Uri(TimezoneEndpointTemplate, ip);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        if (root.TryGetProperty("success", out var successEl)
            && successEl.ValueKind == JsonValueKind.False)
        {
            throw new InvalidOperationException("GeoIP-сервис не смог определить часовой пояс по IP.");
        }

        string? timezone = null;
        if (root.TryGetProperty("timezone", out var timezoneEl))
        {
            if (timezoneEl.ValueKind == JsonValueKind.Object
                && timezoneEl.TryGetProperty("id", out var timezoneIdEl)
                && timezoneIdEl.ValueKind == JsonValueKind.String)
            {
                timezone = timezoneIdEl.GetString();
            }
            else if (timezoneEl.ValueKind == JsonValueKind.String)
            {
                timezone = timezoneEl.GetString();
            }
        }

        timezone = timezone?.Trim();
        if (string.IsNullOrWhiteSpace(timezone) || !timezone.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GeoIP-сервис вернул некорректный часовой пояс.");
        }

        return timezone;
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
