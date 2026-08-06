using Orbita.Contracts;

namespace Orbita.Api.Services;

public interface ICrmNotificationRealtimeNotifier
{
    Task NotifyAsync(
        string recipientUserId,
        CrmTaskNotificationDto notification,
        CancellationToken ct = default);
}
