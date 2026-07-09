using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Hubs;

[Authorize(Policy = "Worker")]
public sealed class WorkerHub(
    WorkerConnectionRegistry registry,
    IWorkerPushNotifier pushNotifier,
    IServiceScopeFactory scopeFactory) : Hub
{
    public async Task Register(WorkerHubRegisterRequest request)
    {
        var workerId = ResolveWorkerId();
        var registration = registry.Register(workerId, Context.ConnectionId);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
        var panelRealtime = scope.ServiceProvider.GetRequiredService<IPanelRealtimeNotifier>();

        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (worker is not null)
        {
            worker.LastSeenAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.AppVersion))
            {
                worker.AppVersion = request.AppVersion.Trim();
            }

            await db.SaveChangesAsync(Context.ConnectionAborted).ConfigureAwait(false);
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Dashboard, PanelChangeKind.Accounts],
                worker.OfficeId,
                worker.Id);
        }

        if (registration.DisplacedConnectionId is not null)
        {
            await Clients.Client(registration.DisplacedConnectionId)
                .SendAsync("ConnectionReplaced", Context.ConnectionAborted)
                .ConfigureAwait(false);
        }

        await pushNotifier.DeliverPendingOnConnectAsync(workerId, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task Heartbeat(WorkerHeartbeatRequest request)
    {
        var workerId = ResolveWorkerId();
        if (request.WorkerId != workerId)
        {
            throw new HubException("WorkerId не совпадает с авторизацией.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var telemetry = scope.ServiceProvider.GetRequiredService<TelemetryService>();
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        await telemetry.HeartbeatAsync(request, ip, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task AckCommand(string command)
    {
        var workerId = ResolveWorkerId();
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
        var worker = await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, Context.ConnectionAborted)
            .ConfigureAwait(false);
        if (worker is null)
        {
            return;
        }

        if (string.Equals(worker.PendingCommand, command.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            worker.PendingCommand = null;
            worker.PendingCommandAtUtc = null;
            await db.SaveChangesAsync(Context.ConnectionAborted).ConfigureAwait(false);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (registry.TryUnregister(Context.ConnectionId, out var info) && info is not null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
            var panelRealtime = scope.ServiceProvider.GetRequiredService<IPanelRealtimeNotifier>();
            var worker = await db.Workers.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == info.WorkerId, Context.ConnectionAborted)
                .ConfigureAwait(false);
            if (worker is not null)
            {
                panelRealtime.Notify(
                    [PanelChangeKind.Workers, PanelChangeKind.Dashboard, PanelChangeKind.Accounts],
                    worker.OfficeId,
                    worker.Id);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private Guid ResolveWorkerId()
    {
        var value = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(value, out var workerId))
        {
            throw new HubException("Не авторизован.");
        }

        return workerId;
    }
}