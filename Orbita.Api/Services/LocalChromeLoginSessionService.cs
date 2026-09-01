using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class LocalChromeLoginSessionService(
    IServiceScopeFactory scopeFactory,
    IWorkerPushNotifier workerPushNotifier)
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(45);
    private static readonly JsonSerializerOptions ActivityJson = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, LoginSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeByWorker = new();

    public async Task<(WorkerPendingLocalChromeLoginDto? Session, string? Error)> StartAsync(
        Guid workerId,
        Guid accountId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        RemoveExpired();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrbitaDbContext>();
        var officeScope = scope.ServiceProvider.GetRequiredService<OfficeScopeService>();

        var access = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!access.HasAccess)
        {
            return (null, "Нет доступа.");
        }

        var worker = await db.Workers
            .AsNoTracking()
            .Include(x => x.Accounts)
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null || !await officeScope.CanAccessWorkerAsync(access, worker.Id, ct).ConfigureAwait(false))
        {
            return (null, "Воркер не найден.");
        }

        var account = worker.Accounts.FirstOrDefault(x => x.AccountId == accountId);
        if (account is null)
        {
            return (null, "Аккаунт не найден.");
        }

        if (!WorkerConfigService.IsLocalAccount(account))
        {
            return (null, "Открыть браузер для входа можно только у аккаунта обычного браузера.");
        }

        if (IsAccountMonitoringNow(worker, accountId))
        {
            return (null, "Сначала дождитесь окончания мониторинга этого аккаунта.");
        }

        if (_activeByWorker.TryGetValue(workerId, out var existingSessionId)
            && _sessions.TryGetValue(existingSessionId, out var existing)
            && existing.ExpiresAtUtc > DateTime.UtcNow
            && !existing.Completed)
        {
            return (null, existing.AccountId == accountId
                ? "Браузер для входа уже открыт."
                : "Сначала закройте уже открытый браузер для входа.");
        }

        if (_sessions.Values.Any(x =>
                x.WorkerId == workerId
                && x.AccountId == accountId
                && x.ExpiresAtUtc > DateTime.UtcNow
                && !x.Completed))
        {
            return (null, "Браузер для входа уже открыт.");
        }

        var now = DateTime.UtcNow;
        var session = new LoginSession
        {
            Id = Guid.NewGuid(),
            WorkerId = worker.Id,
            AccountId = account.AccountId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl)
        };
        _sessions[session.Id] = session;
        _activeByWorker[worker.Id] = session.Id;

        var pending = ToDto(session);
        await workerPushNotifier
            .TryPushLocalChromeLoginSessionAsync(worker.Id, pending, ct)
            .ConfigureAwait(false);
        await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);

        return (pending, null);
    }

    public WorkerPendingLocalChromeLoginDto? GetPendingForWorker(Guid workerId)
    {
        RemoveExpired();
        if (!_activeByWorker.TryGetValue(workerId, out var sessionId)
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.Completed
            || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return ToDto(session);
    }

    public bool CompleteFromWorker(Guid workerId, Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)
            || session.WorkerId != workerId)
        {
            return false;
        }

        session.Completed = true;
        if (_activeByWorker.TryGetValue(workerId, out var current) && current == sessionId)
        {
            _activeByWorker.TryRemove(workerId, out _);
        }

        _sessions.TryRemove(sessionId, out _);
        return true;
    }

    private static bool IsAccountMonitoringNow(WorkerEntity worker, Guid accountId)
    {
        if (string.IsNullOrWhiteSpace(worker.ActivityActiveAccountsJson))
        {
            return false;
        }

        try
        {
            var active = JsonSerializer.Deserialize<List<WorkerActiveAccountDto>>(
                worker.ActivityActiveAccountsJson,
                ActivityJson);
            if (active is null)
            {
                return false;
            }

            return active.Any(item =>
                item.AccountId == accountId
                && (string.Equals(item.Phase, WorkerActivityPhases.Account, StringComparison.Ordinal)
                    || string.Equals(item.Phase, WorkerActivityPhases.SubProfile, StringComparison.Ordinal)
                    || string.Equals(item.Phase, WorkerActivityPhases.Parallel, StringComparison.Ordinal)));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExpiresAtUtc <= now || pair.Value.Completed)
            {
                _sessions.TryRemove(pair.Key, out _);
                if (_activeByWorker.TryGetValue(pair.Value.WorkerId, out var current)
                    && current == pair.Key)
                {
                    _activeByWorker.TryRemove(pair.Value.WorkerId, out _);
                }
            }
        }
    }

    private static WorkerPendingLocalChromeLoginDto ToDto(LoginSession session) =>
        new(session.Id, session.WorkerId, session.AccountId);

    private sealed class LoginSession
    {
        public Guid Id { get; init; }
        public Guid WorkerId { get; init; }
        public Guid AccountId { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime ExpiresAtUtc { get; init; }
        public bool Completed { get; set; }
    }
}
