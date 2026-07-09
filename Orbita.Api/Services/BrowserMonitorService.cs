using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class BrowserMonitorService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IWorkerPushNotifier workerPushNotifier,
    BrowserMonitorRegistry registry)
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<Guid, MonitorSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeSessionByWorker = new();

    public async Task<(BrowserMonitorSessionDto? Session, string? Error)> StartAsync(
        Guid workerId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        RemoveExpired();

        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return (null, "Нет доступа.");
        }

        var worker = await db.Workers
            .Include(x => x.Accounts)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null || !await officeScope.CanAccessWorkerAsync(scope, worker.Id, ct).ConfigureAwait(false))
        {
            return (null, "Воркер не найден.");
        }

        var operatorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var operatorDisplayName = principal.Identity?.Name
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? "Оператор";

        await StopSessionsForWorkerAsync(workerId, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var browsers = BuildBrowserCatalog(worker);
        var session = new MonitorSession
        {
            Id = Guid.NewGuid(),
            WorkerId = worker.Id,
            WorkerName = worker.DisplayName,
            OperatorUserId = operatorUserId,
            OperatorDisplayName = operatorDisplayName,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl),
            Browsers = browsers
        };

        _sessions[session.Id] = session;
        _activeSessionByWorker[worker.Id] = session.Id;

        var pending = new WorkerPendingBrowserMonitorSessionDto(session.Id, worker.Id, browsers);
        await workerPushNotifier.TryPushBrowserMonitorSessionAsync(worker.Id, pending, ct).ConfigureAwait(false);

        return (ToDto(session), null);
    }

    public Task<BrowserMonitorSessionDto?> GetAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default) =>
        TryGetAuthorizedSessionAsync(sessionId, principal, ct);

    public async Task<(bool Success, string? Error)> StopAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var session = await TryGetAuthorizedSessionEntityAsync(sessionId, principal, ct).ConfigureAwait(false);
        if (session is null)
        {
            return (false, "Сессия не найдена.");
        }

        RemoveSession(session.Id, session.WorkerId);
        return (true, null);
    }

    public async Task<WorkerPendingBrowserMonitorSessionDto?> GetPendingForWorkerAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        RemoveExpired();

        if (!_activeSessionByWorker.TryGetValue(workerId, out var sessionId)
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        var worker = await db.Workers.AsNoTracking()
            .Include(x => x.Accounts)
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null)
        {
            return null;
        }

        session.Browsers = BuildBrowserCatalog(worker);
        return new WorkerPendingBrowserMonitorSessionDto(session.Id, workerId, session.Browsers);
    }

    public bool TryAuthorizeOperator(Guid sessionId, string? userId, bool isAdmin, out MonitorSession? session)
    {
        session = null;
        if (!_sessions.TryGetValue(sessionId, out var found) || found.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        if (!isAdmin && !string.Equals(found.OperatorUserId, userId, StringComparison.Ordinal))
        {
            return false;
        }

        session = found;
        return true;
    }

    public bool TryAuthorizeWorker(Guid sessionId, Guid workerId, out MonitorSession? session)
    {
        session = null;
        if (!_activeSessionByWorker.TryGetValue(workerId, out var activeSessionId)
            || activeSessionId != sessionId
            || !_sessions.TryGetValue(sessionId, out var found)
            || found.ExpiresAtUtc <= DateTime.UtcNow
            || found.WorkerId != workerId)
        {
            return false;
        }

        session = found;
        return true;
    }

    public void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAtUtc > now)
            {
                continue;
            }

            RemoveSession(pair.Key, pair.Value.WorkerId);
        }
    }

    private async Task StopSessionsForWorkerAsync(Guid workerId, CancellationToken ct)
    {
        var toRemove = _sessions.Values
            .Where(x => x.WorkerId == workerId)
            .Select(x => x.Id)
            .ToList();

        foreach (var sessionId in toRemove)
        {
            RemoveSession(sessionId, workerId);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void RemoveSession(Guid sessionId, Guid workerId)
    {
        _sessions.TryRemove(sessionId, out _);
        registry.Remove(sessionId);

        if (_activeSessionByWorker.TryGetValue(workerId, out var activeId)
            && activeId == sessionId)
        {
            _activeSessionByWorker.TryRemove(workerId, out _);
        }
    }

    private async Task<BrowserMonitorSessionDto?> TryGetAuthorizedSessionAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var session = await TryGetAuthorizedSessionEntityAsync(sessionId, principal, ct).ConfigureAwait(false);
        return session is null ? null : ToDto(session);
    }

    private async Task<MonitorSession?> TryGetAuthorizedSessionEntityAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        RemoveExpired();

        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess || !_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            RemoveSession(sessionId, session.WorkerId);
            return null;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = principal.IsInRole(PanelRoles.Admin);
        if (!isAdmin && !string.Equals(session.OperatorUserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return null;
        }

        return session;
    }

    private static IReadOnlyList<BrowserMonitorBrowserDto> BuildBrowserCatalog(WorkerEntity worker)
    {
        var index = 0;
        return worker.Accounts
            .Where(x => x.IsEnabledInPanel)
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(account =>
            {
                index++;
                var status = ResolveCatalogStatus(account.Status, account.LastErrorMessage);
                return new BrowserMonitorBrowserDto(
                    account.AccountId,
                    account.DisplayName,
                    account.AdsPowerProfileId ?? string.Empty,
                    index,
                    status,
                    StatusMessage: account.LastErrorMessage);
            })
            .ToList();
    }

    private static string ResolveCatalogStatus(string? status, string? lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            return BrowserMonitorStatuses.Error;
        }

        return string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "requires_login", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "requires_manual_action", StringComparison.OrdinalIgnoreCase)
            ? BrowserMonitorStatuses.Error
            : BrowserMonitorStatuses.Stopped;
    }

    private static BrowserMonitorSessionDto ToDto(MonitorSession session) =>
        new(
            session.Id,
            session.WorkerId,
            session.WorkerName,
            session.OperatorUserId,
            session.OperatorDisplayName,
            session.CreatedAtUtc,
            session.ExpiresAtUtc,
            session.Browsers);

    public sealed class MonitorSession
    {
        public Guid Id { get; init; }
        public Guid WorkerId { get; init; }
        public required string WorkerName { get; init; }
        public required string OperatorUserId { get; init; }
        public required string OperatorDisplayName { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public IReadOnlyList<BrowserMonitorBrowserDto> Browsers { get; set; } = [];
    }
}