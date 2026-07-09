using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CaptchaLockNotifier(IHubContext<PanelHub> hub) : ICaptchaLockNotifier
{
    public Task NotifyLockChangedAsync(
        Guid workerId,
        Guid officeId,
        WorkerCaptchaLockDto lockState,
        CancellationToken ct = default)
    {
        var message = new WorkerCaptchaLockChangedMessage(workerId, officeId, lockState.IsLocked, lockState);
        return hub.Clients
            .Group(PanelHub.OfficeGroup(officeId))
            .SendAsync("WorkerCaptchaLockChanged", message, ct);
    }
}