using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Hubs;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class WorkerPushNotifier(
    IHubContext<WorkerHub> hub,
    WorkerConnectionRegistry registry,
    IServiceScopeFactory scopeFactory) : IWorkerPushNotifier
{
    public async Task<bool> TryPushCommandAsync(Guid workerId, string command, CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return false;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(WorkerHubEvents.ExecuteCommand, new WorkerPushCommandMessage(command), ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task PushConfigChangedAsync(Guid workerId, CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(
                WorkerHubEvents.ConfigChanged,
                new WorkerConfigChangedMessage(DateTime.UtcNow),
                ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryPushCaptchaSessionAsync(
        Guid workerId,
        WorkerPendingCaptchaSessionDto session,
        CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return false;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(WorkerHubEvents.CaptchaSession, session, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryPushBrowserMonitorSessionAsync(
        Guid workerId,
        WorkerPendingBrowserMonitorSessionDto session,
        CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return false;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(WorkerHubEvents.BrowserMonitorSession, session, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryPushLocalChromeLoginSessionAsync(
        Guid workerId,
        WorkerPendingLocalChromeLoginDto session,
        CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return false;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(WorkerHubEvents.LocalChromeLoginSession, session, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryPushTopUpSessionAsync(
        Guid workerId,
        WorkerPendingTopUpSessionDto session,
        CancellationToken ct = default)
    {
        if (!registry.TryGetConnectionId(workerId, out var connectionId) || connectionId is null)
        {
            return false;
        }

        await hub.Clients
            .Client(connectionId)
            .SendAsync(WorkerHubEvents.TopUpSession, session, ct)
            .ConfigureAwait(false);
        return true;
    }

    public async Task DeliverPendingOnConnectAsync(Guid workerId, CancellationToken ct = default)
    {
        if (!registry.IsConnected(workerId))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
        var captchaSessions = scope.ServiceProvider.GetRequiredService<CaptchaSessionService>();
        var browserMonitorSessions = scope.ServiceProvider.GetRequiredService<BrowserMonitorService>();
        var localChromeLoginSessions = scope.ServiceProvider.GetRequiredService<LocalChromeLoginSessionService>();

        var worker = await db.Workers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(worker.PendingCommand))
        {
            await TryPushCommandAsync(workerId, worker.PendingCommand, ct).ConfigureAwait(false);
        }

        var pendingCaptcha = await captchaSessions
            .GetPendingForWorkerAsync(workerId, ct)
            .ConfigureAwait(false);
        if (pendingCaptcha is not null)
        {
            await TryPushCaptchaSessionAsync(workerId, pendingCaptcha, ct).ConfigureAwait(false);
        }

        var pendingBrowserMonitor = await browserMonitorSessions
            .GetPendingForWorkerAsync(workerId, ct)
            .ConfigureAwait(false);
        if (pendingBrowserMonitor is not null)
        {
            await TryPushBrowserMonitorSessionAsync(workerId, pendingBrowserMonitor, ct).ConfigureAwait(false);
        }

        var pendingLocalLogin = localChromeLoginSessions.GetPendingForWorker(workerId);
        if (pendingLocalLogin is not null)
        {
            await TryPushLocalChromeLoginSessionAsync(workerId, pendingLocalLogin, ct).ConfigureAwait(false);
        }

        var topUpSessions = scope.ServiceProvider.GetRequiredService<TopUpSessionService>();
        var pendingTopUp = await topUpSessions
            .GetPendingForWorkerAsync(workerId, ct)
            .ConfigureAwait(false);
        if (pendingTopUp is not null)
        {
            await TryPushTopUpSessionAsync(workerId, pendingTopUp, ct).ConfigureAwait(false);
        }

        await PushConfigChangedAsync(workerId, ct).ConfigureAwait(false);
    }
}