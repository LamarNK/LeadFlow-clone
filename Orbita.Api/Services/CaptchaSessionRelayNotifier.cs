using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CaptchaSessionRelayNotifier(IHubContext<CaptchaRelayHub> hub) : ICaptchaSessionRelayNotifier
{
    public Task NotifyStateChangedAsync(CaptchaStateChangedMessage message, CancellationToken ct = default) =>
        hub.Clients
            .Group(CaptchaRelayHub.SessionGroup(message.SessionId))
            .SendAsync("StateChanged", message, ct);
}
