using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Contracts;

namespace Orbita.Api.Services;

/// <summary>Independent manager CRM. It never changes Bitrix delivery state.</summary>
/// <remarks>
/// Распределение лидов между менеджерами — <see cref="CrmLeadDistributionService"/> /
/// <see cref="CrmLeadDistribution"/> (отдельный модуль).
/// </remarks>
public sealed class CrmWorkspaceService(
    OrbitaDbContext db,
    UserManager<IdentityUser> users,
    CrmLeadDistributionService leadDistribution)
{
    /// <summary>
    /// Create CRM card for a response in the target office (delivery path only).
    /// Does not call SaveChanges — caller owns the unit of work unless <paramref name="save"/> is true.
    /// </summary>
    public async Task<(Guid? CardId, string? Error)> TryCreateCardForDeliveryAsync(
        CandidateResponseEntity response,
        Guid officeId,
        bool save = true,
        CancellationToken ct = default)
    {
        if (response.IsLocalDuplicate)
        {
            return (null, "Локальный дубль — карточка CRM не создаётся.");
        }

        var office = await db.Offices.AsNoTracking()
            .Where(x => x.Id == officeId)
            .Select(x => new { x.CrmEnabled, x.IsEnabled, x.CrmStagesJson })
            .FirstOrDefaultAsync(ct);
        if (office is null || !office.IsEnabled)
        {
            return (null, "Офис не найден или отключён.");
        }

        if (!office.CrmEnabled)
        {
            return (null, "Офис не принимает отклики в CRM.");
        }

        var existing = await db.CrmCandidateCards
            .FirstOrDefaultAsync(x => x.ResponseId == response.Id, ct);
        if (existing is not null)
        {
            return (existing.Id, null);
        }

        var initialStage = CrmStages.Resolve(office.CrmStagesJson)[0];
        var now = DateTime.UtcNow;
        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = officeId,
            Stage = initialStage,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StageChangedAtUtc = now,
            IsInActiveLoad = true
        };
        db.CrmCandidateCards.Add(card);
        AddHistory(card.Id, "Created", "Карточка создана из отклика", "system", "Система", now);
        await leadDistribution.TryAutoAssignNewCardAsync(card, ct: ct);
        if (save)
        {
            await db.SaveChangesAsync(ct);
        }

        return (card.Id, null);
    }

    [Obsolete("Use TryCreateCardForDeliveryAsync — CRM cards are created only on delivery.")]
    public async Task CreateCardForResponseAsync(CandidateResponseEntity response, CancellationToken ct = default)
    {
        if (response.OfficeId is not Guid officeId)
        {
            return;
        }

        await TryCreateCardForDeliveryAsync(response, officeId, save: true, ct);
    }

    public async Task<CrmBoardDto?> GetBoardAsync(
        Guid officeId,
        string userId,
        bool isAdmin,
        CrmBoardQuery? query = null,
        CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == officeId, ct);
        if (office is null)
        {
            return null;
        }

        query ??= new CrmBoardQuery();
        var scope = NormalizeScope(query.Scope, isAdmin);
        var profile = await db.PanelUserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var managers = await GetManagersAsync(officeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var loads = await leadDistribution.GetActiveLoadsAsync(officeId, ct);
        var now = DateTime.UtcNow;

        var cardsQuery = db.CrmCandidateCards.AsNoTracking()
            .Include(x => x.Response)
            .Where(x => x.OfficeId == officeId);

        cardsQuery = scope switch
        {
            CrmBoardScopes.Unassigned => cardsQuery.Where(x => x.ManagerUserId == null && !x.IsClosed),
            CrmBoardScopes.Closed => cardsQuery.Where(x => x.IsClosed && (isAdmin || x.ManagerUserId == userId)),
            CrmBoardScopes.Team when isAdmin => cardsQuery.Where(x => !x.IsClosed || query.IncludeClosed),
            _ => cardsQuery.Where(x => x.ManagerUserId == userId && (!x.IsClosed || query.IncludeClosed))
        };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            cardsQuery = cardsQuery.Where(x =>
                x.Response.FullName.Contains(term) ||
                x.Response.PhoneRaw.Contains(term) ||
                x.Response.City.Contains(term) ||
                x.Response.Vacancy.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(query.City))
        {
            var city = query.City.Trim();
            cardsQuery = cardsQuery.Where(x => x.Response.City.Contains(city));
        }

        if (!string.IsNullOrWhiteSpace(query.Vacancy))
        {
            var vacancy = query.Vacancy.Trim();
            cardsQuery = cardsQuery.Where(x => x.Response.Vacancy.Contains(vacancy));
        }

        if (query.ActiveLoadOnly)
        {
            cardsQuery = cardsQuery.Where(x => x.IsInActiveLoad);
        }

        var cards = await cardsQuery
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(500)
            .ToListAsync(ct);

        var cardIds = cards.Select(x => x.Id).ToList();
        var openTasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.CardId != null && cardIds.Contains(x.CardId.Value) && x.Status == CrmTaskStatuses.Open)
            .Select(x => new { x.CardId, x.DueAtUtc })
            .ToListAsync(ct);
        var taskStats = openTasks
            .GroupBy(x => x.CardId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Overdue: g.Any(t => t.DueAtUtc is DateTime due && due < now)));

        if (query.OverdueOnly)
        {
            cards = cards.Where(x => taskStats.GetValueOrDefault(x.Id).Overdue
                || (x.NextActionAtUtc is DateTime next && next < now)).ToList();
        }

        var officeStages = CrmStages.Resolve(office.CrmStagesJson);
        var stageDtos = officeStages.Select(stage =>
        {
            var stageCards = cards.Where(x => x.Stage == stage && !x.IsClosed).Select(x =>
            {
                var stats = taskStats.GetValueOrDefault(x.Id);
                return ToCardDto(x, names, stats.Count, stats.Overdue, now);
            }).ToList();
            return new CrmStageDto(stage, stageCards, stageCards.Count);
        }).ToList();

        // Cards left on stages removed from the funnel stay visible until remapped.
        var knownStages = new HashSet<string>(officeStages, StringComparer.Ordinal);
        var orphanStages = cards
            .Where(x => !x.IsClosed && !knownStages.Contains(x.Stage))
            .Select(x => x.Stage)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (var orphan in orphanStages)
        {
            var stageCards = cards.Where(x => x.Stage == orphan && !x.IsClosed).Select(x =>
            {
                var stats = taskStats.GetValueOrDefault(x.Id);
                return ToCardDto(x, names, stats.Count, stats.Overdue, now);
            }).ToList();
            stageDtos.Add(new CrmStageDto(orphan, stageCards, stageCards.Count));
        }

        if (scope == CrmBoardScopes.Closed || query.IncludeClosed)
        {
            var closedCards = cards.Where(x => x.IsClosed).Select(x =>
            {
                var stats = taskStats.GetValueOrDefault(x.Id);
                return ToCardDto(x, names, stats.Count, stats.Overdue, now);
            }).ToList();
            if (closedCards.Count > 0)
            {
                stageDtos = stageDtos.Concat([new CrmStageDto("Закрыто", closedCards, closedCards.Count)]).ToList();
            }
        }

        var managerDtos = managers
            .Where(x => isAdmin || x.Profile.UserId == userId)
            .Select(x => new CrmManagerDto(
                x.Profile.UserId,
                x.Name,
                x.Profile.CrmShiftActive,
                x.Profile.CrmCapacity,
                loads.GetValueOrDefault(x.Profile.UserId)))
            .ToList();

        var dayStart = now.Date;
        var teamStats = new CrmTeamStatsDto(
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && !x.IsClosed, ct),
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && x.ManagerUserId == null && !x.IsClosed, ct),
            managers.Count(x => x.Profile.CrmShiftActive),
            managers.Count,
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && x.IsClosed && x.ClosedAtUtc >= dayStart, ct),
            await (
                from h in db.CrmCandidateHistory.AsNoTracking()
                join c in db.CrmCandidateCards.AsNoTracking() on h.CardId equals c.Id
                where c.OfficeId == officeId && h.Action == "Assigned" && h.CreatedAtUtc >= dayStart
                select h.Id).CountAsync(ct),
            (await db.CrmCandidateCards.AsNoTracking()
                .Where(x => x.OfficeId == officeId && !x.IsClosed)
                .Select(x => x.Stage)
                .ToListAsync(ct))
            .GroupBy(stage => stage)
            .Select(g => new CrmStageCountDto(g.Key, g.Count()))
            .ToList());

        var openTaskCount = await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId && x.Status == CrmTaskStatuses.Open && (isAdmin || x.AssigneeUserId == userId), ct);
        var overdueTaskCount = await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId
                 && x.Status == CrmTaskStatuses.Open
                 && x.DueAtUtc != null
                 && x.DueAtUtc < now
                 && (isAdmin || x.AssigneeUserId == userId), ct);

        return new CrmBoardDto(
            office.CrmEnabled,
            office.CrmRequireStageComment,
            profile?.CrmShiftActive == true,
            profile?.CrmCapacity ?? 10,
            loads.GetValueOrDefault(userId),
            stageDtos,
            isAdmin ? managers.Select(x => new CrmManagerDto(
                x.Profile.UserId,
                x.Name,
                x.Profile.CrmShiftActive,
                x.Profile.CrmCapacity,
                loads.GetValueOrDefault(x.Profile.UserId))).ToList() : managerDtos,
            teamStats.UnassignedCount,
            openTaskCount,
            overdueTaskCount,
            isAdmin,
            teamStats,
            scope,
            query.Search,
            query.City,
            query.Vacancy,
            query.OverdueOnly,
            query.ActiveLoadOnly,
            query.IncludeClosed,
            officeStages);
    }

    public async Task<bool> StartShiftAsync(Guid officeId, string userId, CancellationToken ct = default)
    {
        var profile = await GetManagerProfileAsync(officeId, userId, ct);
        if (profile is null)
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        profile.CrmShiftActive = true;
        var actorName = await ResolveDisplayNameAsync(userId, ct);
        await leadDistribution.FillManagerFromQueueOnShiftStartAsync(
            officeId,
            userId,
            profile.CrmCapacity,
            userId,
            actorName,
            ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> StopShiftAsync(Guid officeId, string userId, CancellationToken ct = default)
    {
        var profile = await GetManagerProfileAsync(officeId, userId, ct);
        if (profile is null)
        {
            return false;
        }

        profile.CrmShiftActive = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetCapacityAsync(Guid officeId, string managerUserId, int capacity, CancellationToken ct = default)
    {
        if (capacity is < 1 or > 100)
        {
            return false;
        }

        var profile = await GetManagerProfileAsync(officeId, managerUserId, ct);
        if (profile is null)
        {
            return false;
        }

        profile.CrmCapacity = capacity;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetOfficeSettingsAsync(Guid officeId, bool enabled, bool requireStageComment, CancellationToken ct = default)
    {
        var office = await db.Offices.FirstOrDefaultAsync(x => x.Id == officeId, ct);
        if (office is null)
        {
            return false;
        }

        office.CrmEnabled = enabled;
        office.CrmRequireStageComment = requireStageComment;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Ok, string? Error)> SetOfficeFunnelAsync(
        Guid officeId,
        IReadOnlyList<string> stages,
        string actorUserId,
        CancellationToken ct = default)
    {
        var normalized = CrmStages.Normalize(stages);
        if (normalized is null)
        {
            return (false, $"Укажите от {CrmStages.MinCount} до {CrmStages.MaxCount} уникальных этапов (до {CrmStages.MaxNameLength} символов).");
        }

        var office = await db.Offices.FirstOrDefaultAsync(x => x.Id == officeId, ct);
        if (office is null)
        {
            return (false, "Офис не найден.");
        }

        var nextStages = normalized.ToList();
        var nextSet = new HashSet<string>(nextStages, StringComparer.Ordinal);
        var fallback = nextStages[0];
        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);

        // Cards on removed stages move to the first stage of the new funnel.
        var openCards = await db.CrmCandidateCards
            .Where(x => x.OfficeId == officeId && !x.IsClosed)
            .ToListAsync(ct);
        foreach (var card in openCards.Where(x => !nextSet.Contains(x.Stage)))
        {
            var previousStage = card.Stage;
            card.Stage = fallback;
            card.StageChangedAtUtc = now;
            card.UpdatedAtUtc = now;
            AddHistory(
                card.Id,
                "StageChanged",
                $"{previousStage} → {fallback} (воронка обновлена)",
                actorUserId,
                actorName,
                now);
        }

        office.CrmStagesJson = CrmStages.Serialize(nextStages);
        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<CrmOfficeSettingsDto?> GetOfficeSettingsAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == officeId, ct);
        return office is null
            ? null
            : new CrmOfficeSettingsDto(office.CrmEnabled, office.CrmRequireStageComment, CrmStages.Resolve(office.CrmStagesJson));
    }

    public async Task<CrmCandidateDetailDto?> GetCardAsync(Guid cardId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards.AsNoTracking()
            .Include(x => x.Response)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null || (!isAdmin && card.ManagerUserId != userId))
        {
            return null;
        }

        var officeStagesJson = await db.Offices.AsNoTracking()
            .Where(x => x.Id == card.OfficeId)
            .Select(x => x.CrmStagesJson)
            .FirstOrDefaultAsync(ct);
        var officeStages = CrmStages.Resolve(officeStagesJson);
        var managers = await GetManagersAsync(card.OfficeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var loads = await leadDistribution.GetActiveLoadsAsync(card.OfficeId, ct);
        var now = DateTime.UtcNow;
        var notes = await db.CrmCandidateNotes.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderBy(x => x.Status)
            .ThenBy(x => x.DueAtUtc)
            .ToListAsync(ct);
        var history = await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var openCount = tasks.Count(x => x.Status == CrmTaskStatuses.Open);
        var hasOverdue = tasks.Any(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc is DateTime due && due < now);

        var activity = BuildActivity(notes, tasks, history, names);
        return new CrmCandidateDetailDto(
            ToCardDto(card, names, openCount, hasOverdue, now),
            notes.Select(x => new CrmNoteDto(x.Id, x.AuthorUserId, x.AuthorName, x.Text, x.CreatedAtUtc)).ToList(),
            tasks.Select(x => ToTaskDto(x, names, card.Response.FullName, now)).ToList(),
            history.Select(x => new CrmHistoryDto(x.Id, x.Action, x.Details, x.ActorUserId, x.ActorName, x.CreatedAtUtc)).ToList(),
            activity,
            managers.Select(x => new CrmManagerDto(
                x.Profile.UserId,
                x.Name,
                x.Profile.CrmShiftActive,
                x.Profile.CrmCapacity,
                loads.GetValueOrDefault(x.Profile.UserId))).ToList(),
            officeStages);
    }

    public async Task<IReadOnlyList<CrmTaskDto>> GetTasksAsync(Guid officeId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var managers = await GetManagersAsync(officeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.OfficeId == officeId && (isAdmin || x.AssigneeUserId == userId))
            .OrderBy(x => x.Status)
            .ThenBy(x => x.DueAtUtc)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Take(300)
            .ToListAsync(ct);

        var cardIds = tasks.Where(x => x.CardId is not null).Select(x => x.CardId!.Value).Distinct().ToList();
        var cardNames = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => cardIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Response.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, ct);

        return tasks.Select(x => ToTaskDto(
            x,
            names,
            x.CardId is Guid id ? cardNames.GetValueOrDefault(id) : null,
            now)).ToList();
    }

    public async Task<(bool Ok, string? Error)> MoveAsync(
        Guid cardId,
        string stage,
        string? comment,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }

        if (card.IsClosed)
        {
            return (false, "Карточка закрыта. Сначала верните её в работу.");
        }

        var officeMeta = await db.Offices.AsNoTracking()
            .Where(x => x.Id == card.OfficeId)
            .Select(x => new { x.CrmRequireStageComment, x.CrmStagesJson })
            .FirstOrDefaultAsync(ct);
        var officeStages = CrmStages.Resolve(officeMeta?.CrmStagesJson);
        if (!CrmStages.Contains(officeStages, stage))
        {
            return (false, "Неизвестный этап.");
        }

        if (officeMeta?.CrmRequireStageComment == true && string.IsNullOrWhiteSpace(comment))
        {
            return (false, "Нужен комментарий при смене этапа.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var previous = card.Stage;
        card.Stage = stage;
        card.StageChangedAtUtc = now;
        card.UpdatedAtUtc = now;
        card.LastContactAtUtc = now;
        AddHistory(card.Id, "StageChanged", $"{previous} → {stage}", actorUserId, actorName, now);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                AuthorUserId = actorUserId,
                AuthorName = actorName,
                Text = comment.Trim(),
                CreatedAtUtc = now
            });
            AddHistory(card.Id, "Note", "Комментарий к смене этапа", actorUserId, actorName, now);
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> AssignAsync(Guid cardId, string managerUserId, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        if (!isAdmin)
        {
            return false;
        }

        var card = await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null || await GetManagerProfileAsync(card.OfficeId, managerUserId, ct) is null)
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var managerName = await ResolveDisplayNameAsync(managerUserId, ct);
        leadDistribution.AssignManually(card, managerUserId, managerName, actorUserId, actorName);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetActiveLoadAsync(Guid cardId, bool isInActiveLoad, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        card.IsInActiveLoad = isInActiveLoad;
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, isInActiveLoad ? "ReturnedToLoad" : "RemovedFromLoad", null, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Ok, string? Error)> CloseAsync(
        Guid cardId,
        string reason,
        string? comment,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (!CrmCloseReasons.IsValid(reason))
        {
            return (false, "Неизвестная причина закрытия.");
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        card.IsClosed = true;
        card.CloseReason = reason;
        card.ClosedAtUtc = now;
        card.IsInActiveLoad = false;
        card.UpdatedAtUtc = now;
        card.LastContactAtUtc = now;
        AddHistory(card.Id, "Closed", reason, actorUserId, actorName, now);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                AuthorUserId = actorUserId,
                AuthorName = actorName,
                Text = comment.Trim(),
                CreatedAtUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        return (true, null);
    }

    public async Task<bool> ReopenAsync(Guid cardId, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        card.IsClosed = false;
        card.CloseReason = null;
        card.ClosedAtUtc = null;
        card.IsInActiveLoad = true;
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "Reopened", null, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddNoteAsync(Guid cardId, string text, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            AuthorUserId = actorUserId,
            AuthorName = actorName,
            Text = text.Trim(),
            CreatedAtUtc = now
        });
        card.LastContactAtUtc = now;
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "Note", null, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<CrmTaskDto?> CreateTaskAsync(
        Guid officeId,
        CrmTaskCreateRequest request,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || await GetManagerProfileAsync(officeId, request.AssigneeUserId, ct) is null)
        {
            return null;
        }

        string? candidateName = null;
        if (request.CardId is Guid cardId)
        {
            var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
            if (card is null)
            {
                return null;
            }

            candidateName = await db.CandidateResponses.AsNoTracking()
                .Where(x => x.Id == card.ResponseId)
                .Select(x => x.FullName)
                .FirstOrDefaultAsync(ct);
            card.LastContactAtUtc = DateTime.UtcNow;
            card.UpdatedAtUtc = DateTime.UtcNow;
            if (request.DueAtUtc is DateTime due && (card.NextActionAtUtc is null || due < card.NextActionAtUtc))
            {
                card.NextActionAtUtc = due;
            }
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var assigneeName = await ResolveDisplayNameAsync(request.AssigneeUserId, ct);
        var task = new CrmTaskEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            CardId = request.CardId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            AssigneeUserId = request.AssigneeUserId,
            CreatorUserId = actorUserId,
            CreatorName = actorName,
            DueAtUtc = request.DueAtUtc,
            CreatedAtUtc = now
        };
        db.CrmTasks.Add(task);
        if (request.CardId is Guid historyCardId)
        {
            AddHistory(historyCardId, "TaskCreated", task.Title, actorUserId, actorName, now);
        }

        await db.SaveChangesAsync(ct);
        return new CrmTaskDto(
            task.Id,
            task.CardId,
            candidateName,
            task.Title,
            task.Description,
            task.AssigneeUserId,
            assigneeName,
            task.CreatorUserId,
            actorName,
            task.DueAtUtc,
            task.Status,
            task.CreatedAtUtc,
            null,
            task.DueAtUtc is DateTime d && d < now);
    }

    public async Task<CrmTaskDto?> CreateFollowUpAsync(
        Guid cardId,
        int minutes,
        string? title,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (minutes is < 15 or > 60 * 24 * 30)
        {
            return null;
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return null;
        }

        var due = DateTime.UtcNow.AddMinutes(minutes);
        var label = title?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = minutes switch
            {
                <= 60 => "Перезвонить через час",
                <= 24 * 60 => $"Перезвонить через {minutes / 60} ч",
                _ => $"Перезвонить через {minutes / (24 * 60)} дн"
            };
        }

        return await CreateTaskAsync(
            card.OfficeId,
            new CrmTaskCreateRequest(cardId, label, null, actorUserId, due),
            actorUserId,
            isAdmin,
            ct);
    }

    public async Task<bool> CompleteTaskAsync(Guid taskId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || (!isAdmin && task.AssigneeUserId != userId))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        task.Status = CrmTaskStatuses.Completed;
        task.CompletedAtUtc = now;
        if (task.CardId is Guid cardId)
        {
            var actorName = await ResolveDisplayNameAsync(userId, ct);
            AddHistory(cardId, "TaskCompleted", task.Title, userId, actorName, now);
            var card = await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId, ct);
            if (card is not null)
            {
                card.LastContactAtUtc = now;
                card.UpdatedAtUtc = now;
                var next = await db.CrmTasks.AsNoTracking()
                    .Where(x => x.CardId == cardId && x.Status == CrmTaskStatuses.Open && x.Id != taskId && x.DueAtUtc != null)
                    .OrderBy(x => x.DueAtUtc)
                    .Select(x => x.DueAtUtc)
                    .FirstOrDefaultAsync(ct);
                card.NextActionAtUtc = next;
            }
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> CountOpenTasksAsync(Guid officeId, string userId, bool isAdmin, CancellationToken ct = default) =>
        await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId
                 && x.Status == CrmTaskStatuses.Open
                 && (isAdmin || x.AssigneeUserId == userId),
            ct);

    private void AddHistory(Guid cardId, string action, string? details, string actorUserId, string actorName, DateTime? at = null) =>
        db.CrmCandidateHistory.Add(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = action,
            Details = details,
            ActorUserId = actorUserId,
            ActorName = actorName,
            CreatedAtUtc = at ?? DateTime.UtcNow
        });

    private async Task<CrmCandidateCardEntity?> FindAccessibleCardAsync(Guid cardId, string userId, bool isAdmin, CancellationToken ct) =>
        await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId && (isAdmin || x.ManagerUserId == userId), ct);

    private async Task<PanelUserProfileEntity?> GetManagerProfileAsync(Guid officeId, string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await users.IsInRoleAsync(user, PanelRoles.Manager))
        {
            // Admin may set capacity / assign without manager role on some setups — still require profile.
            if (user is null || !await users.IsInRoleAsync(user, PanelRoles.Admin))
            {
                return null;
            }
        }

        return await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.OfficeId == officeId && x.UserId == userId, ct);
    }

    private async Task<List<(PanelUserProfileEntity Profile, string Name)>> GetManagersAsync(Guid officeId, CancellationToken ct)
    {
        var managerUsers = await users.GetUsersInRoleAsync(PanelRoles.Manager);
        var ids = managerUsers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = await db.PanelUserProfiles.Where(x => x.OfficeId == officeId && ids.Contains(x.UserId)).ToListAsync(ct);
        var names = managerUsers.ToDictionary(x => x.Id, x => DisplayName(x), StringComparer.Ordinal);
        return profiles.Select(x => (x, names.GetValueOrDefault(x.UserId, x.UserId))).ToList();
    }

    private async Task<string> ResolveDisplayNameAsync(string userId, CancellationToken ct)
    {
        if (userId is "system")
        {
            return "Система";
        }

        var user = await users.FindByIdAsync(userId);
        return user is null ? userId : DisplayName(user);
    }

    private static string DisplayName(IdentityUser user) =>
        user.Email ?? user.UserName ?? user.Id;

    private static string NormalizeScope(string? scope, bool isAdmin) =>
        scope switch
        {
            CrmBoardScopes.Team when isAdmin => CrmBoardScopes.Team,
            CrmBoardScopes.Unassigned when isAdmin => CrmBoardScopes.Unassigned,
            CrmBoardScopes.Closed => CrmBoardScopes.Closed,
            CrmBoardScopes.Team when !isAdmin => CrmBoardScopes.Mine,
            CrmBoardScopes.Unassigned when !isAdmin => CrmBoardScopes.Mine,
            _ => CrmBoardScopes.Mine
        };

    private static CrmCandidateCardDto ToCardDto(
        CrmCandidateCardEntity card,
        IReadOnlyDictionary<string, string> names,
        int openTaskCount,
        bool hasOverdue,
        DateTime now)
    {
        var stageAt = card.StageChangedAtUtc == default ? card.CreatedAtUtc : card.StageChangedAtUtc;
        var hours = Math.Max(0, (now - stageAt).TotalHours);
        return new(
            card.Id,
            card.ResponseId,
            card.Response.FullName,
            card.Response.Age,
            card.Response.PhoneRaw,
            card.Response.City,
            card.Response.Vacancy,
            card.Response.MessengerUrl,
            card.Stage,
            card.ManagerUserId,
            card.ManagerUserId is null ? null : names.GetValueOrDefault(card.ManagerUserId, card.ManagerUserId),
            card.IsInActiveLoad,
            card.CreatedAtUtc,
            stageAt,
            card.LastContactAtUtc,
            card.NextActionAtUtc,
            card.IsClosed,
            card.CloseReason,
            openTaskCount,
            hasOverdue,
            Math.Round(hours, 1));
    }

    private static CrmTaskDto ToTaskDto(
        CrmTaskEntity task,
        IReadOnlyDictionary<string, string> names,
        string? candidateName,
        DateTime now) =>
        new(
            task.Id,
            task.CardId,
            candidateName,
            task.Title,
            task.Description,
            task.AssigneeUserId,
            names.GetValueOrDefault(task.AssigneeUserId, task.AssigneeUserId),
            task.CreatorUserId,
            string.IsNullOrWhiteSpace(task.CreatorName) ? names.GetValueOrDefault(task.CreatorUserId, task.CreatorUserId) : task.CreatorName,
            task.DueAtUtc,
            task.Status,
            task.CreatedAtUtc,
            task.CompletedAtUtc,
            task.Status == CrmTaskStatuses.Open && task.DueAtUtc is DateTime due && due < now);

    private static IReadOnlyList<CrmActivityItemDto> BuildActivity(
        IReadOnlyList<CrmCandidateNoteEntity> notes,
        IReadOnlyList<CrmTaskEntity> tasks,
        IReadOnlyList<CrmCandidateHistoryEntity> history,
        IReadOnlyDictionary<string, string> names)
    {
        var items = new List<CrmActivityItemDto>();
        items.AddRange(notes.Select(n => new CrmActivityItemDto(
            "note", "Комментарий", n.Text, n.AuthorName, n.CreatedAtUtc)));
        items.AddRange(tasks.Select(t => new CrmActivityItemDto(
            t.Status == CrmTaskStatuses.Completed ? "task-done" : "task",
            t.Title,
            t.Description,
            string.IsNullOrWhiteSpace(t.CreatorName) ? names.GetValueOrDefault(t.CreatorUserId, t.CreatorUserId) : t.CreatorName,
            t.CompletedAtUtc ?? t.CreatedAtUtc,
            t.Id)));
        items.AddRange(history
            .Where(h => h.Action is not "Note" and not "TaskCreated" and not "TaskCompleted")
            .Select(h => new CrmActivityItemDto(
                "history",
                MapHistoryTitle(h.Action),
                h.Details,
                h.ActorName,
                h.CreatedAtUtc)));
        return items.OrderByDescending(x => x.AtUtc).Take(80).ToList();
    }

    private static string MapHistoryTitle(string action) => action switch
    {
        "Created" => "Карточка создана",
        "Assigned" => "Назначен ответственный",
        "StageChanged" => "Смена этапа",
        "ReturnedToLoad" => "Вернули в нагрузку",
        "RemovedFromLoad" => "Сняли с нагрузки",
        "Closed" => "Карточка закрыта",
        "Reopened" => "Карточка открыта снова",
        _ => action
    };
}
