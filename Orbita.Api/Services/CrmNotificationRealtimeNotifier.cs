using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Sends a wake-up signal to one authenticated panel user. The durable database row remains
/// the source of truth, so a disconnected browser can retrieve the notification later.
/// </summary>
public sealed class CrmNotificationRealtimeNotifier(IHubContext<PanelHub> hub)
    : ICrmNotificationRealtimeNotifier
{
    public Task NotifyAsync(
        string recipientUserId,
        CrmTaskNotificationDto notification,
        CancellationToken ct = default) =>
        hub.Clients.User(recipientUserId)
            .SendAsync("CrmNotificationChanged", notification, ct);
}
