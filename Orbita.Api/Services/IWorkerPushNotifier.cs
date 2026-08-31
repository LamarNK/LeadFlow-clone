using Orbita.Contracts;

namespace Orbita.Api.Services;

public interface IWorkerPushNotifier
{
    Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default);

    Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default);

    Task<bool> TryPushCaptchaSessionAsync(
        Guid workerId,
        WorkerPendingCaptchaSessionDto session,
        CancellationToken ct = default);

    Task<bool> TryPushBrowserMonitorSessionAsync(
        Guid workerId,
        WorkerPendingBrowserMonitorSessionDto session,
        CancellationToken ct = default);

    Task<bool> TryPushLocalChromeLoginSessionAsync(
        Guid workerId,
        WorkerPendingLocalChromeLoginDto session,
        CancellationToken ct = default);

    Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default);
}