using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Жизненный цикл сессии ручного пополнения баланса аккаунта воркера.
/// Фаза 1: только контракты и persisted-состояние. Автоматизация воркера (браузер,
/// QR, оплата) реализуется в отдельной фазе; здесь воркер лишь опрашивает pending-снимок
/// и сообщает статусы Started/QrReady/Expired/Failed/Cancelled.
/// </summary>
public sealed class TopUpSessionService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IPanelRealtimeNotifier panelRealtime,
    IWorkerPushNotifier workerPushNotifier,
    WorkerConnectionRegistry connectionRegistry,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);
    // Keep this as data rather than calling IsActive inside EF expressions: EF Core cannot
    // translate arbitrary CLR methods and worker config polling must never return HTTP 500.
    private static readonly string[] ActiveStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started,
        TopUpSessionStatuses.PaymentClaimed,
        TopUpSessionStatuses.QrReady
    ];

    /// <summary>Срок хранения QR-данных после завершения сессии, после которого они удаляются.</summary>
    private static readonly TimeSpan QrRetention = TimeSpan.FromHours(6);

    public async Task<(TopUpSessionDto? Session, TopUpSessionConflictDto? Conflict)> CreateAsync(
        Guid workerId,
        Guid accountId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return (null, new TopUpSessionConflictDto("Нет доступа."));
        }

        var operatorUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var operatorDisplayName = principal.Identity?.Name
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? "Оператор";

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var worker = await db.Workers
            .Include(x => x.Accounts)
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null || !await officeScope.CanAccessWorkerAsync(scope, worker.Id, ct).ConfigureAwait(false))
        {
            return (null, new TopUpSessionConflictDto("Воркер не найден."));
        }

        var account = worker.Accounts.FirstOrDefault(x => x.AccountId == accountId);
        if (account is null)
        {
            return (null, new TopUpSessionConflictDto("Аккаунт не найден на воркере."));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Воркер должен быть онлайн: иначе воркер не сможет подхватить сессию и сформировать QR.
        if (!WorkerOnlineRules.IsOnline(worker.LastSeenAtUtc, now, connectionRegistry.IsConnected(worker.Id)))
        {
            return (null, new TopUpSessionConflictDto("Воркер оффлайн — пополнение недоступно."));
        }

        // Идемпотентность: не создаём вторую активную сессию для того же аккаунта
        // (глобально по AccountId, независимо от воркера).
        // Если активная сессия уже истекла по времени, но ещё не подметена — сначала
        // транзакционно завершаем её (expired + release pause), затем создаём замену.
        var active = await db.TopUpSessions
            .FirstOrDefaultAsync(x =>
                    x.AccountId == accountId
                    && ActiveStatuses.Contains(x.Status),
                ct)
            .ConfigureAwait(false);
        if (active is not null)
        {
            if (active.ExpiresAtUtc > now)
            {
                return (null, new TopUpSessionConflictDto(
                    "Для этого аккаунта уже есть активная сессия пополнения.",
                    active.Id,
                    active.AccountName));
            }

            // Истекла, но не подметена: завершаем перед созданием замены.
            await ExpireSessionAsync(active, now, ct).ConfigureAwait(false);
        }

        // Валидация текущего баланса и суммы на момент запроса.
        // Блокируем строку аккаунта (FOR UPDATE), чтобы баланс не мог устареть между чтением
        // и созданием сессии: конкурентная синхронизация баланса воркером не создаст
        // недопустимую сессию по устаревшему снимку. В InMemory/SQLite (тесты) блокировка
        // недоступна — читаем обычным запросом.
        var currentBalance = db.Database.IsNpgsql()
            ? await ReadBalanceWithLockAsync(workerId, accountId, ct).ConfigureAwait(false)
            : account.TotalBalance;
        if (!TopUpSessionRules.IsEligible(currentBalance))
        {
            return (null, new TopUpSessionConflictDto(
                $"Баланс аккаунта ({currentBalance:0.##} ₽) не ниже порога пополнения."));
        }

        var dailyResponses = await CountDailyResponsesAsync(workerId, accountId, now, ct).ConfigureAwait(false);
        var targetBalance = TopUpSessionRules.ResolveTargetBalance(dailyResponses);
        var requestedAmount = TopUpSessionRules.ResolveRequestedAmount(currentBalance, dailyResponses);
        if (requestedAmount <= 0m)
        {
            return (null, new TopUpSessionConflictDto("Сумма пополнения не требуется."));
        }

        // Пауза мониторинга: приобретаем «аренду» на уровне воркера, только если воркер
        // ещё не на паузе. Если воркер уже на паузе (ручной или чужой арендой) — аренду не берём.
        var session = new TopUpSessionEntity
        {
            Id = Guid.NewGuid(),
            WorkerId = worker.Id,
            AccountId = account.AccountId,
            AccountName = account.DisplayName,
            OfficeId = worker.OfficeId,
            OperatorUserId = operatorUserId,
            OperatorDisplayName = operatorDisplayName,
            Status = TopUpSessionStatuses.Requested,
            CurrentBalance = currentBalance,
            TargetBalance = targetBalance,
            RequestedAmount = requestedAmount,
            DailyResponseCount = dailyResponses,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl)
        };

        if (!worker.IsMonitoringPaused)
        {
            worker.TopUpPauseLeaseVersion++;
            worker.TopUpPauseLeaseId = session.Id;
            worker.TopUpPauseBaselinePaused = false;
            worker.IsMonitoringPaused = true;
            session.OwnsPauseLease = true;
        }

        // Фиксируем снимок версии аренды паузы после (возможного) приобретения аренды.
        // Это «ожидаемая» версия: любое ручное изменение паузы после создания сессии
        // инкрементирует версию воркера и сделает claim оплаты недействительным.
        session.ExpectedPauseLeaseVersion = worker.TopUpPauseLeaseVersion;

        db.TopUpSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Конкурентный старт: другой запрос уже создал активную сессию для аккаунта.
            db.ChangeTracker.Clear();
            var winner = await db.TopUpSessions.AsNoTracking()
                .FirstOrDefaultAsync(x =>
                        x.AccountId == accountId
                        && ActiveStatuses.Contains(x.Status),
                    ct)
                .ConfigureAwait(false);
            return (null, new TopUpSessionConflictDto(
                "Для этого аккаунта уже есть активная сессия пополнения.",
                winner?.Id,
                winner?.AccountName));
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts],
            worker.OfficeId,
            worker.Id);
        await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);

        var pending = await GetPendingForWorkerAsync(worker.Id, ct).ConfigureAwait(false);
        if (pending is not null)
        {
            await workerPushNotifier.TryPushTopUpSessionAsync(worker.Id, pending, ct).ConfigureAwait(false);
        }

        return (ToDto(session, worker.DisplayName), null);
    }

    public async Task<TopUpSessionDto?> GetAsync(Guid sessionId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return null;
        }

        var session = await db.TopUpSessions.AsNoTracking()
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session is null || !await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return null;
        }

        return ToDto(session, session.Worker.DisplayName);
    }

    public async Task<(bool Success, string? Error)> CancelAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return (false, "Нет доступа.");
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Блокируем строку сессии, чтобы отмена и заявление оплаты сериализовались атомарно.
        var session = await LockSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return (false, "Сессия не найдена.");
        }

        if (!principal.IsInRole(PanelRoles.Admin)
            && !string.Equals(session.OperatorUserId, userId, StringComparison.Ordinal))
        {
            return (false, "Отменить может только оператор, начавший сессию.");
        }

        if (!TopUpSessionStatuses.IsActive(session.Status))
        {
            return (false, "Сессия уже завершена.");
        }

        // Если воркер уже заявил право на оплату (payment_claimed) — отмена проигрывает гонку:
        // возвращаем конфликт, не помечая сессию отменённой.
        if (session.Status == TopUpSessionStatuses.PaymentClaimed)
        {
            return (false, "Оплата уже инициирована — отмена невозможна.");
        }

        session.Status = TopUpSessionStatuses.Cancelled;
        session.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        ClearQrData(session);
        await ReleasePauseAsync(session, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts],
            session.OfficeId,
            session.WorkerId);
        return (true, null);
    }

    private async Task<TopUpSessionEntity?> LockSessionByIdAsync(Guid sessionId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.TopUpSessions
                .FromSqlInterpolated($"SELECT * FROM \"TopUpSessions\" WHERE \"Id\" = {sessionId} FOR UPDATE")
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.TopUpSessions.FirstOrDefaultAsync(x => x.Id == sessionId, ct).ConfigureAwait(false);
    }

    public async Task<(bool Success, string? Error)> UpdateStatusFromWorkerAsync(
        Guid workerId,
        UpdateTopUpSessionStatusRequest request,
        CancellationToken ct = default)
    {
        var session = await db.TopUpSessions
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == request.SessionId && x.WorkerId == workerId, ct)
            .ConfigureAwait(false);
        if (session is null)
        {
            return (false, "Сессия не найдена.");
        }

        // Аккаунт сессии всё ещё должен принадлежать воркеру.
        var accountStillOwned = await db.WorkerAccounts
            .AnyAsync(x => x.WorkerId == workerId && x.AccountId == session.AccountId, ct)
            .ConfigureAwait(false);
        if (!accountStillOwned)
        {
            return (false, "Аккаунт сессии больше не принадлежит воркеру.");
        }

        if (!TopUpSessionStatuses.IsActive(session.Status))
        {
            return string.Equals(session.Status, request.Status, StringComparison.OrdinalIgnoreCase)
                ? (true, null)
                : (false, $"Сессия уже завершена статусом '{session.Status}'.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (session.ExpiresAtUtc <= now)
        {
            await ExpireSessionAsync(session, now, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                session.OfficeId,
                session.WorkerId);
            return (false, "Сессия истекла.");
        }

        // Только вперёд: запрещаем обратные переходы и недопустимые статусы.
        if (!TopUpSessionStatuses.CanTransition(session.Status, request.Status))
        {
            return (false, $"Недопустимый переход статуса '{session.Status}' → '{request.Status}'.");
        }

        session.Status = request.Status;
        if (request.Status == TopUpSessionStatuses.Started)
        {
            session.StartedAtUtc = now;
        }
        else if (request.Status == TopUpSessionStatuses.PaymentClaimed)
        {
            session.PaymentClaimedAtUtc = now;
        }
        else if (request.Status == TopUpSessionStatuses.QrReady)
        {
            // Валидируем QR-данные до сохранения: только корректный base64 PNG или HTTPS Avito URL.
            var (qrValid, qrError) = TopUpSessionQrValidator.Validate(request.QrImageBase64, request.QrImageUrl);
            if (!qrValid)
            {
                return (false, qrError ?? "Некорректные QR-данные.");
            }

            session.QrReadyAtUtc = now;
            session.QrImageBase64 = request.QrImageBase64;
            session.QrImageUrl = request.QrImageUrl;
        }

        if (!TopUpSessionStatuses.IsActive(request.Status))
        {
            session.CompletedAtUtc = now;
            session.FailureMessage = request.FailureMessage;
            ClearQrData(session);
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!TopUpSessionStatuses.IsActive(request.Status))
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                session.OfficeId,
                session.WorkerId);
        }

        return (true, null);
    }

    /// <summary>
    /// Атомарное заявление воркером права на клик по оплате (линеаризационный барьер).
    /// Переводит сессию из допустимого предоплатного состояния (started) в payment_claimed
    /// под блокировкой строки сессии. Если сессия уже отменена/истекла/завершена — отказ.
    /// </summary>
    public async Task<ClaimTopUpPaymentResult> ClaimPaymentAsync(
        Guid workerId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var session = await LockSessionAsync(sessionId, workerId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return new ClaimTopUpPaymentResult(false, "Сессия не найдена.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (!TopUpSessionStatuses.IsActive(session.Status))
        {
            return new ClaimTopUpPaymentResult(
                false,
                $"Сессия уже завершена статусом '{session.Status}'.");
        }

        if (session.ExpiresAtUtc <= now)
        {
            await ExpireSessionAsync(session, now, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new ClaimTopUpPaymentResult(false, "Сессия истекла.");
        }

        // Заявлять право на оплату можно только из started (после выбора СБП, до клика).
        // Повторный claim уже заявленной сессии идемпотентен.
        if (session.Status == TopUpSessionStatuses.PaymentClaimed)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return new ClaimTopUpPaymentResult(true);
        }

        if (session.Status != TopUpSessionStatuses.Started)
        {
            return new ClaimTopUpPaymentResult(
                false,
                $"Недопустимое состояние для оплаты: '{session.Status}'.");
        }

        // Линеаризационный барьер: блокируем и читаем воркера, чтобы проверить, что пауза
        // не была изменена вручную после создания сессии. Если версия аренды изменилась —
        // оплата отклоняется (оператор вмешался в паузу после старта сессии).
        var worker = await LockWorkerAsync(session.WorkerId, ct).ConfigureAwait(false);
        if (worker is null)
        {
            return new ClaimTopUpPaymentResult(false, "Воркер не найден.");
        }

        if (worker.TopUpPauseLeaseVersion != session.ExpectedPauseLeaseVersion)
        {
            return new ClaimTopUpPaymentResult(
                false,
                "Пауза воркера была изменена вручную — оплата недоступна.");
        }

        if (session.OwnsPauseLease && worker.TopUpPauseLeaseId != session.Id)
        {
            return new ClaimTopUpPaymentResult(
                false,
                "Аренда паузы утрачена — оплата недоступна.");
        }

        session.Status = TopUpSessionStatuses.PaymentClaimed;
        session.PaymentClaimedAtUtc = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new ClaimTopUpPaymentResult(true);
    }

    private async Task<TopUpSessionEntity?> LockSessionAsync(
        Guid sessionId,
        Guid workerId,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.TopUpSessions
                .FromSqlInterpolated(
                    $"SELECT * FROM \"TopUpSessions\" WHERE \"Id\" = {sessionId} AND \"WorkerId\" = {workerId} FOR UPDATE")
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.TopUpSessions
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.WorkerId == workerId, ct)
            .ConfigureAwait(false);
    }

    public async Task<WorkerPendingTopUpSessionDto?> GetPendingForWorkerAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var session = await db.TopUpSessions.AsNoTracking()
            .Where(x => x.WorkerId == workerId && ActiveStatuses.Contains(x.Status))
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (session is null || session.ExpiresAtUtc <= now)
        {
            return null;
        }

        return new WorkerPendingTopUpSessionDto(
            session.Id,
            session.WorkerId,
            session.AccountId,
            session.AccountName,
            session.TargetBalance,
            session.RequestedAmount,
            session.CurrentBalance,
            session.DailyResponseCount);
    }

    public async Task<TopUpSessionDto?> GetForWorkerAsync(
        Guid workerId,
        Guid sessionId,
        CancellationToken ct = default)
    {
        var session = await db.TopUpSessions.AsNoTracking()
            .Include(x => x.Worker)
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.WorkerId == workerId, ct)
            .ConfigureAwait(false);
        return session is null ? null : ToDto(session, session.Worker.DisplayName);
    }

    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await db.TopUpSessions
            .Include(x => x.Worker)
            .Where(x => ActiveStatuses.Contains(x.Status) && x.ExpiresAtUtc <= now)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var session in expired)
        {
            await ExpireSessionAsync(session, now, ct).ConfigureAwait(false);
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var session in expired)
            {
                panelRealtime.Notify(
                    [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                    session.OfficeId,
                    session.WorkerId);
            }
        }

        return expired.Count;
    }

    /// <summary>
    /// Удаляет QR-данные (base64/URL) у завершённых сессий, у которых они хранятся дольше
    /// <see cref="QrRetention"/>. Вызывается фоновым sweeper'ом.
    /// </summary>
    public async Task<int> PurgeQrDataAsync(CancellationToken ct = default)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - QrRetention;
        var stale = await db.TopUpSessions
            .Where(x => !ActiveStatuses.Contains(x.Status)
                        && (x.QrImageBase64 != null || x.QrImageUrl != null)
                        && x.CompletedAtUtc != null
                        && x.CompletedAtUtc <= cutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var session in stale)
        {
            ClearQrData(session);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return stale.Count;
    }

    private async Task ExpireSessionAsync(TopUpSessionEntity session, DateTime now, CancellationToken ct)
    {
        session.Status = TopUpSessionStatuses.Expired;
        session.CompletedAtUtc = now;
        session.FailureMessage = "Истекло время сессии.";
        ClearQrData(session);
        await ReleasePauseAsync(session, ct).ConfigureAwait(false);
    }

    private static void ClearQrData(TopUpSessionEntity session)
    {
        session.QrImageBase64 = null;
        session.QrImageUrl = null;
    }

    private async Task<decimal> ReadBalanceWithLockAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        var locked = await db.WorkerAccounts
            .FromSqlInterpolated(
                $"SELECT * FROM \"WorkerAccounts\" WHERE \"WorkerId\" = {workerId} AND \"AccountId\" = {accountId} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return locked?.TotalBalance ?? 0m;
    }

    private async Task<int> CountDailyResponsesAsync(
        Guid workerId,
        Guid accountId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var (start, end) = TopUpSessionRules.GetMoscowDayRange(nowUtc);
        return await db.CandidateResponses.AsNoTracking()
            .CountAsync(x =>
                    x.WorkerId == workerId
                    && x.AccountId == accountId
                    && x.CollectedAt >= start
                    && x.CollectedAt < end,
                ct)
            .ConfigureAwait(false);
    }

    private async Task ReleasePauseAsync(TopUpSessionEntity session, CancellationToken ct)
    {
        // Блокируем строку воркера, чтобы конкурентные терминализации сериализовались.
        var worker = await LockWorkerAsync(session.WorkerId, ct).ConfigureAwait(false);
        if (worker is null || worker.TopUpPauseLeaseId is null)
        {
            // Аренды нет (ручная пауза или уже освобождена) — ничего не делаем.
            return;
        }

        // Если есть другие активные сессии этого воркера — аренда остаётся (пауза сохраняется).
        var hasOtherActiveSession = await db.TopUpSessions
            .AnyAsync(x =>
                    x.WorkerId == session.WorkerId
                    && x.Id != session.Id
                    && ActiveStatuses.Contains(x.Status),
                ct)
            .ConfigureAwait(false);
        if (hasOtherActiveSession)
        {
            return;
        }

        // Последняя активная сессия завершилась: освобождаем аренду и восстанавливаем базовое
        // состояние паузы. Аренда могла быть инвалидирована ручной сменой паузы — тогда
        // TopUpPauseLeaseId уже null, и мы сюда не попали.
        worker.IsMonitoringPaused = worker.TopUpPauseBaselinePaused;
        worker.TopUpPauseLeaseId = null;
        worker.TopUpPauseLeaseVersion++;
    }

    private async Task<WorkerEntity?> LockWorkerAsync(Guid workerId, CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.Workers
                .FromSqlInterpolated($"SELECT * FROM \"Workers\" WHERE \"Id\" = {workerId} FOR UPDATE")
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.Workers.FirstOrDefaultAsync(x => x.Id == workerId, ct).ConfigureAwait(false);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }

    private static TopUpSessionDto ToDto(TopUpSessionEntity session, string workerName) =>
        new(
            session.Id,
            session.WorkerId,
            workerName,
            session.AccountId,
            session.AccountName,
            session.OfficeId,
            session.OperatorUserId,
            session.OperatorDisplayName,
            session.Status,
            session.CurrentBalance,
            session.TargetBalance,
            session.RequestedAmount,
            session.DailyResponseCount,
            session.CreatedAtUtc,
            session.ExpiresAtUtc,
            session.CompletedAtUtc,
            session.StartedAtUtc,
            session.PaymentClaimedAtUtc,
            session.QrReadyAtUtc,
            session.QrImageBase64,
            session.QrImageUrl,
            session.FailureMessage);
}
