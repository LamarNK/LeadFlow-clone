using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>
/// Оркестрация распределения CRM-лидов: читает смену/нагрузку, применяет
/// <see cref="CrmLeadDistribution"/>, пишет ManagerUserId и историю Assigned.
///
/// <para>
/// <b>Владение модулем:</b> вся логика «кому отдать отклик» живёт здесь + в
/// <see cref="CrmLeadDistribution"/>. CRM workspace только вызывает эти методы.
/// </para>
///
/// Не меняет Bitrix / status отклика.
/// </summary>
public sealed class CrmLeadDistributionService(
    OrbitaDbContext db,
    UserManager<IdentityUser> users)
{
    public const string ReasonAuto = "Автоматическое распределение";
    public const string ReasonShiftStart = "Выдан при старте смены";
    public const string ReasonManual = "Ручное назначение";

    /// <summary>
    /// Попытаться назначить новую карточку менеджеру на смене.
    /// Если никого нет — карточка остаётся unassigned.
    /// Не вызывает SaveChanges (caller решает).
    /// </summary>
    public async Task<CrmLeadDistribution.AutoAssignDecision> TryAutoAssignNewCardAsync(
        CrmCandidateCardEntity card,
        string? actorUserId = null,
        string? actorName = null,
        CancellationToken ct = default)
    {
        if (card.IsClosed || card.ManagerUserId is not null)
        {
            return new CrmLeadDistribution.AutoAssignDecision(card.ManagerUserId, "Уже назначена или закрыта");
        }

        var managers = await LoadManagerCandidatesAsync(card.OfficeId, ct);
        var decision = CrmLeadDistribution.SelectManagerForNewLead(managers);
        if (decision.ManagerUserId is null)
        {
            return decision;
        }

        var now = DateTime.UtcNow;
        ApplyAssignment(card, decision.ManagerUserId, now);
        await TouchManagerLastAssignedAsync(card.OfficeId, decision.ManagerUserId, now, ct);
        AddAssignedHistory(
            card.Id,
            ReasonAuto,
            actorUserId ?? "system",
            actorName ?? "Система",
            now);
        return decision;
    }

    /// <summary>
    /// При старте смены: выдать менеджеру до freeSlots старейших unassigned.
    /// Не включает/выключает смену — только раздача из очереди.
    /// Не вызывает SaveChanges (caller решает).
    /// </summary>
    /// <returns>Сколько карточек выдано.</returns>
    public async Task<int> FillManagerFromQueueOnShiftStartAsync(
        Guid officeId,
        string managerUserId,
        int capacity,
        string actorUserId,
        string actorName,
        CancellationToken ct = default)
    {
        var load = await GetActiveLoadAsync(officeId, managerUserId, ct);
        var freeSlots = CrmLeadDistribution.FreeSlots(capacity, load);
        if (freeSlots <= 0)
        {
            return 0;
        }

        var queue = await db.CrmCandidateCards
            .Where(x => x.OfficeId == officeId
                        && x.ManagerUserId == null
                        && x.IsInActiveLoad
                        && !x.IsClosed)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new CrmLeadDistribution.QueueCard(x.Id, x.CreatedAtUtc))
            .Take(freeSlots)
            .ToListAsync(ct);

        var selectedIds = CrmLeadDistribution.SelectCardsForShiftStart(queue, freeSlots);
        if (selectedIds.Count == 0)
        {
            return 0;
        }

        var cards = await db.CrmCandidateCards
            .Where(x => selectedIds.Contains(x.Id))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var byId = cards.ToDictionary(x => x.Id);
        foreach (var id in selectedIds)
        {
            if (!byId.TryGetValue(id, out var card))
            {
                continue;
            }

            ApplyAssignment(card, managerUserId, now);
            AddAssignedHistory(card.Id, ReasonShiftStart, actorUserId, actorName, now);
        }

        await TouchManagerLastAssignedAsync(officeId, managerUserId, now, ct);
        return selectedIds.Count;
    }

    /// <summary>
    /// Ручное назначение (admin). Не вызывает SaveChanges.
    /// </summary>
    public void AssignManually(
        CrmCandidateCardEntity card,
        string managerUserId,
        string managerDisplayName,
        string actorUserId,
        string actorName,
        DateTime? atUtc = null)
    {
        var now = atUtc ?? DateTime.UtcNow;
        ApplyAssignment(card, managerUserId, now);
        if (card.IsClosed)
        {
            card.IsClosed = false;
            card.CloseReason = null;
            card.ClosedAtUtc = null;
        }

        AddAssignedHistory(card.Id, managerDisplayName, actorUserId, actorName, now);
    }

    public async Task<int> GetActiveLoadAsync(Guid officeId, string managerUserId, CancellationToken ct = default) =>
        await db.CrmCandidateCards.CountAsync(
            x => x.OfficeId == officeId
                 && x.ManagerUserId == managerUserId
                 && x.IsInActiveLoad
                 && !x.IsClosed,
            ct);

    public async Task<Dictionary<string, int>> GetActiveLoadsAsync(Guid officeId, CancellationToken ct = default)
    {
        var managerIds = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.ManagerUserId != null && x.IsInActiveLoad && !x.IsClosed)
            .Select(x => x.ManagerUserId!)
            .ToListAsync(ct);
        return managerIds
            .GroupBy(x => x, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    private async Task<List<CrmLeadDistribution.ManagerCandidate>> LoadManagerCandidatesAsync(
        Guid officeId,
        CancellationToken ct)
    {
        var managerUsers = await users.GetUsersInRoleAsync(PanelRoles.Manager);
        var ids = managerUsers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = await db.PanelUserProfiles
            .Where(x => x.OfficeId == officeId && ids.Contains(x.UserId))
            .ToListAsync(ct);
        var loads = await GetActiveLoadsAsync(officeId, ct);

        var now = DateTime.UtcNow;
        return profiles
            .Select(p => new CrmLeadDistribution.ManagerCandidate(
                p.UserId,
                p.CrmCapacity,
                loads.GetValueOrDefault(p.UserId),
                p.CrmLastAutoAssignmentAtUtc,
                CrmShiftRules.IsEffectivelyOnShift(p.CrmShiftActive, p.CrmShiftStartedAtUtc, now)))
            .ToList();
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
}
