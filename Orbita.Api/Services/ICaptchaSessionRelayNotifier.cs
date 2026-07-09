using Orbita.Contracts;

namespace Orbita.Api.Services;

public interface ICaptchaSessionRelayNotifier
{
    Task NotifyStateChangedAsync(CaptchaStateChangedMessage message, CancellationToken ct = default);
}
