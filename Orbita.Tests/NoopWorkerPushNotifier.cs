using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class NoopWorkerPushNotifier : IWorkerPushNotifier
{
    public Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<bool> TryPushCaptchaSessionAsync(
        Guid workerId,
        WorkerPendingCaptchaSessionDto session,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> TryPushBrowserMonitorSessionAsync(
        Guid workerId,
        WorkerPendingBrowserMonitorSessionDto session,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> TryPushLocalChromeLoginSessionAsync(
        Guid workerId,
        WorkerPendingLocalChromeLoginDto session,
        CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default) =>
        Task.CompletedTask;
}