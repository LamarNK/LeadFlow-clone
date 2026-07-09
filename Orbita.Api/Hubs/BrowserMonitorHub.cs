using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Hubs;

[Authorize(Policy = "WorkerOrPanel")]
public sealed class BrowserMonitorHub(
    BrowserMonitorRegistry registry,
    BrowserMonitorService sessions,
    IHubContext<BrowserMonitorHub> self) : Hub
{
    public static string SessionGroup(Guid sessionId) => $"browser-monitor:{sessionId:D}";

    public async Task JoinAsOperator(Guid sessionId)
    {
        var principal = Context.User ?? throw new HubException("Не авторизован.");
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = principal.IsInRole(PanelRoles.Admin);
        if (!sessions.TryAuthorizeOperator(sessionId, userId, isAdmin, out _))
        {
            throw new HubException("Нет доступа к сессии просмотра.");
        }

        var relay = registry.GetOrAdd(sessionId);
        relay.OperatorConnectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId)).ConfigureAwait(false);

        if (relay.LastCatalog is not null)
        {
            await Clients.Caller
                .SendAsync("Catalog", relay.LastCatalog, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }

        foreach (var frame in relay.LastFrames.Values.OrderBy(x => x.TimestampMs))
        {
            await Clients.Caller
                .SendAsync("Frame", frame, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
    }

    public async Task JoinAsWorker(Guid sessionId)
    {
        var workerId = ResolveWorkerId();
        if (!sessions.TryAuthorizeWorker(sessionId, workerId, out _))
        {
            throw new HubException("Сессия не найдена для воркера.");
        }

        var relay = registry.GetOrAdd(sessionId);
        relay.WorkerConnectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId)).ConfigureAwait(false);
    }

    public async Task SendCatalog(BrowserMonitorCatalogMessage message)
    {
        if (!TryResolveWorkerId(out _))
        {
            return;
        }

        if (!registry.TryGet(message.SessionId, out var relay)
            || relay is null
            || !string.Equals(relay.WorkerConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }

        relay.LastCatalog = message;

        if (!string.IsNullOrWhiteSpace(relay.OperatorConnectionId))
        {
            await self.Clients.Client(relay.OperatorConnectionId)
                .SendAsync("Catalog", message, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return;
        }

        await self.Clients
            .GroupExcept(SessionGroup(message.SessionId), Context.ConnectionId)
            .SendAsync("Catalog", message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task SendFrame(BrowserMonitorFrameMessage message)
    {
        if (!TryResolveWorkerId(out _))
        {
            return;
        }

        if (!registry.TryGet(message.SessionId, out var relay)
            || relay is null
            || !string.Equals(relay.WorkerConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }

        relay.LastFrames[message.AccountId] = message;

        if (!string.IsNullOrWhiteSpace(relay.OperatorConnectionId))
        {
            await self.Clients.Client(relay.OperatorConnectionId)
                .SendAsync("Frame", message, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return;
        }

        await self.Clients
            .GroupExcept(SessionGroup(message.SessionId), Context.ConnectionId)
            .SendAsync("Frame", message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.ClearConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private Guid ResolveWorkerId()
    {
        if (!TryResolveWorkerId(out var workerId))
        {
            throw new HubException("Воркер не авторизован.");
        }

        return workerId;
    }

    private bool TryResolveWorkerId(out Guid workerId)
    {
        workerId = default;
        var principal = Context.User;
        if (principal?.Identity?.AuthenticationType != Auth.WorkerApiKeyAuthenticationHandler.SchemeName)
        {
            return false;
        }

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(id, out workerId);
    }
}