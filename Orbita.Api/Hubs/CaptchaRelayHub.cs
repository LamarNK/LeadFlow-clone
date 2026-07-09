using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Orbita.Api.Services;
using Orbita.Contracts;
using Orbita.Logging.Audit;

namespace Orbita.Api.Hubs;

[Authorize(Policy = "WorkerOrPanel")]
public sealed class CaptchaRelayHub(
    CaptchaRelayRegistry registry,
    CaptchaSessionService sessions,
    IHubContext<CaptchaRelayHub> self) : Hub
{
    public static string SessionGroup(Guid sessionId) => $"captcha:{sessionId:D}";

    public async Task JoinAsOperator(Guid sessionId)
    {
        var principal = Context.User ?? throw new HubException("Не авторизован.");
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = principal.IsInRole(PanelRoles.Admin);
        if (!await sessions.IsOperatorForSessionAsync(sessionId, userId, isAdmin, Context.ConnectionAborted).ConfigureAwait(false))
        {
            throw new HubException("Только оператор, начавший сессию, может подключиться.");
        }

        var relay = registry.GetOrAdd(sessionId);
        relay.OperatorConnectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId)).ConfigureAwait(false);

        await GlobalLogger.Instance.LogAsync(
            $"Captcha: оператор подключился к сессии {sessionId:D}.",
            DeskLinkAuditLogLevel.Info,
            errorKey: "captcha.operator.joined",
            properties: new Dictionary<string, object?>
            {
                ["captcha.sessionId"] = sessionId,
                ["connection.id"] = Context.ConnectionId
            }).ConfigureAwait(false);

        if (relay.LastFrame is not null
            && (relay.LastSnapshot is null || relay.LastFrame.TimestampMs >= relay.LastSnapshot.TimestampMs))
        {
            await Clients.Caller
                .SendAsync("Frame", relay.LastFrame, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
        else if (relay.LastSnapshot is not null)
        {
            await Clients.Caller
                .SendAsync("Snapshot", relay.LastSnapshot, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
    }

    public async Task JoinAsWorker(Guid sessionId)
    {
        var workerId = ResolveWorkerId();
        var session = await sessions.GetForWorkerAsync(workerId, sessionId, Context.ConnectionAborted).ConfigureAwait(false);
        if (session is null)
        {
            throw new HubException("Сессия не найдена для воркера.");
        }

        var relay = registry.GetOrAdd(sessionId);
        relay.WorkerConnectionId = Context.ConnectionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId)).ConfigureAwait(false);
    }

    public async Task SendSnapshot(CaptchaSnapshotMessage message)
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

        relay.LastSnapshot = message;

        if (!string.IsNullOrWhiteSpace(relay.OperatorConnectionId))
        {
            await self.Clients.Client(relay.OperatorConnectionId)
                .SendAsync("Snapshot", message, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return;
        }

        if (relay.SnapshotsWithoutOperatorLogged == 0)
        {
            relay.SnapshotsWithoutOperatorLogged = 1;
            await GlobalLogger.Instance.LogAsync(
                $"Captcha: оператор ещё не подключён к сессии {message.SessionId:D}, снимки уходят только в группу.",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "captcha.snapshot.no_operator",
                properties: new Dictionary<string, object?>
                {
                    ["captcha.sessionId"] = message.SessionId,
                    ["captcha.payloadLength"] = message.MhtmlGzipBase64.Length
                }).ConfigureAwait(false);
        }

        await self.Clients
            .GroupExcept(SessionGroup(message.SessionId), Context.ConnectionId)
            .SendAsync("Snapshot", message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task SendFrame(CaptchaFrameMessage message)
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

        relay.LastFrame = message;

        if (!string.IsNullOrWhiteSpace(relay.OperatorConnectionId))
        {
            await self.Clients.Client(relay.OperatorConnectionId)
                .SendAsync("Frame", message, Context.ConnectionAborted)
                .ConfigureAwait(false);
            return;
        }

        if (relay.FramesWithoutOperatorLogged == 0)
        {
            relay.FramesWithoutOperatorLogged = 1;
            await GlobalLogger.Instance.LogAsync(
                $"Captcha: оператор ещё не подключён к сессии {message.SessionId:D}, live frames уходят только в группу.",
                DeskLinkAuditLogLevel.Warning,
                errorKey: "captcha.frame.no_operator",
                properties: new Dictionary<string, object?>
                {
                    ["captcha.sessionId"] = message.SessionId,
                    ["captcha.payloadLength"] = message.ImageBase64.Length
                }).ConfigureAwait(false);
        }

        await self.Clients
            .GroupExcept(SessionGroup(message.SessionId), Context.ConnectionId)
            .SendAsync("Frame", message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    public async Task SendInput(CaptchaInputMessage message)
    {
        if (!registry.TryGet(message.SessionId, out var relay)
            || relay is null
            || !string.Equals(relay.OperatorConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            return;
        }
        relay.PendingInputs.Enqueue(new CaptchaInputEnvelope(
            message.EventType,
            message.X,
            message.Y,
            message.TimestampMs,
            message.Button,
            message.Buttons,
            message.Key,
            message.Code,
            message.AltKey,
            message.CtrlKey,
            message.ShiftKey,
            message.MetaKey,
            message.Repeat));

        if (!string.IsNullOrWhiteSpace(relay.WorkerConnectionId))
        {
            await self.Clients.Client(relay.WorkerConnectionId)
                .SendAsync("Input", message, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }
    }

    public async Task NotifyStateChanged(CaptchaStateChangedMessage message)
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

        await self.Clients
            .Group(SessionGroup(message.SessionId))
            .SendAsync("StateChanged", message, Context.ConnectionAborted)
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
