using System.Security.Claims;
using Orbita.Logging.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Api.Hubs;

[Authorize(Policy = "WorkerOrPanel")]
public sealed class BrowserMonitorHub(
    BrowserMonitorRegistry registry,
    BrowserMonitorService sessions,
    IHubContext<BrowserMonitorHub> self,
    ILogger<BrowserMonitorHub> logger) : Hub
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

        await GlobalLogger.Instance.LogAsync(
            $"Browser monitor: оператор подключился к сессии {sessionId:D}.",
            DeskLinkAuditLogLevel.Info,
            errorKey: "browser.monitor.operator.joined",
            properties: new Dictionary<string, object?>
            {
                ["browserMonitor.sessionId"] = sessionId,
                ["connection.id"] = Context.ConnectionId,
                ["browserMonitor.lastCatalogCount"] = relay.LastCatalog?.Browsers.Count ?? 0
            }).ConfigureAwait(false);

        if (relay.LastCatalog is not null)
        {
            await Clients.Caller
                .SendAsync("Catalog", relay.LastCatalog, Context.ConnectionAborted)
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

        await GlobalLogger.Instance.LogAsync(
            $"Browser monitor: воркер {workerId:D} подключился к сессии {sessionId:D}.",
            DeskLinkAuditLogLevel.Info,
            errorKey: "browser.monitor.worker.joined",
            properties: new Dictionary<string, object?>
            {
                ["browserMonitor.sessionId"] = sessionId,
                ["browserMonitor.workerId"] = workerId,
                ["connection.id"] = Context.ConnectionId
            }).ConfigureAwait(false);
    }

    public async Task SendCatalog(BrowserMonitorCatalogMessage message)
    {
        if (!TryResolveWorkerId(out var workerId))
        {
            return;
        }

        if (!TryGetAuthorizedWorkerRelay(message.SessionId, workerId, out var relay))
        {
            LogRelayRejected("catalog", message.SessionId, workerId);
            return;
        }

        relay!.LastCatalog = message;
        sessions.UpdateSessionCatalog(message.SessionId, message.Browsers);

        logger.LogInformation(
            "Browser monitor catalog relayed for session {SessionId}: {BrowserCount} browsers.",
            message.SessionId,
            message.Browsers.Count);

        await RelayToOperatorAsync(relay, "Catalog", message).ConfigureAwait(false);
    }

    public async Task SendFrame(BrowserMonitorFrameMessage message)
    {
        if (!TryResolveWorkerId(out var workerId))
        {
            return;
        }

        if (!TryGetAuthorizedWorkerRelay(message.SessionId, workerId, out var relay))
        {
            LogRelayRejected("frame", message.SessionId, workerId, message.AccountId);
            return;
        }

        await RelayToOperatorAsync(relay!, "Frame", message).ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.ClearConnection(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private bool TryGetAuthorizedWorkerRelay(Guid sessionId, Guid workerId, out BrowserMonitorRegistry.MonitorRelay? relay)
    {
        relay = null;
        if (!registry.TryGet(sessionId, out var found)
            || found is null
            || !sessions.TryAuthorizeWorker(sessionId, workerId, out _))
        {
            return false;
        }

        if (!string.Equals(found.WorkerConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Browser monitor worker connection refreshed for session {SessionId}: {PreviousConnectionId} -> {ConnectionId}.",
                sessionId,
                found.WorkerConnectionId,
                Context.ConnectionId);
            found.WorkerConnectionId = Context.ConnectionId;
        }

        relay = found;
        return true;
    }

    private async Task RelayToOperatorAsync<TMessage>(
        BrowserMonitorRegistry.MonitorRelay relay,
        string eventName,
        TMessage message)
    {
        if (!string.IsNullOrWhiteSpace(relay.OperatorConnectionId))
        {
            await self.Clients.Client(relay.OperatorConnectionId)
                .SendAsync(eventName, message, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return;
        }

        await self.Clients
            .GroupExcept(SessionGroup(relay.SessionId), Context.ConnectionId)
            .SendAsync(eventName, message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    private void LogRelayRejected(string kind, Guid sessionId, Guid workerId, Guid? accountId = null)
    {
        logger.LogWarning(
            "Browser monitor {Kind} rejected for session {SessionId} from worker {WorkerId} on connection {ConnectionId}. AccountId={AccountId}",
            kind,
            sessionId,
            workerId,
            Context.ConnectionId,
            accountId);

        _ = GlobalLogger.Instance.LogAsync(
            $"Browser monitor: {kind} отклонён для сессии {sessionId:D} (worker {workerId:D}, connection {Context.ConnectionId}).",
            DeskLinkAuditLogLevel.Warning,
            errorKey: $"browser.monitor.relay.rejected.{kind}",
            properties: new Dictionary<string, object?>
            {
                ["browserMonitor.sessionId"] = sessionId,
                ["browserMonitor.workerId"] = workerId,
                ["connection.id"] = Context.ConnectionId,
                ["browserMonitor.accountId"] = accountId
            });
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