using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Жизненный цикл сессии ручного пополнения баланса аккаунта воркера.
/// Фаза 1: только контракты и persisted-состояние. Автоматизация воркера (браузер,
/// QR, оплата) реализуется в отдельной фазе; здесь воркер лишь опрашивает pending-снимок
/// и сообщает статусы Started/QrReady/Expired/Failed/Cancelled/Paid.
/// </summary>
public sealed class TopUpSessionService(
    OrbitaDbContext db,
    OfficeScopeService officeScope,
    IPanelRealtimeNotifier panelRealtime,
    IWorkerPushNotifier workerPushNotifier,
    WorkerConnectionRegistry connectionRegistry,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan SessionTtl = TopUpSessionRules.PauseLeaseTtl;
    private static readonly TimeSpan BalanceSpendLookback = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Keep this as data rather than calling IsActive inside EF expressions: EF Core cannot
    // translate arbitrary CLR methods and worker config polling must never return HTTP 500.
    private static readonly string[] ActiveStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started,
        TopUpSessionStatuses.PaymentClaimed,
        TopUpSessionStatuses.QrReady
    ];
    private static readonly string[] ConflictStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started,
        TopUpSessionStatuses.PaymentClaimed,
        TopUpSessionStatuses.QrReady,
        TopUpSessionStatuses.AwaitingBalance
    ];
    private static readonly TimeSpan BalanceConfirmationTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Срок хранения QR-данных после завершения сессии, после которого они удаляются.</summary>
    private static readonly TimeSpan QrRetention = TimeSpan.FromHours(6);

    public async Task<(TopUpSessionDto? Session, TopUpSessionConflictDto? Conflict)> CreateAsync(
        Guid workerId,
        Guid accountId,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
        => await CreateAsync(workerId, accountId, principal, subProfileId: null, ct).ConfigureAwait(false);

    public async Task<(TopUpSessionDto? Session, TopUpSessionConflictDto? Conflict)> CreateAsync(
        Guid workerId,
        Guid accountId,
        ClaimsPrincipal principal,
        string? subProfileId,
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
            .FirstOrDefaultAsync(x => x.Id == workerId, ct)
            .ConfigureAwait(false);
        if (worker is null || !await officeScope.CanAccessWorkerAsync(scope, worker.Id, ct).ConfigureAwait(false))
        {
            return (null, new TopUpSessionConflictDto("Воркер не найден."));
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
                    && x.SubProfileId == (subProfileId ?? string.Empty)
                    && ConflictStatuses.Contains(x.Status),
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
        var lockedAccount = await ReadAccountWithLockAsync(workerId, accountId, ct).ConfigureAwait(false);
        if (lockedAccount is null)
        {
            return (null, new TopUpSessionConflictDto("Аккаунт не найден на воркере."));
        }

        // Panel requests always name a subprofile. The account fallback exists only for
        // existing non-panel callers while they migrate to the explicit operation.
        var subProfile = string.IsNullOrWhiteSpace(subProfileId)
            ? null
            : SubProfileDeserializer.Deserialize(lockedAccount.SubProfilesJson)
                ?.FirstOrDefault(x => string.Equals(x.Id, subProfileId, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(subProfileId)
            && (subProfile is null || string.IsNullOrWhiteSpace(subProfile.Id)))
        {
            return (null, new TopUpSessionConflictDto("Субпрофиль не найден у аккаунта."));
        }

        var currentBalance = subProfile?.Balance ?? lockedAccount.TotalBalance;
        if (!TopUpSessionRules.IsEligible(currentBalance))
        {
            return (null, new TopUpSessionConflictDto(
                $"Баланс аккаунта ({currentBalance:0.##} ₽) не ниже порога пополнения."));
        }

        var dailyResponses = await CountDailyResponsesAsync(workerId, accountId, subProfile?.Id, now, ct).ConfigureAwait(false);
        var spentLastHour = await GetBalanceSpentLastHourAsync(
                workerId,
                accountId,
                subProfile?.Id,
                subProfile?.Name,
                currentBalance,
                now,
                ct)
            .ConfigureAwait(false);
        var targetBalance = TopUpSessionRules.ResolveTargetBalance(dailyResponses, spentLastHour);
        var requestedAmount = TopUpSessionRules.ResolveRequestedAmount(currentBalance, dailyResponses, spentLastHour);
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
            AccountId = lockedAccount.AccountId,
            AccountName = lockedAccount.DisplayName,
            SubProfileId = subProfile?.Id ?? string.Empty,
            SubProfileName = subProfile?.Name ?? string.Empty,
            OfficeId = worker.OfficeId,
            OperatorUserId = operatorUserId,
            OperatorDisplayName = operatorDisplayName,
            Status = TopUpSessionStatuses.Requested,
            CurrentBalance = currentBalance,
            TargetBalance = targetBalance,
            RequestedAmount = requestedAmount,
            DailyResponseCount = dailyResponses,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(SessionTtl),
            ProgressMessage = "Ставим мониторинг на паузу и передаём задачу воркеру…"
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
                        && x.SubProfileId == (subProfileId ?? string.Empty)
                        && ConflictStatuses.Contains(x.Status),
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

    public async Task<IReadOnlyList<TopUpSessionDto>> GetOfficeAsync(
        ClaimsPrincipal principal,
        bool history,
        CancellationToken ct = default)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return [];
        }

        var query = db.TopUpSessions.AsNoTracking().Include(x => x.Worker).AsQueryable();
        if (!scope.IsGlobalAdmin && scope.OfficeId is Guid officeId)
        {
            query = query.Where(x => x.OfficeId == officeId);
        }

        if (!history)
        {
            query = query.Where(x =>
                ActiveStatuses.Contains(x.Status)
                || x.Status == TopUpSessionStatuses.AwaitingBalance
                || x.Status == TopUpSessionStatuses.VerificationRequired);
        }

        var sessions = await query
            .OrderBy(x => x.CreatedAtUtc)
            .Take(history ? 500 : 200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sessions.Select(x => ToDto(x, x.Worker.DisplayName)).ToArray();
    }

    public Task<(bool Success, string? Error)> CancelAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default) =>
        CompleteByOperatorAsync(
            sessionId,
            principal,
            TopUpSessionStatuses.Cancelled,
            session => TopUpSessionStatuses.IsActive(session.Status)
                ? null
                : "Сессия уже завершена.",
            ownerError: "Отменить может только оператор, начавший сессию.",
            ct);

    /// <summary>
    /// Оператор подтвердил оплату QR. Допустимо только из <see cref="TopUpSessionStatuses.QrReady"/>.
    /// Снимает аренду паузы мониторинга.
    /// </summary>
    public Task<(bool Success, string? Error)> MarkPaidAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default) =>
        CompleteByOperatorAsync(
            sessionId,
            principal,
            TopUpSessionStatuses.AwaitingBalance,
            session => session.Status == TopUpSessionStatuses.QrReady
                ? null
                : TopUpSessionStatuses.IsActive(session.Status)
                    ? "Отметить оплату можно, когда QR-код готов."
                    : "Сессия уже завершена.",
            ownerError: "Подтвердить оплату может только оператор, начавший сессию.",
            ct);

    private async Task<(bool Success, string? Error)> CompleteByOperatorAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        string terminalStatus,
        Func<TopUpSessionEntity, string?> validate,
        string ownerError,
        CancellationToken ct)
    {
        var scope = await officeScope.ResolveAsync(principal, ct).ConfigureAwait(false);
        if (!scope.HasAccess)
        {
            return (false, "Нет доступа.");
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var session = await LockSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || !await officeScope.CanAccessWorkerAsync(scope, session.WorkerId, ct).ConfigureAwait(false))
        {
            return (false, "Сессия не найдена.");
        }

        if (!principal.IsInRole(PanelRoles.Admin)
            && !string.Equals(session.OperatorUserId, userId, StringComparison.Ordinal))
        {
            return (false, ownerError);
        }

        if (string.Equals(session.Status, terminalStatus, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (true, null);
        }

        var validationError = validate(session);
        if (validationError is not null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (false, validationError);
        }

        session.Status = terminalStatus;
        var completedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (terminalStatus == TopUpSessionStatuses.AwaitingBalance)
        {
            session.AwaitingBalanceAtUtc = completedAt;
            session.CompletedAtUtc = null;
            session.ProgressMessage = "Оплата отмечена. Ожидаем новый баланс от воркера…";
        }
        else
        {
            session.CompletedAtUtc = completedAt;
        }
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

    private async Task<TopUpSessionEntity?> LockSessionByIdAsync(Guid sessionId, CancellationToken ct) =>
        await LockTopUpSessionRowAsync(sessionId, workerId: null, ct).ConfigureAwait(false);

    /// <summary>
    /// Берёт Postgres-блокировку отдельным <c>SELECT 1 … FOR UPDATE</c>, затем читает строку
    /// обычным LINQ. Нельзя материализовать сущность через <c>FromSql SELECT *</c>: системный
    /// <c>xmin</c> (RowVersion) в звёздочку не входит, EF делает UPDATE WHERE xmin = 0 → HTTP 500.
    /// </summary>
    private async Task<TopUpSessionEntity?> LockTopUpSessionRowAsync(
        Guid sessionId,
        Guid? workerId,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            if (workerId is Guid lockedWorkerId)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"TopUpSessions\" WHERE \"Id\" = {sessionId} AND \"WorkerId\" = {lockedWorkerId} FOR UPDATE",
                        ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT 1 FROM \"TopUpSessions\" WHERE \"Id\" = {sessionId} FOR UPDATE",
                        ct)
                    .ConfigureAwait(false);
            }
        }

        return workerId is Guid filterWorkerId
            ? await db.TopUpSessions
                .FirstOrDefaultAsync(x => x.Id == sessionId && x.WorkerId == filterWorkerId, ct)
                .ConfigureAwait(false)
            : await db.TopUpSessions
                .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
                .ConfigureAwait(false);
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

        if (request.Status is TopUpSessionStatuses.Paid or TopUpSessionStatuses.Cancelled)
        {
            return (false, "Этот статус выставляет только оператор.");
        }

        // Только вперёд: запрещаем обратные переходы и недопустимые статусы.
        if (!TopUpSessionStatuses.CanTransition(session.Status, request.Status))
        {
            return (false, $"Недопустимый переход статуса '{session.Status}' → '{request.Status}'.");
        }

        var statusChanged = !string.Equals(session.Status, request.Status, StringComparison.OrdinalIgnoreCase);
        var applyingQr = request.Status == TopUpSessionStatuses.QrReady
                         && (statusChanged || request.QrImageBase64 is not null || request.QrImageUrl is not null);
        if (applyingQr)
        {
            var (qrValid, qrError) = TopUpSessionQrValidator.Validate(request.QrImageBase64, request.QrImageUrl);
            if (!qrValid)
            {
                return (false, qrError ?? "Некорректные QR-данные.");
            }
        }

        if (statusChanged)
        {
            session.Status = request.Status;
            if (request.Status == TopUpSessionStatuses.Started)
            {
                session.StartedAtUtc ??= now;
            }
            else if (request.Status == TopUpSessionStatuses.PaymentClaimed)
            {
                session.PaymentClaimedAtUtc ??= now;
            }
            else if (request.Status == TopUpSessionStatuses.QrReady)
            {
                session.QrReadyAtUtc ??= now;
                session.QrImageBase64 = request.QrImageBase64;
                session.QrImageUrl = request.QrImageUrl;
            }
        }
        else if (applyingQr)
        {
            session.QrImageBase64 = request.QrImageBase64;
            session.QrImageUrl = request.QrImageUrl;
        }

        if (request.ProgressMessage is not null)
        {
            session.ProgressMessage = TopUpSessionRules.SanitizeProgressMessage(request.ProgressMessage);
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
        try
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ClaimTopUpPaymentResult(
                false,
                "Сессия была изменена параллельно. Попробуйте ещё раз.");
        }
        catch (Exception)
        {
            return new ClaimTopUpPaymentResult(
                false,
                "Не удалось подтвердить оплату. Попробуйте ещё раз.");
        }
    }

    private async Task<TopUpSessionEntity?> LockSessionAsync(
        Guid sessionId,
        Guid workerId,
        CancellationToken ct) =>
        await LockTopUpSessionRowAsync(sessionId, workerId, ct).ConfigureAwait(false);

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
            session.DailyResponseCount,
            session.SubProfileId,
            session.SubProfileName);
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

        var awaitingCutoff = now - BalanceConfirmationTimeout;
        var unconfirmed = await db.TopUpSessions
            .Where(x => x.Status == TopUpSessionStatuses.AwaitingBalance
                        && x.AwaitingBalanceAtUtc != null
                        && x.AwaitingBalanceAtUtc <= awaitingCutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var session in unconfirmed)
        {
            session.Status = TopUpSessionStatuses.VerificationRequired;
            session.CompletedAtUtc = now;
            session.ProgressMessage = "Баланс не обновился за 10 минут. Требуется ручная проверка.";
        }
        if (unconfirmed.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var session in unconfirmed)
            {
                panelRealtime.Notify([PanelChangeKind.Workers, PanelChangeKind.Accounts], session.OfficeId, session.WorkerId);
            }
        }

        return expired.Count + unconfirmed.Count;
    }

    public async Task<int> ConfirmBalancesAsync(
        Guid workerId,
        IReadOnlyList<WorkerBalanceDto> balances,
        DateTime capturedAtUtc,
        CancellationToken ct = default)
    {
        var awaiting = await db.TopUpSessions
            .Where(x => x.WorkerId == workerId && x.Status == TopUpSessionStatuses.AwaitingBalance)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (awaiting.Count == 0)
        {
            return 0;
        }

        var completed = 0;
        foreach (var session in awaiting)
        {
            var account = balances.FirstOrDefault(x => x.AccountId == session.AccountId);
            if (account is null)
            {
                continue;
            }

            decimal? balanceAfter = null;
            if (!string.IsNullOrWhiteSpace(session.SubProfileId) || !string.IsNullOrWhiteSpace(session.SubProfileName))
            {
                var profile = account.SubProfiles.FirstOrDefault(x =>
                    string.Equals(x.SubProfileName, session.SubProfileName, StringComparison.OrdinalIgnoreCase));
                balanceAfter = profile?.Balance;
            }
            else
            {
                balanceAfter = account.TotalBalance;
            }

            if (balanceAfter is not decimal actual || actual <= session.CurrentBalance)
            {
                continue;
            }

            session.Status = TopUpSessionStatuses.Completed;
            session.BalanceAfter = actual;
            session.BalanceConfirmedAtUtc = capturedAtUtc;
            session.CompletedAtUtc = capturedAtUtc;
            session.ProgressMessage = "Пополнение подтверждено новым снимком баланса.";
            completed++;
        }

        if (completed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return completed;
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

    private async Task<WorkerAccountEntity?> ReadAccountWithLockAsync(
        Guid workerId,
        Guid accountId,
        CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
        {
            return await db.WorkerAccounts
                .FromSqlInterpolated(
                    $"SELECT * FROM \"WorkerAccounts\" WHERE \"WorkerId\" = {workerId} AND \"AccountId\" = {accountId} FOR UPDATE")
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await db.WorkerAccounts
            .FirstOrDefaultAsync(x => x.WorkerId == workerId && x.AccountId == accountId, ct)
            .ConfigureAwait(false);
    }

    private async Task<int> CountDailyResponsesAsync(
        Guid workerId,
        Guid accountId,
        string? subProfileId,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var (start, end) = TopUpSessionRules.GetMoscowDayRange(nowUtc);
        return await db.CandidateResponses.AsNoTracking()
            .CountAsync(x =>
                    x.WorkerId == workerId
                    && x.AccountId == accountId
                    && (string.IsNullOrWhiteSpace(subProfileId) || x.AvitoSubProfileId == subProfileId)
                    && x.CollectedAt >= start
                    && x.CollectedAt < end,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Возвращает подтверждённый расход аванса за последний час. Берётся максимальный
    /// корректно идентифицированный баланс из снимков и сравнивается с текущим: рост
    /// баланса (ручное пополнение) не интерпретируется как расход.
    /// </summary>
    private async Task<decimal> GetBalanceSpentLastHourAsync(
        Guid workerId,
        Guid accountId,
        string? subProfileId,
        string? subProfileName,
        decimal currentBalance,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var sinceUtc = nowUtc - BalanceSpendLookback;
        var snapshotJson = await db.WorkerSnapshots.AsNoTracking()
            .Where(x => x.WorkerId == workerId && x.CapturedAtUtc >= sinceUtc && x.CapturedAtUtc <= nowUtc)
            .OrderByDescending(x => x.CapturedAtUtc)
            .Select(x => x.BalancesJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        decimal? highestObservedBalance = null;
        foreach (var json in snapshotJson)
        {
            var balance = TryGetSnapshotBalance(json, accountId, subProfileId, subProfileName);
            if (!balance.HasValue)
            {
                continue;
            }

            highestObservedBalance = !highestObservedBalance.HasValue || balance.Value > highestObservedBalance.Value
                ? balance.Value
                : highestObservedBalance;
        }

        return highestObservedBalance.HasValue
            ? Math.Max(0m, highestObservedBalance.Value - currentBalance)
            : 0m;
    }

    private static decimal? TryGetSnapshotBalance(
        string? balancesJson,
        Guid accountId,
        string? subProfileId,
        string? subProfileName)
    {
        if (string.IsNullOrWhiteSpace(balancesJson))
        {
            return null;
        }

        try
        {
            var account = (JsonSerializer.Deserialize<List<WorkerBalanceDto>>(balancesJson, JsonOptions) ?? [])
                .LastOrDefault(x => x.AccountId == accountId);
            if (account is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(subProfileId))
            {
                return account.TotalBalance;
            }

            var byId = account.SubProfiles
                .FirstOrDefault(x => string.Equals(x.SubProfileId, subProfileId, StringComparison.Ordinal));
            if (byId?.Balance is not null)
            {
                return byId.Balance.Value;
            }

            // Старые снимки не содержали Id. Имя годится только если оно однозначно,
            // иначе чужой субпрофиль мог бы искусственно увеличить сумму пополнения.
            var nameMatches = account.SubProfiles
                .Where(x => string.IsNullOrWhiteSpace(x.SubProfileId)
                            && string.Equals(x.SubProfileName, subProfileName, StringComparison.Ordinal))
                .ToList();
            return nameMatches.Count == 1 ? nameMatches[0].Balance : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
            session.FailureMessage,
            session.SubProfileId,
            session.SubProfileName,
            session.ProgressMessage,
            session.BalanceAfter,
            session.BalanceConfirmedAtUtc,
            session.AwaitingBalanceAtUtc);
}
