using LeadFlow.Models;

namespace LeadFlow.Services.Browser;

public interface IProxyCheckService
{
    /// <summary>Проверка исходящего IP через HTTP-прокси (SOCKS5 не поддерживается).</summary>
    Task<string> CheckPublicIpAsync(AvitoAccount account, CancellationToken cancellationToken = default);

    /// <summary>GET по URL смены IP (ротация у провайдера прокси).</summary>
    Task RequestRotationUrlAsync(string? rotationUrl, CancellationToken cancellationToken = default);
}
