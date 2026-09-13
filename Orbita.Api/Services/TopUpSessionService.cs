using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    TimeProvider timeProvider,
    ILogger<TopUpSessionService>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<TopUpSessionService>.Instance;
    private static readonly TimeSpan SessionTtl = TopUpSessionRules.PauseLeaseTtl;
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
    /// <summary>Воркер берёт в работу только эти статусы. Готовый QR — задача оператора.</summary>
    private static readonly string[] WorkerDispatchStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started
    ];
    /// <summary>Пауза мониторинга держится, пока воркер ещё автоматизирует пополнение.</summary>
    private static readonly string[] PauseHoldingStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started,
        TopUpSessionStatuses.PaymentClaimed
    ];
    private static readonly string[] ConflictStatuses =
    [
        TopUpSessionStatuses.Requested,
        TopUpSessionStatuses.Started,
        TopUpSessionStatuses.PaymentClaimed,
        TopUpSessionStatuses.QrReady,
        TopUpSessionStatuses.AwaitingBalance,
        TopUpSessionStatuses.VerificationRequired
    ];
    private static readonly string[] ConfirmableStatuses =
    [
        TopUpSessionStatuses.QrReady,
        TopUpSessionStatuses.AwaitingBalance,
        TopUpSessionStatuses.VerificationRequired
    ];
    private static readonly TimeSpan BalanceConfirmationTimeout = TopUpSessionRules.BalanceConfirmationTtl;

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
            _log.LogWarning(
                "Top-up: отказ создания, воркер {WorkerId} оффлайн, account={AccountId} subprofile={SubProfileId}.",
                workerId,
                accountId,
                subProfileId);
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
                _log.LogInformation(
                    "Top-up: отказ создания, уже есть сессия {ExistingSessionId} ({Status}) worker={WorkerId} account={AccountId} subprofile={SubProfileId}.",
                    active.Id,
                    active.Status,
                    workerId,
                    accountId,
                    subProfileId);
                return (null, new TopUpSessionConflictDto(
                    "Для этого аккаунта уже есть активная сессия пополнения.",
                    active.Id,
                    active.AccountName));
            }

            // Истекла, но не подметена: завершаем перед созданием замены.
            await ExpireSessionAsync(active, now, ct).ConfigureAwait(false);
        }

        var latestCompletedAtUtc = await db.TopUpSessions
            .AsNoTracking()
            .Where(x =>
                x.AccountId == accountId
                && x.SubProfileId == (subProfileId ?? string.Empty)
                && x.Status == TopUpSessionStatuses.Completed)
            .MaxAsync(x => (DateTime?)x.CompletedAtUtc, ct)
            .ConfigureAwait(false);
        if (TopUpSessionRules.IsRepeatTopUpCooldownActive(latestCompletedAtUtc, now))
        {
            var availableAtUtc = latestCompletedAtUtc!.Value + TopUpSessionRules.RepeatTopUpCooldown;
            _log.LogInformation(
                "Top-up: отказ создания во время cooldown worker={WorkerId} account={AccountId} subprofile={SubProfileId} available={AvailableAtUtc}.",
                workerId,
                accountId,
                subProfileId,
                availableAtUtc);
            return (null, new TopUpSessionConflictDto(
                $"Пополнение подтверждено недавно. Повторное пополнение будет доступно после {availableAtUtc:HH:mm} UTC."));
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
        var unresolvedHistoryCutoff = now - TopUpSessionRules.BalanceConfirmationTtl;
        var unresolvedHistoryConfirmations = await db.TopUpSessions
            .AsNoTracking()
            .Where(x =>
                x.AccountId == accountId
                && x.SubProfileId == (subProfileId ?? string.Empty)
                && x.Status == TopUpSessionStatuses.Completed
                && x.HistoryConfirmedAtUtc != null
                && x.HistoryConfirmedAtUtc >= unresolvedHistoryCutoff
                && x.BalanceConfirmedAtUtc == null)
            .Select(x => new { x.CurrentBalance, x.RequestedAmount, x.TargetBalance })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (unresolvedHistoryConfirmations.Count > 0)
        {
            var confirmedFloor = unresolvedHistoryConfirmations.Max(x =>
                Math.Min(x.CurrentBalance + x.RequestedAmount, x.TargetBalance));
            currentBalance = Math.Max(currentBalance, confirmedFloor);
        }

        if (!TopUpSessionRules.IsEligible(currentBalance))
        {
            _log.LogInformation(
                "Top-up: отказ создания, баланс {Balance} не ниже порога worker={WorkerId} account={AccountId} subprofile={SubProfileId}.",
                currentBalance,
                workerId,
                accountId,
                subProfileId);
            return (null, new TopUpSessionConflictDto(
                $"Баланс аккаунта ({currentBalance:0.##} ₽) не ниже порога пополнения."));
        }

        var dailyResponses = await CountDailyResponsesAsync(workerId, accountId, subProfile?.Id, now, ct).ConfigureAwait(false);
        var requestedAmount = TopUpSessionRules.ResolveRequestedAmount(currentBalance, dailyResponses);
        var targetBalance = TopUpSessionRules.ResolveTargetBalance(currentBalance, dailyResponses);
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
            ExpiresAtUtc = now.Add(TopUpSessionRules.QueueTtl),
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
            _log.LogInformation(
                "Top-up: отказ создания, конкурентная сессия {ExistingSessionId} worker={WorkerId} account={AccountId} subprofile={SubProfileId}.",
                winner?.Id,
                workerId,
                accountId,
                subProfileId);
            return (null, new TopUpSessionConflictDto(
                "Для этого аккаунта уже есть активная сессия пополнения.",
                winner?.Id,
                winner?.AccountName));
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        _log.LogInformation(
            "Top-up: создана сессия {SessionId} worker={WorkerId} account={AccountId} ({AccountName}) subprofile={SubProfileId}/{SubProfileName} balance={Current} target={Target} amount={Requested} expires={ExpiresAtUtc} pauseLease={OwnsPause}.",
            session.Id,
            session.WorkerId,
            session.AccountId,
            session.AccountName,
            session.SubProfileId,
            session.SubProfileName,
            session.CurrentBalance,
            session.TargetBalance,
            session.RequestedAmount,
            session.ExpiresAtUtc,
            session.OwnsPauseLease);

        panelRealtime.Notify(
            [PanelChangeKind.Workers, PanelChangeKind.Accounts],
            worker.OfficeId,
            worker.Id);
        await workerPushNotifier.PushConfigChangedAsync(worker.Id, ct).ConfigureAwait(false);

        await PushNextPendingAsync(worker.Id, ct).ConfigureAwait(false);

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
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var todayStartUtc = TopUpSessionRules.GetMoscowDayRange(nowUtc).UtcStartInclusive;
            var cooldownStartUtc = nowUtc - TopUpSessionRules.RepeatTopUpCooldown;
            var completedCutoffUtc = todayStartUtc < cooldownStartUtc
                ? todayStartUtc
                : cooldownStartUtc;
            query = query.Where(x =>
                ActiveStatuses.Contains(x.Status)
                || x.Status == TopUpSessionStatuses.AwaitingBalance
                || x.Status == TopUpSessionStatuses.VerificationRequired
                || (x.Status == TopUpSessionStatuses.Completed && x.CompletedAtUtc >= completedCutoffUtc));
        }

        var sessions = await (history
                ? query.OrderByDescending(x => x.CompletedAtUtc ?? x.CreatedAtUtc)
                : query.OrderBy(x => x.CreatedAtUtc))
            .Take(history ? 500 : 200)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sessions.Select(x => ToDto(x, x.Worker.DisplayName)).ToArray();
    }

    public Task<(bool Success, string? Error)> CancelAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct = default) =>
        CancelOrDeferForHistoryCheckAsync(sessionId, principal, ct);

    private async Task<(bool Success, string? Error)> CancelOrDeferForHistoryCheckAsync(
        Guid sessionId,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var session = await db.TopUpSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (session?.Status == TopUpSessionStatuses.QrReady)
        {
            return await CompleteByOperatorAsync(
                    sessionId,
                    principal,
                    TopUpSessionStatuses.VerificationRequired,
                    current => current.Status == TopUpSessionStatuses.QrReady
                        ? null
                        : "Проверить историю можно только после формирования QR-кода.",
                    ownerError: "Отменить может только оператор, начавший сессию.",
                    ct)
                .ConfigureAwait(false);
        }

        return await CompleteByOperatorAsync(
                sessionId,
                principal,
                TopUpSessionStatuses.Cancelled,
                current => TopUpSessionStatuses.IsActive(current.Status)
                    ? null
                    : "Сессия уже завершена.",
                ownerError: "Отменить может только оператор, начавший сессию.",
                ct)
            .ConfigureAwait(false);
    }

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

        if (string.Equals(session.Status, terminalStatus, StringComparison.OrdinalIgnoreCase)
            || (terminalStatus == TopUpSessionStatuses.AwaitingBalance
                && session.Status == TopUpSessionStatuses.Completed))
        {
            _log.LogInformation(
                "Top-up: повторное действие для {SessionId}, статус уже {Status} worker={WorkerId} account={AccountName}.",
                session.Id,
                session.Status,
                session.WorkerId,
                session.AccountName);
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (true, null);
        }

        var validationError = validate(session);
        if (validationError is not null)
        {
            _log.LogWarning(
                "Top-up: оператор не смог перевести {SessionId} в {Status}: {Error} (сейчас {CurrentStatus}) worker={WorkerId} account={AccountName}.",
                session.Id,
                terminalStatus,
                validationError,
                session.Status,
                session.WorkerId,
                session.AccountName);
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return (false, validationError);
        }

        session.Status = terminalStatus;
        var completedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (terminalStatus is TopUpSessionStatuses.AwaitingBalance or TopUpSessionStatuses.VerificationRequired)
        {
            session.AwaitingBalanceAtUtc = completedAt;
            session.CompletedAtUtc = null;
            session.ProgressMessage = terminalStatus == TopUpSessionStatuses.AwaitingBalance
                ? "Оплата отмечена. На следующем проходе проверим историю операций."
                : "Перед отменой на следующем проходе проверим историю операций Avito.";
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
        _log.LogInformation(
            "Top-up: оператор {Operator} перевёл {SessionId} в {Status} worker={WorkerId} account={AccountName} subprofile={SubProfileName}.",
            session.OperatorDisplayName,
            session.Id,
            session.Status,
            session.WorkerId,
            session.AccountName,
            session.SubProfileName);
        await PushNextPendingAsync(session.WorkerId, ct).ConfigureAwait(false);
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
            await PushNextPendingAsync(session.WorkerId, ct).ConfigureAwait(false);
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

        var previousStatus = session.Status;
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
                session.ExpiresAtUtc = now.Add(SessionTtl);
            }
            else if (request.Status == TopUpSessionStatuses.PaymentClaimed)
            {
                session.PaymentClaimedAtUtc ??= now;
                session.ExpiresAtUtc = now.Add(SessionTtl);
            }
            else if (request.Status == TopUpSessionStatuses.QrReady)
            {
                session.QrReadyAtUtc ??= now;
                // QR оплачивается вручную, поэтому короткий технический TTL сессии
                // не должен закрыть её раньше следующего обычного прохода воркера.
                // На нём история кошелька будет проверена даже если аванс ещё старый.
                session.ExpiresAtUtc = now.Add(TopUpSessionRules.BalanceConfirmationTtl);
                session.QrImageBase64 = request.QrImageBase64;
                session.QrImageUrl = request.QrImageUrl;
            }
        }
        else if (applyingQr)
        {
            session.QrImageBase64 = request.QrImageBase64;
            session.QrImageUrl = request.QrImageUrl;
            session.ExpiresAtUtc = now.Add(TopUpSessionRules.BalanceConfirmationTtl);
        }

        if (request.ProgressMessage is not null)
        {
            session.ProgressMessage = TopUpSessionRules.SanitizeProgressMessage(request.ProgressMessage);
        }

        var dispatchNext = false;
        if (statusChanged && request.Status == TopUpSessionStatuses.QrReady)
        {
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
            dispatchNext = true;
        }
        else if (!TopUpSessionStatuses.IsActive(request.Status)
                 && request.Status != TopUpSessionStatuses.VerificationRequired)
        {
            session.CompletedAtUtc = now;
            session.FailureMessage = request.FailureMessage;
            ClearQrData(session);
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
            dispatchNext = true;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (!TopUpSessionStatuses.IsActive(request.Status)
            || (statusChanged && request.Status == TopUpSessionStatuses.QrReady))
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                session.OfficeId,
                session.WorkerId);
        }

        if (statusChanged)
        {
            if (request.Status == TopUpSessionStatuses.QrReady)
            {
                _log.LogInformation(
                    "Top-up: QR готов {SessionId} worker={WorkerId} account={AccountName} subprofile={SubProfileName} amount={Requested} expires={ExpiresAtUtc}.",
                    session.Id,
                    session.WorkerId,
                    session.AccountName,
                    session.SubProfileName,
                    session.RequestedAmount,
                    session.ExpiresAtUtc);
            }
            else if (request.Status is TopUpSessionStatuses.Failed or TopUpSessionStatuses.Expired)
            {
                _log.LogWarning(
                    "Top-up: сессия {SessionId} {PreviousStatus} → {Status} worker={WorkerId} account={AccountName} subprofile={SubProfileName}: {Failure}.",
                    session.Id,
                    previousStatus,
                    request.Status,
                    session.WorkerId,
                    session.AccountName,
                    session.SubProfileName,
                    request.FailureMessage);
            }
            else
            {
                _log.LogInformation(
                    "Top-up: сессия {SessionId} {PreviousStatus} → {Status} worker={WorkerId} account={AccountName} subprofile={SubProfileName}.",
                    session.Id,
                    previousStatus,
                    request.Status,
                    session.WorkerId,
                    session.AccountName,
                    session.SubProfileName);
            }
        }

        if (dispatchNext)
        {
            await PushNextPendingAsync(session.WorkerId, ct).ConfigureAwait(false);
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
            session.ExpiresAtUtc = now.Add(SessionTtl);

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
            .Where(x =>
                x.WorkerId == workerId
                && WorkerDispatchStatuses.Contains(x.Status)
                && x.ExpiresAtUtc > now)
            .OrderBy(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (session is null)
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

    public async Task<IReadOnlyList<WorkerPendingTopUpHistoryCheckDto>> GetPendingHistoryChecksForWorkerAsync(
        Guid workerId,
        CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.TopUpSessions
            .AsNoTracking()
            .Where(x =>
                x.WorkerId == workerId
                && x.HistoryConfirmedAtUtc == null
                && (x.Status == TopUpSessionStatuses.AwaitingBalance
                    || x.Status == TopUpSessionStatuses.VerificationRequired)
                && x.SubProfileId != "")
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new WorkerPendingTopUpHistoryCheckDto(
                x.Id,
                x.AccountId,
                x.SubProfileId,
                x.SubProfileName,
                x.RequestedAmount))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<ConfirmTopUpHistoryResult> ConfirmPaymentFromHistoryAsync(
        Guid workerId,
        ConfirmTopUpHistoryRequest request,
        CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var account = await ReadAccountWithLockAsync(workerId, request.AccountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new ConfirmTopUpHistoryResult(0, "Аккаунт не принадлежит воркеру.");
        }

        var candidateIds = await db.TopUpSessions
            .AsNoTracking()
            .Where(x =>
                x.WorkerId == workerId
                && x.AccountId == request.AccountId
                && x.SubProfileId == request.SubProfileId
                && x.HistoryConfirmedAtUtc == null
                && (x.Status == TopUpSessionStatuses.AwaitingBalance
                    || x.Status == TopUpSessionStatuses.VerificationRequired))
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => x.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var candidates = new List<TopUpSessionEntity>(candidateIds.Count);
        foreach (var candidateId in candidateIds)
        {
            var candidate = await LockTopUpSessionRowAsync(candidateId, workerId, ct).ConfigureAwait(false);
            if (candidate is not null
                && candidate.HistoryConfirmedAtUtc is null
                && candidate.Status is TopUpSessionStatuses.AwaitingBalance
                    or TopUpSessionStatuses.VerificationRequired)
            {
                candidates.Add(candidate);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var matchedSessionIds = new HashSet<Guid>();
        var confirmed = 0;
        foreach (var operation in request.Operations.OrderBy(x => x.OccurredAtUtc))
        {
            var session = candidates
                .Where(x =>
                    x.HistoryConfirmedAtUtc == null
                    && !matchedSessionIds.Contains(x.Id)
                    && Math.Abs(x.RequestedAmount - operation.Amount) <= TopUpSessionRules.BalanceEpsilonRub
                    && operation.OccurredAtUtc >= (x.PaymentClaimedAtUtc
                        ?? x.QrReadyAtUtc
                        ?? x.CreatedAtUtc).AddMinutes(-2)
                    && operation.OccurredAtUtc <= request.CapturedAtUtc.AddMinutes(2))
                .OrderByDescending(x => x.PaymentClaimedAtUtc ?? x.QrReadyAtUtc ?? x.CreatedAtUtc)
                .FirstOrDefault();
            if (session is null)
            {
                continue;
            }

            session.Status = TopUpSessionStatuses.Completed;
            session.AwaitingBalanceAtUtc = null;
            session.HistoryConfirmedAtUtc = now;
            session.HistoryOperationAtUtc = operation.OccurredAtUtc;
            session.CompletedAtUtc = now;
            session.ProgressMessage = "Пополнение подтверждено историей операций Avito.";
            session.FailureMessage = null;
            if (request.AdvanceBalance is decimal advanceBalance
                && advanceBalance > session.CurrentBalance)
            {
                ApplyObservedSubProfileBalance(
                    account,
                    request.SubProfileId,
                    advanceBalance,
                    request.CapturedAtUtc);
                session.BalanceAfter = advanceBalance;
                session.BalanceConfirmedAtUtc = request.CapturedAtUtc;
            }

            ClearQrData(session);
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
            matchedSessionIds.Add(session.Id);
            confirmed++;

            _log.LogInformation(
                "Top-up: оплата найдена в истории Avito {SessionId} → completed worker={WorkerId} account={AccountName} subprofile={SubProfileName} amount={Amount} occurred={OccurredAtUtc}.",
                session.Id,
                session.WorkerId,
                session.AccountName,
                session.SubProfileName,
                operation.Amount,
                operation.OccurredAtUtc);
        }

        var cancelledAfterVerification = 0;
        var operationsAvailableThrough = request.CapturedAtUtc.AddMinutes(1);
        foreach (var session in candidates.Where(x =>
                     x.Status == TopUpSessionStatuses.VerificationRequired
                     && x.HistoryConfirmedAtUtc == null
                     && (x.PaymentClaimedAtUtc ?? x.QrReadyAtUtc ?? x.CreatedAtUtc) <= operationsAvailableThrough))
        {
            session.Status = TopUpSessionStatuses.Cancelled;
            session.CompletedAtUtc = now;
            session.ProgressMessage = "В истории операций пополнение не найдено. Сессия отменена.";
            session.FailureMessage = null;
            ClearQrData(session);
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
            cancelledAfterVerification++;
        }

        if (confirmed == 0 && cancelledAfterVerification == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return new ConfirmTopUpHistoryResult(0);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (var session in candidates.Where(x => x.Status == TopUpSessionStatuses.Completed))
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                session.OfficeId,
                session.WorkerId);
        }

        foreach (var session in candidates.Where(x => x.Status == TopUpSessionStatuses.Cancelled))
        {
            panelRealtime.Notify(
                [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                session.OfficeId,
                session.WorkerId);
        }

        return new ConfirmTopUpHistoryResult(confirmed);
    }

    private static void ApplyObservedSubProfileBalance(
        WorkerAccountEntity account,
        string subProfileId,
        decimal advanceBalance,
        DateTime capturedAtUtc)
    {
        var profiles = SubProfileDeserializer.Deserialize(account.SubProfilesJson)?.ToList();
        if (profiles is null || profiles.Count == 0)
        {
            return;
        }

        var index = profiles.FindIndex(x =>
            string.Equals(x.Id, subProfileId, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        profiles[index] = profiles[index] with { Balance = advanceBalance };
        account.SubProfilesJson = JsonSerializer.Serialize(profiles, SubProfileJsonOptions.Serialize);
        account.TotalBalance = profiles.Sum(x => x.Balance ?? 0m);
        account.UpdatedAtUtc = capturedAtUtc;
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
            .Where(x =>
                (x.Status == TopUpSessionStatuses.AwaitingBalance
                 || x.Status == TopUpSessionStatuses.VerificationRequired)
                && x.AwaitingBalanceAtUtc != null
                && x.AwaitingBalanceAtUtc <= awaitingCutoff)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var session in unconfirmed)
        {
            _log.LogWarning(
                "Top-up: сессия {SessionId} не подтверждена за 24 часа worker={WorkerId} account={AccountName} subprofile={SubProfileName} current={Current} requested={Requested}.",
                session.Id,
                session.WorkerId,
                session.AccountName,
                session.SubProfileName,
                session.CurrentBalance,
                session.RequestedAmount);
            session.Status = TopUpSessionStatuses.Failed;
            session.CompletedAtUtc = now;
            session.FailureMessage = "Баланс не увеличился за 24 часа.";
            session.ProgressMessage = "Баланс не увеличился за 24 часа.";
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
        var reconciledHistory = await ReconcileHistoryConfirmedBalancesAsync(
                workerId,
                balances,
                capturedAtUtc,
                ct)
            .ConfigureAwait(false);

        var candidates = await db.TopUpSessions
            .Where(x =>
                x.WorkerId == workerId
                && x.SubProfileId == ""
                && ConfirmableStatuses.Contains(x.Status))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return reconciledHistory;
        }

        var conflicts = await db.TopUpSessions.AsNoTracking()
            .Where(x => x.WorkerId == workerId && ConflictStatuses.Contains(x.Status))
            .Select(x => new { x.Id, x.AccountId, x.SubProfileId, x.CreatedAtUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var completed = 0;
        foreach (var session in candidates)
        {
            var account = balances.FirstOrDefault(x => x.AccountId == session.AccountId);
            if (account is null)
            {
                _log.LogWarning(
                    "Top-up: снимок воркера {WorkerId} без аккаунта {AccountId} для сессии {SessionId} ({Status}).",
                    workerId,
                    session.AccountId,
                    session.Id,
                    session.Status);
                continue;
            }

            var blockedByNewer = conflicts.Any(x =>
                x.Id != session.Id
                && x.AccountId == session.AccountId
                && string.Equals(x.SubProfileId, session.SubProfileId, StringComparison.Ordinal)
                && x.CreatedAtUtc > session.CreatedAtUtc);
            if (blockedByNewer)
            {
                _log.LogInformation(
                    "Top-up: сессию {SessionId} ({Status}) не подтверждаем, на субпрофиле {SubProfileId} уже есть более новая операция.",
                    session.Id,
                    session.Status,
                    session.SubProfileId);
                continue;
            }

            var previousStatus = session.Status;
            var balanceAfter = TryGetAccountBalance(account, session.SubProfileId, session.SubProfileName);
            if (balanceAfter is not decimal actual
                || !TopUpSessionRules.IsExpectedBalanceIncrease(
                    session.CurrentBalance,
                    session.RequestedAmount,
                    session.TargetBalance,
                    actual))
            {
                if (balanceAfter is not decimal seen)
                {
                    _log.LogWarning(
                        "Top-up: в снимке нет баланса субпрофиля {SubProfileId}/{SubProfileName} для сессии {SessionId} ({Status}).",
                        session.SubProfileId,
                        session.SubProfileName,
                        session.Id,
                        session.Status);
                }
                else if (seen > session.CurrentBalance)
                {
                    _log.LogWarning(
                        "Top-up: сессия {SessionId} ({Status}) не подтверждена: было {Current}, стало {Actual}, ждали {Requested} до {Target}.",
                        session.Id,
                        session.Status,
                        session.CurrentBalance,
                        seen,
                        session.RequestedAmount,
                        session.TargetBalance);
                }
                else
                {
                    _log.LogDebug(
                        "Top-up: сессия {SessionId} ({Status}) ждёт рост баланса: было {Current}, снимок {Actual}, ждали {Requested}.",
                        session.Id,
                        session.Status,
                        session.CurrentBalance,
                        seen,
                        session.RequestedAmount);
                }

                continue;
            }

            session.Status = TopUpSessionStatuses.Completed;
            session.BalanceAfter = actual;
            session.BalanceConfirmedAtUtc = capturedAtUtc;
            session.CompletedAtUtc = capturedAtUtc;
            session.ProgressMessage = "Пополнение подтверждено новым снимком баланса.";
            ClearQrData(session);
            await ReleasePauseAsync(session, ct).ConfigureAwait(false);
            completed++;
            _log.LogInformation(
                "Top-up: баланс подтверждён {SessionId} {PreviousStatus} → completed {Current} → {Actual} requested={Requested} worker={WorkerId} account={AccountName} subprofile={SubProfileName}.",
                session.Id,
                previousStatus,
                session.CurrentBalance,
                actual,
                session.RequestedAmount,
                session.WorkerId,
                session.AccountName,
                session.SubProfileName);
        }

        if (completed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var session in candidates.Where(x => x.Status == TopUpSessionStatuses.Completed))
            {
                panelRealtime.Notify(
                    [PanelChangeKind.Workers, PanelChangeKind.Accounts],
                    session.OfficeId,
                    session.WorkerId);
            }

            await PushNextPendingAsync(workerId, ct).ConfigureAwait(false);
        }

        return completed + reconciledHistory;
    }

    private async Task<int> ReconcileHistoryConfirmedBalancesAsync(
        Guid workerId,
        IReadOnlyList<WorkerBalanceDto> balances,
        DateTime capturedAtUtc,
        CancellationToken ct)
    {
        var candidates = await db.TopUpSessions
            .Where(x =>
                x.WorkerId == workerId
                && x.Status == TopUpSessionStatuses.Completed
                && x.HistoryConfirmedAtUtc != null
                && x.BalanceConfirmedAtUtc == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var reconciled = 0;
        foreach (var session in candidates)
        {
            var account = balances.FirstOrDefault(x => x.AccountId == session.AccountId);
            var balanceAfter = account is null
                ? null
                : TryGetAccountBalance(account, session.SubProfileId, session.SubProfileName);
            if (balanceAfter is not decimal actual
                || !TopUpSessionRules.IsExpectedBalanceIncrease(
                    session.CurrentBalance,
                    session.RequestedAmount,
                    session.TargetBalance,
                    actual))
            {
                continue;
            }

            session.BalanceAfter = actual;
            session.BalanceConfirmedAtUtc = capturedAtUtc;
            reconciled++;
            _log.LogInformation(
                "Top-up: аванс догнал подтверждённую историей оплату {SessionId} balance={Actual} worker={WorkerId} account={AccountName} subprofile={SubProfileName}.",
                session.Id,
                actual,
                session.WorkerId,
                session.AccountName,
                session.SubProfileName);
        }

        if (reconciled > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return reconciled;
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
        _log.LogInformation(
            "Top-up: сессия {SessionId} истекла (была {Status}) worker={WorkerId} account={AccountName} subprofile={SubProfileName}.",
            session.Id,
            session.Status,
            session.WorkerId,
            session.AccountName,
            session.SubProfileName);
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

    private static decimal? TryGetAccountBalance(
        WorkerBalanceDto account,
        string? subProfileId,
        string? subProfileName)
    {
        if (string.IsNullOrWhiteSpace(subProfileId) && string.IsNullOrWhiteSpace(subProfileName))
        {
            return account.TotalBalance;
        }

        var byId = string.IsNullOrWhiteSpace(subProfileId)
            ? null
            : account.SubProfiles
                .FirstOrDefault(x => string.Equals(x.SubProfileId, subProfileId, StringComparison.Ordinal));
        if (byId?.Balance is not null)
        {
            return byId.Balance.Value;
        }

        if (string.IsNullOrWhiteSpace(subProfileName))
        {
            return string.IsNullOrWhiteSpace(subProfileId) ? account.TotalBalance : null;
        }

        var nameMatches = account.SubProfiles
            .Where(x => string.Equals(x.SubProfileName, subProfileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return nameMatches.Count == 1 ? nameMatches[0].Balance : null;
    }

    private async Task PushNextPendingAsync(Guid workerId, CancellationToken ct)
    {
        var pending = await GetPendingForWorkerAsync(workerId, ct).ConfigureAwait(false);
        if (pending is null)
        {
            _log.LogDebug("Top-up: у воркера {WorkerId} нет следующей сессии в очереди.", workerId);
            return;
        }

        _log.LogInformation(
            "Top-up: следующая сессия {SessionId} отдана воркеру {WorkerId} account={AccountName} subprofile={SubProfileId}/{SubProfileName} amount={Requested}.",
            pending.SessionId,
            workerId,
            pending.AccountName,
            pending.SubProfileId,
            pending.SubProfileName,
            pending.RequestedAmount);
        await workerPushNotifier.TryPushTopUpSessionAsync(workerId, pending, ct).ConfigureAwait(false);
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

        // Если воркер ещё автоматизирует другую сессию — аренда остаётся (пауза сохраняется).
        var hasOtherActiveSession = await db.TopUpSessions
            .AnyAsync(x =>
                    x.WorkerId == session.WorkerId
                    && x.Id != session.Id
                    && PauseHoldingStatuses.Contains(x.Status),
                ct)
            .ConfigureAwait(false);
        if (hasOtherActiveSession)
        {
            _log.LogInformation(
                "Top-up: пауза воркера {WorkerId} сохранена, после {SessionId} ({Status}) в очереди ещё есть сессии.",
                session.WorkerId,
                session.Id,
                session.Status);
            return;
        }

        // Последняя активная сессия завершилась: освобождаем аренду и восстанавливаем базовое
        // состояние паузы. Аренда могла быть инвалидирована ручной сменой паузы — тогда
        // TopUpPauseLeaseId уже null, и мы сюда не попали.
        worker.IsMonitoringPaused = worker.TopUpPauseBaselinePaused;
        worker.TopUpPauseLeaseId = null;
        worker.TopUpPauseLeaseVersion++;
        _log.LogInformation(
            "Top-up: пауза воркера {WorkerId} снята после {SessionId} ({Status}).",
            session.WorkerId,
            session.Id,
            session.Status);
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
            session.AwaitingBalanceAtUtc,
            session.HistoryConfirmedAtUtc,
            session.HistoryOperationAtUtc);
}
