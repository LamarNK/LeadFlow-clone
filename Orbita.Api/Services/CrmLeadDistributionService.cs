using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Daily native-CRM distribution. The first eligible shift opens a persisted
/// five-minute collection window. Leads and NDZ are then balanced separately;
/// later leads use round-robin among the managers currently on shift, without
/// compensating for cards assigned before the active roster changed.
/// </summary>
public sealed class CrmLeadDistributionService(
    OrbitaDbContext db,
    UserManager<IdentityUser> users,
    TimeProvider? timeProvider = null)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> OfficeLocks = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public const string ReasonAuto = "Автоматическое распределение";
    public const string ReasonShiftStart = "Выдан при старте смены";
    public const string ReasonManual = "Ручное назначение";
    public const string ReasonDailyLead = "Ежедневное распределение: Лиды";
    public const string ReasonDailyNdz = "Ежедневное распределение: НДЗ";
    public const string ReasonFileImport = "Импорт лидов из файла";

    /// <summary>
    /// Assign a lead that arrived after the morning batch. During the five-minute
    /// collection window it remains unassigned and joins the morning lead pool.
    /// Caller owns SaveChanges.
    /// </summary>
    public async Task<CrmLeadDistribution.AutoAssignDecision> TryAutoAssignNewCardAsync(
        CrmCandidateCardEntity card,
        string? actorUserId = null,
        string? actorName = null,
        CancellationToken ct = default)
    {
        if (card.IsClosed
            || card.ManagerUserId is not null
            || !string.Equals(card.Stage, CrmStages.Lead, StringComparison.Ordinal))
        {
            return new CrmLeadDistribution.AutoAssignDecision(
                card.ManagerUserId,
                "Уже назначена, закрыта или находится не в Лидах");
        }

        var gate = OfficeLocks.GetOrAdd(card.OfficeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = UtcNow();
            var managers = await LoadEligibleDistributionManagersAsync(card.OfficeId, now, ct);
            if (managers.Count == 0)
            {
                return new CrmLeadDistribution.AutoAssignDecision(
                    null,
                    "Нет менеджеров или старших менеджеров на смене");
            }

            var localDate = CrmDailyDistribution.BusinessDate(now);
            var session = db.CrmDailyDistributionSessions.Local.FirstOrDefault(
                              x => x.OfficeId == card.OfficeId && x.LocalDate == localDate)
                          ?? await db.CrmDailyDistributionSessions.FirstOrDefaultAsync(
                              x => x.OfficeId == card.OfficeId && x.LocalDate == localDate,
                              ct);

            if (session is null)
            {
                return new CrmLeadDistribution.AutoAssignDecision(
                    null,
                    "Ожидание первого старта смены");
            }

            if (session.DistributedAtUtc is null || now < session.DistributeAfterUtc)
            {
                return new CrmLeadDistribution.AutoAssignDecision(
                    null,
                    "Ожидание завершения пятиминутного сбора смены");
            }

            var counters = await db.CrmDailyDistributionCounters
                .Where(x => x.OfficeId == card.OfficeId
                            && x.LocalDate == session.LocalDate
                            && x.Pool == CrmDailyDistribution.LeadPool)
                .ToListAsync(ct);
            var selected = CrmDailyDistribution.SelectNextForNewLead(
                managers.Select(x => x.UserId),
                session.LastLeadManagerUserId);
            if (selected is null)
            {
                return new CrmLeadDistribution.AutoAssignDecision(null, "Не удалось выбрать менеджера смены");
            }

            ApplyAssignment(card, selected, now);
            IncrementCounter(
                counters,
                card.OfficeId,
                session.LocalDate,
                CrmDailyDistribution.LeadPool,
                selected,
                now);
            session.LastLeadManagerUserId = selected;
            session.UpdatedAtUtc = now;
            await TouchManagerLastAssignedAsync(card.OfficeId, selected, now, ct);
            AddAssignedHistory(
                card.Id,
                ReasonDailyLead,
                actorUserId ?? "system",
                actorName ?? "Система",
                now);
            return new CrmLeadDistribution.AutoAssignDecision(selected, "Дневная равномерная очередь Лидов");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Immediately distributes one imported batch evenly among the eligible
    /// Manager/SeniorManager users who are on shift now. Existing workload is
    /// deliberately ignored: every uploaded batch is split independently.
    /// Caller owns SaveChanges and the surrounding transaction.
    /// </summary>
    public async Task<(int AssignedCount, int ManagersOnShift)> AssignImportedLeadBatchAsync(
        Guid officeId,
        IReadOnlyList<CrmCandidateCardEntity> cards,
        string actorUserId,
        string actorName,
        CancellationToken ct = default)
    {
        if (cards.Count == 0)
        {
            return (0, 0);
        }

        if (cards.Any(x => x.OfficeId != officeId
                           || x.IsClosed
                           || x.ManagerUserId is not null
                           || !string.Equals(x.Stage, CrmStages.Lead, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Импортированная пачка содержит карточку, которую нельзя распределить.");
        }

        var gate = OfficeLocks.GetOrAdd(officeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = UtcNow();
            var managers = await LoadEligibleDistributionManagersAsync(officeId, now, ct);
            if (managers.Count == 0)
            {
                return (0, 0);
            }

            var localDate = CrmDailyDistribution.BusinessDate(now);
            var plan = CrmDailyDistribution.BuildBalancedPlan(
                cards.Select(x => x.Id),
                managers.Select(x => x.UserId),
                officeId,
                localDate,
                $"{CrmDailyDistribution.LeadPool}-file-import");
            var cardsById = cards.ToDictionary(x => x.Id);
            foreach (var assignment in plan)
            {
                var card = cardsById[assignment.CardId];
                ApplyAssignment(card, assignment.ManagerUserId, now);
                AddAssignedHistory(
                    card.Id,
                    ReasonFileImport,
                    actorUserId,
                    actorName,
                    now);
            }

            var assignedIds = plan
                .Select(x => x.ManagerUserId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var manager in managers.Where(x => assignedIds.Contains(x.UserId)))
            {
                manager.CrmLastAutoAssignmentAtUtc = now;
            }

            // Keep the daytime round-robin cursor consistent when today's
            // shift session already exists, without making it a prerequisite
            // for a senior manager's explicit file import.
            var session = db.CrmDailyDistributionSessions.Local.FirstOrDefault(
                              x => x.OfficeId == officeId && x.LocalDate == localDate)
                          ?? await db.CrmDailyDistributionSessions.FirstOrDefaultAsync(
                              x => x.OfficeId == officeId && x.LocalDate == localDate,
                              ct);
            if (session is not null && plan.Count > 0)
            {
                var counters = await db.CrmDailyDistributionCounters
                    .Where(x => x.OfficeId == officeId
                                && x.LocalDate == localDate
                                && x.Pool == CrmDailyDistribution.LeadPool)
                    .ToListAsync(ct);
                foreach (var assignment in plan)
                {
                    IncrementCounter(
                        counters,
                        officeId,
                        localDate,
                        CrmDailyDistribution.LeadPool,
                        assignment.ManagerUserId,
                        now);
                }

                session.LastLeadManagerUserId = plan[^1].ManagerUserId;
                session.UpdatedAtUtc = now;
            }

            return (plan.Count, managers.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The first Manager/SeniorManager shift persists the five-minute window.
    /// OfficeLead/Admin shifts do not participate in automatic distribution.
    /// </summary>
    public async Task ScheduleDailyDistributionAsync(
        Guid officeId,
        string managerUserId,
        DateTime shiftStartedAtUtc,
        CancellationToken ct = default)
    {
        if (!await IsDistributionRecipientAsync(managerUserId))
        {
            return;
        }

        var gate = OfficeLocks.GetOrAdd(officeId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = UtcNow();
            if (CrmDailyDistribution.BusinessDate(shiftStartedAtUtc)
                != CrmDailyDistribution.BusinessDate(now))
            {
                return;
            }

            await EnsureSessionNoLockAsync(officeId, shiftStartedAtUtc, now, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Executes due sessions created by an actual Manager/SeniorManager shift
    /// start. Active profile flags alone never create a new daily session.
    /// Returned office ids need a board refresh.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> ProcessDueDailyDistributionsAsync(CancellationToken ct = default)
    {
        var now = UtcNow();
        var localDate = CrmDailyDistribution.BusinessDate(now);
        var dueOfficeIds = await db.CrmDailyDistributionSessions.AsNoTracking()
            .Where(x => x.LocalDate == localDate
                        && x.DistributedAtUtc == null
                        && x.DistributeAfterUtc <= now)
            .Select(x => x.OfficeId)
            .Distinct()
            .ToListAsync(ct);
        var changed = new HashSet<Guid>();

        foreach (var officeId in dueOfficeIds)
        {
            var gate = OfficeLocks.GetOrAdd(officeId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (await DistributeDueSessionNoLockAsync(officeId, now, ct))
                {
                    changed.Add(officeId);
                }
            }
            finally
            {
                gate.Release();
            }
        }

        return changed;
    }

    /// <summary>
    /// Legacy compatibility helper. New shift code must schedule the daily
    /// session instead of filling one manager to capacity.
    /// </summary>
    [Obsolete("Use ScheduleDailyDistributionAsync")]
    public Task<int> FillManagerFromQueueOnShiftStartAsync(
        Guid officeId,
        string managerUserId,
        int capacity,
        string actorUserId,
        string actorName,
        CancellationToken ct = default) => Task.FromResult(0);

    public void AssignManually(
        CrmCandidateCardEntity card,
        string managerUserId,
        string managerDisplayName,
        string actorUserId,
        string actorName,
        DateTime? atUtc = null)
    {
        var now = atUtc ?? UtcNow();
        ApplyAssignment(card, managerUserId, now);
        if (card.IsClosed)
        {
            card.IsClosed = false;
            card.CloseReason = null;
            card.ClosedAtUtc = null;
        }

        AddAssignedHistory(card.Id, managerDisplayName, actorUserId, actorName, now);
    }

    public Task<int> GetActiveLoadAsync(Guid officeId, string managerUserId, CancellationToken ct = default) =>
        db.CrmCandidateCards.CountAsync(
            x => x.OfficeId == officeId
                 && x.ManagerUserId == managerUserId
                 && x.IsInActiveLoad
                 && x.Stage != CrmManagerLoadRules.RobotStage
                 && !x.IsClosed,
            ct);

    public async Task<Dictionary<string, int>> GetActiveLoadsAsync(Guid officeId, CancellationToken ct = default)
    {
        var managerIds = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == officeId
                        && x.ManagerUserId != null
                        && x.IsInActiveLoad
                        && x.Stage != CrmManagerLoadRules.RobotStage
                        && !x.IsClosed)
            .Select(x => x.ManagerUserId!)
            .ToListAsync(ct);
        return managerIds
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    private async Task<CrmDailyDistributionSessionEntity> EnsureSessionNoLockAsync(
        Guid officeId,
        DateTime firstShiftStartedAtUtc,
        DateTime now,
        CancellationToken ct)
    {
        var normalizedStart = NormalizeUtc(firstShiftStartedAtUtc);
        var localDate = CrmDailyDistribution.BusinessDate(normalizedStart);
        var tracked = db.CrmDailyDistributionSessions.Local.FirstOrDefault(
            x => x.OfficeId == officeId && x.LocalDate == localDate);
        if (tracked is not null)
        {
            return tracked;
        }

        var existing = await db.CrmDailyDistributionSessions.FirstOrDefaultAsync(
            x => x.OfficeId == officeId && x.LocalDate == localDate,
            ct);
        if (existing is not null)
        {
            return existing;
        }

        var session = new CrmDailyDistributionSessionEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            LocalDate = localDate,
            FirstShiftStartedAtUtc = normalizedStart,
            DistributeAfterUtc = normalizedStart.Add(CrmDailyDistribution.ShiftCollectionDelay),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.CrmDailyDistributionSessions.Add(session);
        return session;
    }

    private async Task<bool> DistributeDueSessionNoLockAsync(
        Guid officeId,
        DateTime now,
        CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        var localDate = CrmDailyDistribution.BusinessDate(now);
        var session = await db.CrmDailyDistributionSessions
            .Where(x => x.OfficeId == officeId
                        && x.LocalDate == localDate
                        && x.DistributedAtUtc == null
                        && x.DistributeAfterUtc <= now)
            .OrderBy(x => x.LocalDate)
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            return false;
        }

        var managers = await LoadEligibleDistributionManagersAsync(officeId, now, ct);
        if (managers.Count == 0)
        {
            session.DistributeAfterUtc = now.AddMinutes(1);
            session.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }

            return false;
        }

        var officeStagesJson = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => x.CrmStagesJson)
            .SingleOrDefaultAsync(ct);
        var ndzStages = CrmDailyDistribution.ResolveNdzStages(
            CrmStages.Resolve(officeStagesJson));
        var ndzStageNames = ndzStages.Stages.ToArray();
        var leadCards = await db.CrmCandidateCards
            .Where(x => x.OfficeId == officeId
                        && !x.IsClosed
                        && x.Stage == CrmStages.Lead)
            .ToListAsync(ct);
        var ndzCards = ndzStageNames.Length == 0
            ? []
            : await db.CrmCandidateCards
                .Where(x => x.OfficeId == officeId
                            && !x.IsClosed
                            && ndzStageNames.Contains(x.Stage))
                .ToListAsync(ct);
        var cards = leadCards.Concat(ndzCards).ToList();
        var managerIds = managers.Select(x => x.UserId).ToList();
        var leadPlan = CrmDailyDistribution.BuildBalancedPlan(
            leadCards.Select(x => x.Id),
            managerIds,
            officeId,
            session.LocalDate,
            CrmDailyDistribution.LeadPool);
        var ndzPlan = CrmDailyDistribution.BuildBalancedPlan(
            ndzCards.Select(x => x.Id),
            managerIds,
            officeId,
            session.LocalDate,
            CrmDailyDistribution.NdzPool);
        IReadOnlySet<string> leadSourceStages = new HashSet<string>([CrmStages.Lead], StringComparer.Ordinal);
        IReadOnlySet<string> ndzSourceStages = new HashSet<string>(ndzStageNames, StringComparer.Ordinal);

        var counters = await db.CrmDailyDistributionCounters
            .Where(x => x.OfficeId == officeId && x.LocalDate == session.LocalDate)
            .ToListAsync(ct);
        foreach (var counter in counters)
        {
            counter.AssignedCount = 0;
            counter.UpdatedAtUtc = now;
        }

        var cardsById = cards.ToDictionary(x => x.Id);
        ApplyMorningPlan(
            leadPlan,
            cardsById,
            counters,
            officeId,
            session.LocalDate,
            CrmDailyDistribution.LeadPool,
            ReasonDailyLead,
            leadSourceStages,
            normalizeNdzTo: null,
            now);
        ApplyMorningPlan(
            ndzPlan,
            cardsById,
            counters,
            officeId,
            session.LocalDate,
            CrmDailyDistribution.NdzPool,
            ReasonDailyNdz,
            ndzSourceStages,
            normalizeNdzTo: ndzStages.PrimaryStage,
            now);

        session.ManagerRosterJson = JsonSerializer.Serialize(managerIds.OrderBy(x => x, StringComparer.Ordinal));
        session.LastLeadManagerUserId = leadPlan.LastOrDefault()?.ManagerUserId;
        session.DistributedAtUtc = now;
        session.UpdatedAtUtc = now;
        foreach (var manager in managers)
        {
            manager.CrmLastAutoAssignmentAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return cards.Count > 0;
    }

    private void ApplyMorningPlan(
        IReadOnlyList<CrmDailyDistribution.Assignment> plan,
        IReadOnlyDictionary<Guid, CrmCandidateCardEntity> cardsById,
        List<CrmDailyDistributionCounterEntity> counters,
        Guid officeId,
        DateOnly localDate,
        string pool,
        string reason,
        IReadOnlySet<string> allowedSourceStages,
        string? normalizeNdzTo,
        DateTime now)
    {
        foreach (var assignment in plan)
        {
            if (!cardsById.TryGetValue(assignment.CardId, out var card))
            {
                continue;
            }

            // The plan is built from an explicit office-stage allowlist. Keep a
            // second guard at application time so unrelated columns can never
            // be reassigned or normalized into NDZ by a stale/contaminated plan.
            if (!allowedSourceStages.Contains(card.Stage))
            {
                continue;
            }

            var ownerChanged = !string.Equals(
                card.ManagerUserId,
                assignment.ManagerUserId,
                StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(normalizeNdzTo)
                && !string.Equals(card.Stage, normalizeNdzTo, StringComparison.Ordinal))
            {
                var previous = card.Stage;
                card.Stage = normalizeNdzTo;
                card.StageChangedAtUtc = now;
                db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
                {
                    Id = Guid.NewGuid(),
                    CardId = card.Id,
                    Action = "StageChanged",
                    Details = $"{previous} → {normalizeNdzTo} · {reason}",
                    ActorUserId = "system",
                    ActorName = "Система",
                    CreatedAtUtc = now
                });
            }

            ApplyAssignment(card, assignment.ManagerUserId, now);
            IncrementCounter(counters, officeId, localDate, pool, assignment.ManagerUserId, now);
            if (ownerChanged)
            {
                AddAssignedHistory(card.Id, reason, "system", "Система", now);
            }
        }
    }

    private void IncrementCounter(
        List<CrmDailyDistributionCounterEntity> counters,
        Guid officeId,
        DateOnly localDate,
        string pool,
        string managerUserId,
        DateTime now)
    {
        var counter = counters.FirstOrDefault(
            x => x.Pool == pool
                 && string.Equals(x.ManagerUserId, managerUserId, StringComparison.Ordinal));
        if (counter is null)
        {
            counter = new CrmDailyDistributionCounterEntity
            {
                OfficeId = officeId,
                LocalDate = localDate,
                Pool = pool,
                ManagerUserId = managerUserId
            };
            counters.Add(counter);
            db.CrmDailyDistributionCounters.Add(counter);
        }

        counter.AssignedCount++;
        counter.UpdatedAtUtc = now;
    }

    private async Task<List<PanelUserProfileEntity>> LoadEligibleDistributionManagersAsync(
        Guid officeId,
        DateTime now,
        CancellationToken ct)
    {
        var recipientIds = await LoadDistributionRecipientIdsAsync();
        if (recipientIds.Count == 0)
        {
            return [];
        }

        var profiles = await db.PanelUserProfiles
            .Where(x => x.OfficeId == officeId && recipientIds.Contains(x.UserId))
            .ToListAsync(ct);
        return profiles
            .Where(x => CrmShiftRules.IsEffectivelyOnShift(
                x.CrmShiftActive,
                x.CrmShiftStartedAtUtc,
                now))
            .OrderBy(x => x.UserId, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<HashSet<string>> LoadDistributionRecipientIdsAsync()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (users is null)
        {
            return ids;
        }

        foreach (var role in PanelRoles.CrmDistributionRoles)
        {
            foreach (var user in await users.GetUsersInRoleAsync(role))
            {
                ids.Add(user.Id);
            }
        }

        // Identity supports multiple roles. An elevated account must not start
        // receiving cards merely because an old Manager role was left on it.
        foreach (var excludedRole in new[] { PanelRoles.Admin, PanelRoles.OfficeLead })
        {
            foreach (var user in await users.GetUsersInRoleAsync(excludedRole))
            {
                ids.Remove(user.Id);
            }
        }

        return ids;
    }

    private async Task<bool> IsDistributionRecipientAsync(string userId)
    {
        if (users is null)
        {
            return false;
        }

        var user = await users.FindByIdAsync(userId);
        if (user is null)
        {
            return false;
        }

        if (await users.IsInRoleAsync(user, PanelRoles.Admin)
            || await users.IsInRoleAsync(user, PanelRoles.OfficeLead))
        {
            return false;
        }

        foreach (var role in PanelRoles.CrmDistributionRoles)
        {
            if (await users.IsInRoleAsync(user, role))
            {
                return true;
            }
        }

        return false;
    }

    private async Task TouchManagerLastAssignedAsync(
        Guid officeId,
        string managerUserId,
        DateTime atUtc,
        CancellationToken ct)
    {
        var profile = await db.PanelUserProfiles
            .FirstOrDefaultAsync(x => x.OfficeId == officeId && x.UserId == managerUserId, ct);
        if (profile is not null)
        {
            profile.CrmLastAutoAssignmentAtUtc = atUtc;
        }
    }

    private static void ApplyAssignment(CrmCandidateCardEntity card, string managerUserId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(card.InitialManagerUserId))
        {
            card.InitialManagerUserId = managerUserId;
            card.InitialAssignedAtUtc = now;
        }

        card.ManagerUserId = managerUserId;
        card.IsInActiveLoad = true;
        card.UpdatedAtUtc = now;
    }

    private void AddAssignedHistory(
        Guid cardId,
        string details,
        string actorUserId,
        string actorName,
        DateTime at) =>
        db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = "Assigned",
            Details = details,
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAtUtc = at
        });

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
