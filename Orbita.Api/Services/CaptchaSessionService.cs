using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed class CaptchaSessionService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    ICaptchaLockNotifier captchaLockNotifier,
    IPanelRealtimeNotifier panelRealtime,
    IWorkerPushNotifier workerPushNotifier,
    ICaptchaSessionRelayNotifier relayNotifier)
{
    private const string DefaultAdsPowerApiBaseUrl = "http://local.adspower.net:50325";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    public async Task<(CaptchaSessionDto? Session, CaptchaSessionConflictDto? Conflict)> CreateAsync(
        CreateCaptchaSessionRequest request,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return (null, new CaptchaSessionConflictDto("Нет доступа."));
        }

        if (string.IsNullOrWhiteSpace(request.PageUrl))
        {
            return (null, new CaptchaSessionConflictDto("URL страницы капчи не указан."));
        }

        var operatorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var operatorDisplayName = principal.Identity?.Name
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? "Оператор";

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var worker = await db.Workers
            .Include(x => x.Accounts)
            .FirstOrDefaultAsync(x => x.Id == request.WorkerId, ct)
            .ConfigureAwait(false);
        if (worker is null || !await officeScope.CanAccessWorkerAsync(scope, worker.Id, ct).ConfigureAwait(false))
        {
            return (null, new CaptchaSessionConflictDto("Воркер не найден."));
        }

        var account = worker.Accounts.FirstOrDefault(x => x.AccountId == request.AccountId);
        if (account is null)
        {
            return (null, new CaptchaSessionConflictDto("Аккаунт не найден на воркере."));
        }

        if (worker.ActiveCaptchaSessionId is Guid activeId)
        {
            var active = await db.CaptchaSessions.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == activeId, ct)
                .ConfigureAwait(false);
            if (active is not null && CaptchaSessionStatuses.IsActive(active.Status) && active.ExpiresAtUtc > DateTime.UtcNow)
            {
                return (null, new CaptchaSessionConflictDto(
                    "Воркер занят другой сессией решения капчи.",
                    active.Id,
                    active.OperatorDisplayName,
                    active.AccountName));
            }

            await ReleaseWorkerLockAsync(worker, ct).ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;
        var session = new CaptchaSessionEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker.Id,
            AccountId = account.AccountId,
            AccountName = account.DisplayName,
            OfficeId = worker.OfficeId,
            OperatorUserId = operatorUserId,
            OperatorDisplayName = operatorDisplayName,
            PageUrl = request.PageUrl.Trim(),
            CaptchaKind = string.IsNullOrWhiteSpace(request.CaptchaKind) ? "captcha" : request.CaptchaKind.Trim(),
            SubProfileId = string.IsNullOrWhiteSpace(request.SubProfileId) ? null : request.SubProfileId.Trim(),
            Status = CaptchaSessionStatuses.Pending,
            ViewportWidth = CaptchaViewportDefaults.Width,
            ViewportHeight = CaptchaViewportDefaults.Height,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl)
        };

        worker.ActiveCaptchaSessionId = session.Id;
        worker.ActiveCaptchaOperatorUserId = operatorUserId;
        worker.ActiveCaptchaOperatorDisplayName = operatorDisplayName;
        worker.ActiveCaptchaSessionStartedAtUtc = now;

        db.CaptchaSessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var dto = ToDto(session, worker.DisplayName);
        await NotifyLockAsync(worker, ct).ConfigureAwait(false);
        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Errors],
            worker.OfficeId,
            worker.Id);

        var pending = await GetPendingForWorkerAsync(worker.Id, ct).ConfigureAwait(false);
        if (pending is not null)
        {
            await workerPushNotifier.TryPushCaptchaSessionAsync(worker.Id, pending, ct).ConfigureAwait(false);
        }

        return (dto, null);
    }

    public async Task<CaptchaSessionDto?> GetAsync(Guid sessionId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return null;
        }

        var session = await db.CaptchaSessions.AsNoTracking()
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || !await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return null;
        }

        return ToDto(session, session.Worker.DisplayName);
    }

    public async Task<WorkerCaptchaLockDto?> GetWorkerLockAsync(
        Guid workerId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess || !await officeScope.CanAccessWorkerAsync(scope, workerId, ct).ConfigureAwait(false))
        {
            return null;
        }

        var worker = await db.Workers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workerId, ct).ConfigureAwait(false);
        return worker is null ? null : await BuildLockDtoAsync(worker, ct).ConfigureAwait(false);
    }

    public async Task<WorkerPendingCaptchaSessionDto?> GetPendingForWorkerAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        var worker = await db.Workers.AsNoTracking()
            .Include(x => x.Accounts)
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker?.ActiveCaptchaSessionId is not Guid sessionId)
        {
            return null;
        }

        var session = await db.CaptchaSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (session.Status == CaptchaSessionStatuses.Pending)
        {
            session.Status = CaptchaSessionStatuses.Opening;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else if (session.Status != CaptchaSessionStatuses.Opening)
        {
            return null;
        }

        var account = worker.Accounts.FirstOrDefault(x => x.AccountId == session.AccountId);
        if (account is null || string.IsNullOrWhiteSpace(account.AdsPowerProfileId))
        {
            return null;
        }

        return new WorkerPendingCaptchaSessionDto(
            session.Id,
            session.AccountId,
            account.AdsPowerProfileId,
            ResolveAdsPowerApiBaseUrl(worker.AdsPowerApiBaseUrl),
            worker.AdsPowerApiKey,
            session.PageUrl,
            session.CaptchaKind,
            session.SubProfileId,
            session.ViewportWidth,
            session.ViewportHeight);
    }

    public async Task<(bool Success, string? Error)> CancelAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        var session = await db.CaptchaSessions
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || !await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return (false, "Сессия не найдена.");
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!principal.IsInRole(PanelRoles.Admin)
            && !string.Equals(session.OperatorUserId, userId, StringComparison.Ordinal))
        {
            return (false, "Отменить может только оператор, начавший сессию.");
        }

        if (!CaptchaSessionStatuses.IsActive(session.Status))
        {
            return (false, "Сессия уже завершена.");
        }

        session.Status = CaptchaSessionStatuses.Cancelled;
        session.CompletedAtUtc = DateTime.UtcNow;
        await ReleaseWorkerLockAsync(session.Worker, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await NotifyLockAsync(session.Worker, ct).ConfigureAwait(false);
        await NotifyTerminalStateAsync(
            session.Id,
            CaptchaSessionStatuses.Cancelled,
            "Сессия отменена оператором.",
            ct).ConfigureAwait(false);
        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts],
            session.OfficeId,
            session.WorkerId);
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateStatusFromWorkerAsync(
        Guid workerId,
        UpdateCaptchaSessionStatusRequest request,
        CancellationToken ct = default)
    {
        var session = await db.CaptchaSessions
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == request.SessionId && x.WorkerId == workerId, ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            return (false, "Сессия не найдена.");
        }

        if (!CaptchaSessionStatuses.IsActive(session.Status))
        {
            return string.Equals(session.Status, request.Status, StringComparison.OrdinalIgnoreCase)
                ? (true, null)
                : (false, $"Сессия уже завершена статусом '{session.Status}'.");
        }

        if (session.ExpiresAtUtc <= DateTime.UtcNow && CaptchaSessionStatuses.IsActive(session.Status))
        {
            session.Status = CaptchaSessionStatuses.Expired;
            session.CompletedAtUtc = DateTime.UtcNow;
            session.FailureMessage = "Истекло время сессии.";
            await ReleaseWorkerLockAsync(session.Worker, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await NotifyLockAsync(session.Worker, ct).ConfigureAwait(false);
            await NotifyTerminalStateAsync(
                session.Id,
                CaptchaSessionStatuses.Expired,
                session.FailureMessage,
                ct).ConfigureAwait(false);
            return (false, "Сессия истекла.");
        }

        session.Status = request.Status;
        if (!CaptchaSessionStatuses.IsActive(request.Status))
        {
            session.CompletedAtUtc = DateTime.UtcNow;
            session.FailureMessage = request.FailureMessage;
            await ReleaseWorkerLockAsync(session.Worker, ct).ConfigureAwait(false);

            if (request.CaptchaCleared == true)
            {
                var account = await db.WorkerAccounts
                    .FirstOrDefaultAsync(x => x.WorkerId == workerId && x.AccountId == session.AccountId, ct)
                    .ConfigureAwait(false);
                if (account is not null)
                {
                    account.Status = "Authorized";
                    account.LastErrorMessage = null;
                    account.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (!CaptchaSessionStatuses.IsActive(request.Status))
        {
            await NotifyLockAsync(session.Worker, ct).ConfigureAwait(false);
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts, PanelChangeKind.Errors],
                session.OfficeId,
                session.WorkerId);
        }

        return (true, null);
    }

    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expired = await db.CaptchaSessions
            .Include(x => x.Worker)
            .Where(x => CaptchaSessionStatuses.IsActive(x.Status) && x.ExpiresAtUtc <= now)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var session in expired)
        {
            session.Status = CaptchaSessionStatuses.Expired;
            session.CompletedAtUtc = now;
            session.FailureMessage = "Истекло время сессии.";
            await ReleaseWorkerLockAsync(session.Worker, ct).ConfigureAwait(false);
            await NotifyLockAsync(session.Worker, ct).ConfigureAwait(false);
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var session in expired)
            {
                await NotifyTerminalStateAsync(
                    session.Id,
                    CaptchaSessionStatuses.Expired,
                    session.FailureMessage,
                    ct).ConfigureAwait(false);
            }
        }

        return expired.Count;
    }

    public async Task<CaptchaSessionDto?> GetForWorkerAsync(
        Guid workerId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        var session = await db.CaptchaSessions.AsNoTracking()
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.WorkerId == workerId, ct)
            .ConfigureAwait(false);
        return session is null ? null : ToDto(session, session.Worker.DisplayName);
    }

    public async Task<bool> IsOperatorForSessionAsync(
        Guid sessionId,
        string? userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (isAdmin)
        {
            return true;
        }

        var session = await db.CaptchaSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        return session is not null
            && !string.IsNullOrWhiteSpace(userId)
            && string.Equals(session.OperatorUserId, userId, StringComparison.Ordinal);
    }

    private async Task NotifyLockAsync(WorkerEntity worker, CancellationToken ct)
    {
        var lockDto = await BuildLockDtoAsync(worker, ct).ConfigureAwait(false);
        await captchaLockNotifier.NotifyLockChangedAsync(worker.Id, worker.OfficeId, lockDto, ct).ConfigureAwait(false);
    }

    private Task NotifyTerminalStateAsync(
        Guid sessionId,
        string status,
        string? message,
        CancellationToken ct) =>
        relayNotifier.NotifyStateChangedAsync(new CaptchaStateChangedMessage(sessionId, status, message), ct);

    private async Task<WorkerCaptchaLockDto> BuildLockDtoAsync(WorkerEntity worker, CancellationToken ct)
    {
        if (worker.ActiveCaptchaSessionId is not Guid sessionId)
        {
            return new WorkerCaptchaLockDto(false, null, null, null, null, null, null);
        }

        var session = await db.CaptchaSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || !CaptchaSessionStatuses.IsActive(session.Status) || session.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return new WorkerCaptchaLockDto(false, null, null, null, null, null, null);
        }

        return new WorkerCaptchaLockDto(
            true,
            session.Id,
            session.AccountId,
            session.AccountName,
            session.OperatorUserId,
            session.OperatorDisplayName,
            worker.ActiveCaptchaSessionStartedAtUtc);
    }

    private static Task ReleaseWorkerLockAsync(WorkerEntity worker, CancellationToken ct)
    {
        worker.ActiveCaptchaSessionId = null;
        worker.ActiveCaptchaOperatorUserId = null;
        worker.ActiveCaptchaOperatorDisplayName = null;
        worker.ActiveCaptchaSessionStartedAtUtc = null;
        return Task.CompletedTask;
    }

    private static string ResolveAdsPowerApiBaseUrl(string? configuredUrl) =>
        string.IsNullOrWhiteSpace(configuredUrl) ? DefaultAdsPowerApiBaseUrl : configuredUrl.Trim().TrimEnd('/');

    private static CaptchaSessionDto ToDto(CaptchaSessionEntity session, string workerName) =>
        new(
            session.Id,
            session.AccountId,
            session.AccountName,
            session.WorkerId,
            workerName,
            session.OfficeId,
            session.OperatorUserId,
            session.OperatorDisplayName,
            session.PageUrl,
            session.CaptchaKind,
            session.SubProfileId,
            session.Status,
            session.ViewportWidth,
            session.ViewportHeight,
            session.CreatedAtUtc,
            session.ExpiresAtUtc,
            session.CompletedAtUtc,
            session.FailureMessage);
}
