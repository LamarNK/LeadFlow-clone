using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
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
    CrmLeadDistributionService leadDistribution,
    IPanelRealtimeNotifier? panelRealtime = null,
    CrmTaskAttachmentStorageService? taskAttachments = null,
    CrmDeadlineNotificationService? deadlineNotifications = null,
    PhoneNormalizer? phoneNormalizer = null,
    CandidateParser? candidateParser = null,
    CandidatePersonPhoneService? personPhone = null,
    ICrmNotificationRealtimeNotifier? crmNotificationRealtime = null)
{
    private readonly PhoneNormalizer _phoneNormalizer = phoneNormalizer ?? new PhoneNormalizer();
    private readonly CandidateParser _candidateParser = candidateParser ?? new CandidateParser();
    private readonly CandidatePersonPhoneService _personPhone = personPhone ?? new CandidatePersonPhoneService(db);
    private readonly List<(string RecipientUserId, CrmTaskNotificationDto Dto)> _pendingPhoneRealtime = [];
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

        // Самовосстановление UI: забытый «Стоп» не должен показывать «На смене» сутками.
        await ExpireStaleShiftsAsync(ct);

        query ??= new CrmBoardQuery();
        // isAdmin here means elevated office access (Admin / OfficeLead / SeniorManager), not only global admin.
        var scope = NormalizeScope(query.Scope);
        if (!isAdmin && scope is CrmBoardScopes.Team or CrmBoardScopes.Unassigned)
        {
            // Manager: only own leads — force Mine (Team / queue are elevated-only).
            scope = CrmBoardScopes.Mine;
        }

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
            // Elevated: all closed in office. Manager: only own closed.
            CrmBoardScopes.Closed => isAdmin
                ? cardsQuery.Where(x => x.IsClosed)
                : cardsQuery.Where(x => x.IsClosed && x.ManagerUserId == userId),
            CrmBoardScopes.Team => cardsQuery.Where(x => !x.IsClosed || query.IncludeClosed),
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

        var chatUnreadByCard = await LoadChatUnreadCountsAsync(cardIds, userId, cards, ct);

        if (query.OverdueOnly)
        {
            cards = cards.Where(x => taskStats.GetValueOrDefault(x.Id).Overdue
                || (x.NextActionAtUtc is DateTime next && next < now)).ToList();
        }

        CrmCandidateCardDto MapCard(CrmCandidateCardEntity x)
        {
            var stats = taskStats.GetValueOrDefault(x.Id);
            return ToCardDto(x, names, stats.Count, stats.Overdue, now, chatUnreadByCard.GetValueOrDefault(x.Id));
        }

        var officeStages = CrmStages.Resolve(office.CrmStagesJson);
        var stageDtos = officeStages.Select(stage =>
        {
            var stageCards = cards.Where(x => x.Stage == stage && !x.IsClosed).Select(MapCard).ToList();
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
            var stageCards = cards.Where(x => x.Stage == orphan && !x.IsClosed).Select(MapCard).ToList();
            stageDtos.Add(new CrmStageDto(orphan, stageCards, stageCards.Count));
        }

        if (scope == CrmBoardScopes.Closed || query.IncludeClosed)
        {
            var closedCards = cards.Where(x => x.IsClosed).Select(MapCard).ToList();
            if (closedCards.Count > 0)
            {
                stageDtos = stageDtos.Concat([new CrmStageDto("Закрыто", closedCards, closedCards.Count)]).ToList();
            }
        }

        var shiftMeta = await LoadManagerShiftMetaAsync(
            officeId,
            managers.Select(x => x.Profile.UserId),
            ct);
        CrmManagerDto MapManager((PanelUserProfileEntity Profile, string Name) m) =>
            ToManagerDto(m, loads, now, shiftMeta);

        var allManagerDtos = managers.Select(MapManager).ToList();
        var managerDtos = allManagerDtos
            .Where(x => isAdmin || x.UserId == userId)
            .ToList();

        var dayStart = now.Date;
        var teamStats = new CrmTeamStatsDto(
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && !x.IsClosed, ct),
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && x.ManagerUserId == null && !x.IsClosed, ct),
            managers.Count(x => IsOnShift(x.Profile, now)),
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
            profile is not null && IsOnShift(profile, now),
            profile?.CrmCapacity ?? 10,
            loads.GetValueOrDefault(userId),
            stageDtos,
            isAdmin ? allManagerDtos : managerDtos,
            teamStats.UnassignedCount,
            openTaskCount,
            overdueTaskCount,
            isAdmin,
            isAdmin || scope == CrmBoardScopes.Mine,
            teamStats,
            scope,
            query.Search,
            query.City,
            query.Vacancy,
            query.OverdueOnly,
            query.ActiveLoadOnly,
            query.IncludeClosed,
            officeStages,
            office.CrmDeadlineNotificationsEnabled);
    }

    public async Task<bool> StartShiftAsync(Guid officeId, string userId, CancellationToken ct = default)
    {
        var profile = await GetManagerProfileAsync(officeId, userId, ct);
        if (profile is null)
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow;
        // Редкий повторный старт / «зависшая» open-запись — закрываем перед новой.
        await CloseOpenShiftsAsync(
            officeId,
            userId,
            now,
            CrmShiftEndReasons.Superseded,
            endedByUserId: userId,
            fallbackStartedAtUtc: profile.CrmShiftStartedAtUtc,
            ct);
        OpenShiftRecord(officeId, userId, now);
        profile.CrmShiftActive = true;
        profile.CrmShiftStartedAtUtc = now;
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
        NotifyBoardChanged(officeId);
        return true;
    }

    public async Task<bool> StopShiftAsync(Guid officeId, string userId, CancellationToken ct = default)
    {
        var profile = await GetManagerProfileAsync(officeId, userId, ct);
        if (profile is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        await CloseOpenShiftsAsync(
            officeId,
            userId,
            now,
            CrmShiftEndReasons.Manual,
            endedByUserId: userId,
            fallbackStartedAtUtc: profile.CrmShiftStartedAtUtc,
            ct);
        profile.CrmShiftActive = false;
        profile.CrmShiftStartedAtUtc = null;
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(officeId);
        return true;
    }

    /// <summary>
    /// Снять CRM-смены, у которых вышел <see cref="CrmShiftRules.MaxDuration"/>
    /// или нет времени старта (legacy «зависшие» смены).
    /// </summary>
    /// <returns>Сколько профилей закрыто.</returns>
    public async Task<int> ExpireStaleShiftsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = await db.PanelUserProfiles
            .Where(x => x.CrmShiftActive)
            .ToListAsync(ct);
        if (active.Count == 0)
        {
            return 0;
        }

        var expiredOfficeIds = new HashSet<Guid>();
        var count = 0;
        foreach (var profile in active)
        {
            if (!CrmShiftRules.ShouldAutoStop(profile.CrmShiftActive, profile.CrmShiftStartedAtUtc, now))
            {
                continue;
            }

            if (profile.OfficeId is Guid officeId)
            {
                await CloseOpenShiftsAsync(
                    officeId,
                    profile.UserId,
                    now,
                    CrmShiftRules.ResolveAutoEndReason(profile.CrmShiftStartedAtUtc),
                    endedByUserId: null,
                    fallbackStartedAtUtc: profile.CrmShiftStartedAtUtc,
                    ct);
                expiredOfficeIds.Add(officeId);
            }

            profile.CrmShiftActive = false;
            profile.CrmShiftStartedAtUtc = null;
            count++;
        }

        if (count == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct);
        foreach (var officeId in expiredOfficeIds)
        {
            NotifyBoardChanged(officeId);
        }

        return count;
    }

    private void OpenShiftRecord(Guid officeId, string managerUserId, DateTime startedAtUtc)
    {
        db.CrmManagerShifts.Add(new CrmManagerShiftEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            ManagerUserId = managerUserId,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = null,
            EndReason = null,
            EndedByUserId = null
        });
    }

    /// <summary>
    /// Закрыть открытые записи истории; если их нет, но есть fallback-старт — создать
    /// закрытую запись (для сидов / legacy-профилей без history-row).
    /// </summary>
    private async Task CloseOpenShiftsAsync(
        Guid officeId,
        string managerUserId,
        DateTime endedAtUtc,
        string endReason,
        string? endedByUserId,
        DateTime? fallbackStartedAtUtc,
        CancellationToken ct)
    {
        var open = await db.CrmManagerShifts
            .Where(x => x.OfficeId == officeId
                        && x.ManagerUserId == managerUserId
                        && x.EndedAtUtc == null)
            .ToListAsync(ct);

        if (open.Count > 0)
        {
            foreach (var shift in open)
            {
                shift.EndedAtUtc = endedAtUtc < shift.StartedAtUtc ? shift.StartedAtUtc : endedAtUtc;
                shift.EndReason = endReason;
                shift.EndedByUserId = endedByUserId;
            }

            return;
        }

        // Нет open-row: профиль был «на смене» без истории (seed/legacy) — фиксируем факт для аналитики.
        if (fallbackStartedAtUtc is null && endReason is CrmShiftEndReasons.Superseded)
        {
            return;
        }

        var started = fallbackStartedAtUtc ?? endedAtUtc;
        if (started > endedAtUtc)
        {
            started = endedAtUtc;
        }

        db.CrmManagerShifts.Add(new CrmManagerShiftEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            ManagerUserId = managerUserId,
            StartedAtUtc = started,
            EndedAtUtc = endedAtUtc,
            EndReason = endReason,
            EndedByUserId = endedByUserId
        });
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
        NotifyBoardChanged(officeId);
        return true;
    }

    public async Task<bool> SetOfficeSettingsAsync(
        Guid officeId,
        bool enabled,
        bool requireStageComment,
        bool? deadlineNotificationsEnabled = null,
        CancellationToken ct = default)
    {
        var office = await db.Offices.FirstOrDefaultAsync(x => x.Id == officeId, ct);
        if (office is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        office.CrmEnabled = enabled;
        office.CrmRequireStageComment = requireStageComment;
        var nextDeadlineNotificationsEnabled = enabled
            && (deadlineNotificationsEnabled ?? office.CrmDeadlineNotificationsEnabled);
        if (nextDeadlineNotificationsEnabled != office.CrmDeadlineNotificationsEnabled)
        {
            office.CrmDeadlineNotificationsEnabled = nextDeadlineNotificationsEnabled;
            office.CrmDeadlineNotificationsEnabledAtUtc = nextDeadlineNotificationsEnabled ? now : null;
            if (!nextDeadlineNotificationsEnabled && deadlineNotifications is not null)
            {
                await deadlineNotifications.DismissOfficeAsync(officeId, now, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(officeId);
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
        NotifyBoardChanged(officeId);
        return (true, null);
    }

    public async Task<CrmOfficeSettingsDto?> GetOfficeSettingsAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await db.Offices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == officeId, ct);
        return office is null
            ? null
            : new CrmOfficeSettingsDto(
                office.CrmEnabled,
                office.CrmRequireStageComment,
                CrmStages.Resolve(office.CrmStagesJson),
                office.CrmDeadlineNotificationsEnabled);
    }

    public async Task<CrmCandidateDetailDto?> GetCardAsync(Guid cardId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards.AsNoTracking()
            .Include(x => x.Response)
                .ThenInclude(x => x.Person)
                    .ThenInclude(x => x.PhoneHistory)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null)
        {
            return null;
        }

        // Elevated or assigned owner only — managers cannot open foreign cards (read or write).
        if (!await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct))
        {
            return null;
        }

        // Elevated (same office / global admin) or assigned owner may edit.
        var canEdit = isAdmin || string.Equals(card.ManagerUserId, userId, StringComparison.Ordinal);

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
            .OrderBy(x => x.Status == CrmTaskStatuses.Open ? 0 : x.Status == CrmTaskStatuses.Completed ? 1 : 2)
            .ThenBy(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc != null && x.DueAtUtc < now ? 0 : 1)
            .ThenBy(x => x.DueAtUtc ?? DateTime.MaxValue)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var history = await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var openCount = tasks.Count(x => x.Status == CrmTaskStatuses.Open);
        var hasOverdue = tasks.Any(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc is DateTime due && due < now);

        var activity = BuildActivity(notes, tasks, history, names);
        var chat = ParseChatMessages(card.Response.ChatMessagesJson);
        var phoneHistory = BuildPhoneHistory(card.Response);
        var contactPhones = await LoadContactPhonesAsync(card.Response, ct);
        var chatHash = ComputeChatContentHash(card.Response.ChatMessagesJson);
        var chatRead = await db.CrmCardChatReads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CardId == cardId && x.UserId == userId, ct);
        var chatUnread = chat.Count > 0
            && (chatRead is null || !string.Equals(chatRead.ContentHash, chatHash, StringComparison.Ordinal))
            ? chat.Count
            : 0;
        return new CrmCandidateDetailDto(
            ToCardDto(card, names, openCount, hasOverdue, now, chatUnread),
            notes.Select(x => new CrmNoteDto(x.Id, x.AuthorUserId, x.AuthorName, x.Text, x.CreatedAtUtc)).ToList(),
            tasks.Select(x => ToTaskDto(x, names, card.Response.FullName, now)).ToList(),
            history.Select(x => new CrmHistoryDto(x.Id, x.Action, x.Details, x.ActorUserId, x.ActorName, x.CreatedAtUtc)).ToList(),
            activity,
            managers.Select(x => ToManagerDto(x, loads, now)).ToList(),
            officeStages,
            canEdit,
            chat,
            phoneHistory,
            contactPhones,
            chatUnread);
    }

    public async Task<ResponseAvatarFile?> GetCardAvatarAsync(
        Guid cardId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        // Read the image only after access is confirmed and only for the requested card.
        var card = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.Id == cardId)
            .Select(x => new { x.OfficeId, x.ManagerUserId, x.ResponseId })
            .FirstOrDefaultAsync(ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct))
        {
            return null;
        }

        var image = await db.CandidateResponses.AsNoTracking()
            .Where(x => x.Id == card.ResponseId)
            .Select(x => x.AvatarImage)
            .FirstOrDefaultAsync(ct);
        if (image is not { Length: > 0 })
        {
            return null;
        }

        var contentType = CandidateResponseAvatar.DetectContentType(image);
        return string.IsNullOrEmpty(contentType)
            ? null
            : new ResponseAvatarFile(image, contentType);
    }

    public async Task<IReadOnlyList<CrmTaskDto>> GetTasksAsync(Guid officeId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var managers = await GetManagersAsync(officeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.OfficeId == officeId && (isAdmin || x.AssigneeUserId == userId || x.CreatorUserId == userId))
            .OrderBy(x => x.Status == CrmTaskStatuses.Open ? 0 : x.Status == CrmTaskStatuses.Completed ? 1 : 2)
            .ThenBy(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc != null && x.DueAtUtc < now ? 0 : 1)
            .ThenBy(x => x.DueAtUtc ?? DateTime.MaxValue)
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
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<bool> AssignAsync(Guid cardId, string managerUserId, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        if (!isAdmin)
        {
            return false;
        }

        var card = await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isElevated: true, ct)
            || await GetManagerProfileAsync(card.OfficeId, managerUserId, ct) is null)
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var managerName = await ResolveDisplayNameAsync(managerUserId, ct);
        leadDistribution.AssignManually(card, managerUserId, managerName, actorUserId, actorName);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        NotifyBoardChanged(card.OfficeId);
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
        NotifyBoardChanged(card.OfficeId);
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

        if (string.IsNullOrWhiteSpace(comment))
        {
            return (false, "При закрытии сделки обязателен комментарий с причиной и деталями.");
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var commentText = comment.Trim();
        card.IsClosed = true;
        card.CloseReason = reason;
        card.ClosedAtUtc = now;
        card.IsInActiveLoad = false;
        card.UpdatedAtUtc = now;
        card.LastContactAtUtc = now;
        AddHistory(card.Id, "Closed", reason, actorUserId, actorName, now);
        db.CrmCandidateNotes.Add(new CrmCandidateNoteEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            AuthorUserId = actorUserId,
            AuthorName = actorName,
            Text = commentText,
            CreatedAtUtc = now
        });

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
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
        NotifyBoardChanged(card.OfficeId);
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
        NotifyBoardChanged(card.OfficeId);
        return true;
    }

    public async Task<(bool Ok, string? Error)> UpdateCardAsync(
        Guid cardId,
        CrmCardUpdateRequest request,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        const int minAge = 14;
        const int maxAge = 99;

        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
                .ThenInclude(x => x.Person)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isAdmin, ct))
        {
            return (false, "Карточка не найдена.");
        }

        var fullName = (request.FullName ?? string.Empty).Trim();
        if (fullName.Length == 0)
        {
            return (false, "Укажите ФИО кандидата.");
        }

        if (fullName.Length > 256)
        {
            return (false, "ФИО слишком длинное.");
        }

        var phoneRaw = (request.PhoneRaw ?? string.Empty).Trim();
        var phoneNormalized = _phoneNormalizer.Normalize(phoneRaw);
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return (false, "Укажите корректный телефон.");
        }

        var city = (request.City ?? string.Empty).Trim();
        if (city.Length > 256)
        {
            return (false, "Город слишком длинный.");
        }

        var vacancy = (request.Vacancy ?? string.Empty).Trim();
        if (vacancy.Length > 512)
        {
            return (false, "Вакансия слишком длинная.");
        }

        int? age = request.Age;
        if (age is int ageValue && ageValue is < minAge or > maxAge)
        {
            return (false, $"Возраст должен быть от {minAge} до {maxAge}.");
        }

        if (age is null || age < minAge)
        {
            age = null;
        }

        string Clamp(string? value, int max) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];

        var sourceResponseId = Clamp(request.SourceResponseId, 128);
        var accountName = Clamp(request.AccountName, 256);
        var sourceUrl = Clamp(request.SourceUrl, 1024);
        var vacancyUrl = Clamp(request.VacancyUrl, 1024);
        var messengerUrl = Clamp(request.MessengerUrl, 1024);

        var response = card.Response;
        var (firstName, lastName, middleName) = _candidateParser.ParseName(fullName);
        var changes = new List<string>();
        void Track(string label, string? before, string? after)
        {
            before ??= string.Empty;
            after ??= string.Empty;
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                changes.Add(label);
            }
        }

        Track("ФИО", response.FullName, fullName);
        Track("телефон", response.PhoneRaw, phoneRaw);
        Track("город", response.City, city);
        Track("вакансия", response.Vacancy, vacancy);
        Track("возраст", response.Age?.ToString(CultureInfo.InvariantCulture), age?.ToString(CultureInfo.InvariantCulture));
        Track("ID отклика", response.SourceResponseId, sourceResponseId);
        Track("источник", response.AccountName, accountName);
        Track("ссылка", response.SourceUrl, sourceUrl);
        Track("объявление", response.VacancyUrl, vacancyUrl);
        Track("мессенджер", response.MessengerUrl, messengerUrl);

        var phoneChanged = !string.Equals(response.PhoneNormalized, phoneNormalized, StringComparison.Ordinal);

        response.FullName = fullName;
        response.FirstName = firstName;
        response.LastName = lastName;
        response.MiddleName = middleName;
        response.Age = age;
        response.City = city;
        response.Vacancy = vacancy;
        response.SourceResponseId = sourceResponseId;
        response.AccountName = accountName;
        response.SourceUrl = sourceUrl;
        response.VacancyUrl = vacancyUrl;
        response.MessengerUrl = messengerUrl;

        if (phoneChanged)
        {
            response.PreviousPhoneRaw = response.PhoneRaw;
            response.PreviousPhoneNormalized = response.PhoneNormalized;
            response.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
            response.PhoneChangedAtUtc = DateTime.UtcNow;
            response.PhoneRaw = phoneRaw;
            response.PhoneNormalized = phoneNormalized;
        }
        else if (!string.Equals(response.PhoneRaw, phoneRaw, StringComparison.Ordinal))
        {
            response.PhoneRaw = phoneRaw;
        }

        var person = response.Person;
        if (person is not null)
        {
            person.FullName = fullName;
            person.FirstName = firstName;
            person.LastName = lastName;
            person.MiddleName = middleName;
            person.Age = age;
            person.City = city;
            person.UpdatedAtUtc = DateTime.UtcNow;
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        card.UpdatedAtUtc = now;
        if (changes.Count > 0)
        {
            AddHistory(
                card.Id,
                "CardUpdated",
                string.Join(", ", changes),
                actorUserId,
                actorName,
                now);
        }

        if (phoneChanged && person is not null)
        {
            await SyncPrimaryContactPhoneAsync(person.Id, phoneRaw, phoneNormalized, actorUserId, now, ct);
            await _personPhone.ApplyPhoneFromResponseAsync(
                person,
                phoneRaw,
                phoneNormalized,
                response.Id,
                ct,
                save: false);
            await QueuePhoneChangedAlertAsync(
                card,
                actorUserId,
                response.PreviousPhoneRaw,
                phoneRaw,
                now,
                ct);
        }

        await db.SaveChangesAsync(ct);
        if (phoneChanged)
        {
            await FlushPhoneChangedRealtimeAsync(ct);
        }

        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(CrmContactPhoneDto? Phone, string? Error)> AddContactPhoneAsync(
        Guid cardId,
        CrmContactPhoneCreateRequest request,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
                .ThenInclude(x => x.Person)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isAdmin, ct))
        {
            return (null, "Карточка не найдена.");
        }

        var phoneRaw = (request.PhoneRaw ?? string.Empty).Trim();
        var phoneNormalized = _phoneNormalizer.Normalize(phoneRaw);
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return (null, "Укажите корректный телефон.");
        }

        var personId = card.Response.PersonId;
        await EnsureContactPhonesSeededAsync(card, actorUserId, ct);

        if (await db.CandidateContactPhones.AnyAsync(
                x => x.PersonId == personId && x.PhoneNormalized == phoneNormalized, ct))
        {
            return (null, "Такой номер уже есть у кандидата.");
        }

        var count = await db.CandidateContactPhones.CountAsync(x => x.PersonId == personId, ct);
        if (count >= CrmContactPhoneLimits.MaxPhonesPerPerson)
        {
            return (null, $"Можно добавить не больше {CrmContactPhoneLimits.MaxPhonesPerPerson} номеров.");
        }

        var now = DateTime.UtcNow;
        var setPrimary = request.SetAsPrimary;
        string? previousPrimary = null;
        if (setPrimary)
        {
            previousPrimary = card.Response.PhoneRaw;
            await ClearPrimaryContactFlagsAsync(personId, ct);
            card.Response.PreviousPhoneRaw = card.Response.PhoneRaw;
            card.Response.PreviousPhoneNormalized = card.Response.PhoneNormalized;
            card.Response.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
            card.Response.PhoneChangedAtUtc = now;
            card.Response.PhoneRaw = phoneRaw;
            card.Response.PhoneNormalized = phoneNormalized;
            if (card.Response.Person is not null)
            {
                await _personPhone.ApplyPhoneFromResponseAsync(
                    card.Response.Person,
                    phoneRaw,
                    phoneNormalized,
                    card.Response.Id,
                    ct,
                    save: false);
            }

            await QueuePhoneChangedAlertAsync(card, actorUserId, previousPrimary, phoneRaw, now, ct);
        }

        var entity = new CandidateContactPhoneEntity
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            IsPrimary = setPrimary,
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId
        };
        db.CandidateContactPhones.Add(entity);
        card.UpdatedAtUtc = now;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        AddHistory(card.Id, "PhoneAdded", phoneRaw, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        if (setPrimary)
        {
            await FlushPhoneChangedRealtimeAsync(ct);
        }

        NotifyBoardChanged(card.OfficeId);
        return (new CrmContactPhoneDto(entity.Id, entity.PhoneRaw, entity.PhoneNormalized, entity.IsPrimary, entity.Label, entity.CreatedAtUtc), null);
    }

    public async Task<(bool Ok, string? Error)> RemoveContactPhoneAsync(
        Guid cardId,
        Guid phoneId,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isAdmin, ct))
        {
            return (false, "Карточка не найдена.");
        }

        var phone = await db.CandidateContactPhones
            .FirstOrDefaultAsync(x => x.Id == phoneId && x.PersonId == card.Response.PersonId, ct);
        if (phone is null)
        {
            return (false, "Номер не найден.");
        }

        if (phone.IsPrimary || string.Equals(phone.PhoneNormalized, card.Response.PhoneNormalized, StringComparison.Ordinal))
        {
            return (false, "Нельзя удалить основной номер. Сначала назначьте другой основным.");
        }

        db.CandidateContactPhones.Remove(phone);
        var now = DateTime.UtcNow;
        card.UpdatedAtUtc = now;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        AddHistory(card.Id, "PhoneRemoved", phone.PhoneRaw, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetPrimaryContactPhoneAsync(
        Guid cardId,
        Guid phoneId,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
                .ThenInclude(x => x.Person)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isAdmin, ct))
        {
            return (false, "Карточка не найдена.");
        }

        var phone = await db.CandidateContactPhones
            .FirstOrDefaultAsync(x => x.Id == phoneId && x.PersonId == card.Response.PersonId, ct);
        if (phone is null)
        {
            return (false, "Номер не найден.");
        }

        if (string.Equals(card.Response.PhoneNormalized, phone.PhoneNormalized, StringComparison.Ordinal))
        {
            await ClearPrimaryContactFlagsAsync(card.Response.PersonId, ct);
            phone.IsPrimary = true;
            await db.SaveChangesAsync(ct);
            return (true, null);
        }

        var now = DateTime.UtcNow;
        var previousRaw = card.Response.PhoneRaw;
        await ClearPrimaryContactFlagsAsync(card.Response.PersonId, ct);
        phone.IsPrimary = true;
        card.Response.PreviousPhoneRaw = card.Response.PhoneRaw;
        card.Response.PreviousPhoneNormalized = card.Response.PhoneNormalized;
        card.Response.PhoneMetricKind = ResponsePhoneMetricKinds.PhoneChanged;
        card.Response.PhoneChangedAtUtc = now;
        card.Response.PhoneRaw = phone.PhoneRaw;
        card.Response.PhoneNormalized = phone.PhoneNormalized;
        card.UpdatedAtUtc = now;

        if (card.Response.Person is not null)
        {
            await _personPhone.ApplyPhoneFromResponseAsync(
                card.Response.Person,
                phone.PhoneRaw,
                phone.PhoneNormalized,
                card.Response.Id,
                ct,
                save: false);
        }

        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        AddHistory(card.Id, "PhonePrimary", phone.PhoneRaw, actorUserId, actorName, now);
        await QueuePhoneChangedAlertAsync(card, actorUserId, previousRaw, phone.PhoneRaw, now, ct);
        await db.SaveChangesAsync(ct);
        await FlushPhoneChangedRealtimeAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<bool> MarkChatReadAsync(Guid cardId, string userId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards.AsNoTracking()
            .Include(x => x.Response)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null
            || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct))
        {
            return false;
        }

        var hash = ComputeChatContentHash(card.Response.ChatMessagesJson);
        var now = DateTime.UtcNow;
        var existing = await db.CrmCardChatReads
            .FirstOrDefaultAsync(x => x.CardId == cardId && x.UserId == userId, ct);
        if (existing is null)
        {
            db.CrmCardChatReads.Add(new CrmCardChatReadEntity
            {
                CardId = cardId,
                UserId = userId,
                LastReadAtUtc = now,
                ContentHash = hash
            });
        }
        else
        {
            existing.LastReadAtUtc = now;
            existing.ContentHash = hash;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(Guid? CardId, string? Error)> CreateManualCardAsync(
        Guid officeId,
        CrmManualCardCreateRequest request,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var fullName = (request.FullName ?? string.Empty).Trim();
        if (fullName.Length == 0)
        {
            return (null, "Укажите ФИО кандидата.");
        }

        var phoneRaw = (request.PhoneRaw ?? string.Empty).Trim();
        var phoneNormalized = _phoneNormalizer.Normalize(phoneRaw);
        if (string.IsNullOrWhiteSpace(phoneNormalized))
        {
            return (null, "Укажите корректный телефон.");
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
            return (null, "CRM выключена для офиса.");
        }

        if (!isAdmin)
        {
            var profileOffice = await db.PanelUserProfiles.AsNoTracking()
                .Where(x => x.UserId == actorUserId)
                .Select(x => x.OfficeId)
                .FirstOrDefaultAsync(ct);
            if (profileOffice != officeId)
            {
                return (null, "Нет доступа к офису.");
            }
        }

        var stages = CrmStages.Resolve(office.CrmStagesJson);
        var stage = string.IsNullOrWhiteSpace(request.Stage) ? stages[0] : request.Stage.Trim();
        if (!CrmStages.Contains(stages, stage))
        {
            return (null, "Неизвестный этап.");
        }

        var sourceResponseId = string.IsNullOrWhiteSpace(request.SourceResponseId)
            ? $"manual-{Guid.NewGuid():N}"
            : request.SourceResponseId.Trim();

        // Same AccountId (Empty) + SourceResponseId is unique in DB — fail early with a clear message.
        var sourceTaken = await db.CandidateResponses.AsNoTracking()
            .AnyAsync(
                x => x.AccountId == Guid.Empty
                     && x.SourceResponseId == sourceResponseId
                     && x.SourceResponseId != string.Empty,
                ct);
        if (sourceTaken)
        {
            return (null, "Отклик с таким ID уже существует. Укажите другой ID отклика.");
        }

        // Soft signal: existing CRM card for same phone in office (do not block — manager may still create).
        var existingCardId = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == officeId && x.Response.PhoneNormalized == phoneNormalized)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (existingCardId is Guid existingId)
        {
            // Prefer linking managers to the existing card rather than silent duplicates.
            return (null, $"Кандидат с этим телефоном уже есть в CRM. Откройте карточку: {existingId:D}");
        }

        var now = DateTime.UtcNow;
        var (firstName, lastName, middleName) = _candidateParser.ParseName(fullName);
        var person = new CandidatePersonEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = officeId,
            FullName = fullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            Age = request.Age is >= 14 and <= 99 ? request.Age : null,
            City = request.City?.Trim() ?? string.Empty,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var response = new CandidateResponseEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            OfficeId = officeId,
            AccountId = Guid.Empty,
            AccountName = string.IsNullOrWhiteSpace(request.Source) ? "Ручной ввод" : request.Source.Trim(),
            Source = "Manual",
            SourceResponseId = sourceResponseId,
            FullName = fullName,
            FirstName = firstName,
            LastName = lastName,
            MiddleName = middleName,
            Age = person.Age,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            City = person.City,
            Vacancy = request.Vacancy?.Trim() ?? string.Empty,
            Status = ResponseStatuses.New,
            CreatedAt = now,
            CollectedAt = now
        };

        db.CandidatePersons.Add(person);
        db.CandidateResponses.Add(response);
        db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            IsPrimary = true,
            CreatedAtUtc = now,
            CreatedByUserId = actorUserId
        });
        db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            ResponseId = response.Id,
            PhoneRaw = phoneRaw,
            PhoneNormalized = phoneNormalized,
            RecordedAtUtc = now
        });

        var card = new CrmCandidateCardEntity
        {
            Id = Guid.NewGuid(),
            ResponseId = response.Id,
            OfficeId = officeId,
            Stage = stage,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StageChangedAtUtc = now,
            IsInActiveLoad = true
        };
        if (request.AssignToMe)
        {
            card.ManagerUserId = actorUserId;
        }

        db.CrmCandidateCards.Add(card);
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        AddHistory(card.Id, "Created", "Карточка создана вручную", actorUserId, actorName, now);
        if (request.AssignToMe)
        {
            AddHistory(card.Id, "Assigned", actorName, actorUserId, actorName, now);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return (null, "Не удалось сохранить отклик (возможен дубль ID). Проверьте ID отклика и телефон.");
        }

        NotifyBoardChanged(officeId);
        return (card.Id, null);
    }

    public async Task<CrmTaskDto?> CreateTaskAsync(
        Guid officeId,
        CrmTaskCreateRequest request,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title)
            || !CrmTaskImportances.IsValid(request.Importance)
            || await GetManagerProfileAsync(officeId, request.AssigneeUserId, ct, allowAdmin: false) is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var dueAtUtc = DateTimeUtcHelper.EnsureUtc(request.DueAtUtc);
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
            card.LastContactAtUtc = now;
            card.UpdatedAtUtc = now;
            if (dueAtUtc is DateTime due && (card.NextActionAtUtc is null || due < card.NextActionAtUtc))
            {
                card.NextActionAtUtc = due;
            }
        }

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
            DueAtUtc = dueAtUtc,
            Importance = request.Importance,
            ReminderVersion = Guid.NewGuid(),
            ReminderVersionChangedAtUtc = now,
            CreatedAtUtc = now
        };
        db.CrmTasks.Add(task);
        if (request.CardId is Guid historyCardId)
        {
            AddHistory(historyCardId, "TaskCreated", task.Title, actorUserId, actorName, now);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(officeId);

        return task is null
            ? null
            : new CrmTaskDto(
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
                task.DueAtUtc is DateTime d && d < now,
                task.Importance);
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

    public async Task<(bool Ok, string? Error)> CompleteTaskAsync(
        Guid taskId,
        string comment,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return (false, "При выполнении задачи обязателен комментарий: что сделано.");
        }

        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null)
        {
            return (false, "Задача не найдена.");
        }

        if (task.Status != CrmTaskStatuses.Open)
        {
            return (false, "Выполнить можно только задачу в работе.");
        }

        if (!isAdmin && task.AssigneeUserId != userId)
        {
            return (false, "Выполнить задачу может только ответственный или руководитель.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(userId, ct);
        var commentText = comment.Trim();
        db.CrmTaskComments.Add(new CrmTaskCommentEntity
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AuthorUserId = userId,
            AuthorName = actorName,
            Text = commentText,
            CreatedAtUtc = now
        });
        task.Status = CrmTaskStatuses.Completed;
        task.CompletedAtUtc = now;
        if (deadlineNotifications is not null)
        {
            await deadlineNotifications.DismissTaskAsync(task.Id, now, ct);
        }
        if (task.CardId is Guid cardId)
        {
            AddHistory(cardId, "TaskCompleted", task.Title, userId, actorName, now);
            await RefreshTaskCardNextActionAsync(task, now, markContact: true, ct);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);

        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateTaskAsync(
        Guid taskId,
        CrmTaskUpdateRequest request,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null)
        {
            return (false, "Задача не найдена.");
        }

        if (!CanManageTask(task, userId, isAdmin))
        {
            return (false, "Изменять задачу может её автор или администратор.");
        }

        if (task.Status != CrmTaskStatuses.Open)
        {
            return (false, "Можно изменить только задачу в работе.");
        }

        var title = request.Title?.Trim();
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 500)
        {
            return (false, "Название задачи обязательно и не должно превышать 500 символов.");
        }

        if (description?.Length > 4000)
        {
            return (false, "Описание задачи не должно превышать 4000 символов.");
        }

        if (!CrmTaskImportances.IsValid(request.Importance))
        {
            return (false, "Укажите корректную важность задачи.");
        }

        if (await GetManagerProfileAsync(task.OfficeId, request.AssigneeUserId, ct, allowAdmin: false) is null)
        {
            return (false, "Ответственный должен быть менеджером этого офиса.");
        }

        var now = DateTime.UtcNow;
        var dueAtUtc = DateTimeUtcHelper.EnsureUtc(request.DueAtUtc);
        var reminderInputsChanged = task.AssigneeUserId != request.AssigneeUserId
                                    || task.DueAtUtc != dueAtUtc;
        if (reminderInputsChanged)
        {
            task.ReminderVersion = Guid.NewGuid();
            task.ReminderVersionChangedAtUtc = now;
            if (deadlineNotifications is not null)
            {
                await deadlineNotifications.DismissTaskAsync(task.Id, now, ct);
            }
        }

        task.Title = title;
        task.Description = description;
        task.AssigneeUserId = request.AssigneeUserId;
        task.DueAtUtc = dueAtUtc;
        task.Importance = request.Importance;
        if (task.CardId is Guid cardId)
        {
            AddHistory(cardId, "TaskUpdated", task.Title, userId, await ResolveDisplayNameAsync(userId, ct), now);
            await RefreshTaskCardNextActionAsync(task, now, markContact: false, ct);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CancelTaskAsync(
        Guid taskId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null)
        {
            return (false, "Задача не найдена.");
        }

        if (!CanManageTask(task, userId, isAdmin))
        {
            return (false, "Отменять задачу может её автор или администратор.");
        }

        if (task.Status != CrmTaskStatuses.Open)
        {
            return (false, "Отменить можно только задачу в работе.");
        }

        var now = DateTime.UtcNow;
        task.Status = CrmTaskStatuses.Cancelled;
        task.CompletedAtUtc = now;
        if (deadlineNotifications is not null)
        {
            await deadlineNotifications.DismissTaskAsync(task.Id, now, ct);
        }
        if (task.CardId is Guid cardId)
        {
            AddHistory(cardId, "TaskCancelled", task.Title, userId, await ResolveDisplayNameAsync(userId, ct), now);
            await RefreshTaskCardNextActionAsync(task, now, markContact: false, ct);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> ReopenTaskAsync(
        Guid taskId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null)
        {
            return (false, "Задача не найдена.");
        }

        if (!CanManageTask(task, userId, isAdmin))
        {
            return (false, "Возвращать задачу в работу может её автор или администратор.");
        }

        if (task.Status == CrmTaskStatuses.Open)
        {
            return (true, null);
        }

        var now = DateTime.UtcNow;
        task.Status = CrmTaskStatuses.Open;
        task.CompletedAtUtc = null;
        task.ReminderVersion = Guid.NewGuid();
        task.ReminderVersionChangedAtUtc = now;
        if (deadlineNotifications is not null)
        {
            await deadlineNotifications.DismissTaskAsync(task.Id, now, ct);
        }
        if (task.CardId is Guid cardId)
        {
            AddHistory(cardId, "TaskReopened", task.Title, userId, await ResolveDisplayNameAsync(userId, ct), now);
            await RefreshTaskCardNextActionAsync(task, now, markContact: false, ct);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);
        return (true, null);
    }

    public async Task<CrmTaskDetailDto?> GetTaskAsync(
        Guid taskId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return null;
        }

        var canManage = CanManageTask(task, userId, isAdmin);
        var managers = await GetManagersAsync(task.OfficeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var candidateName = task.CardId is Guid cardId
            ? await db.CrmCandidateCards.AsNoTracking()
                .Where(x => x.Id == cardId)
                .Select(x => x.Response.FullName)
                .FirstOrDefaultAsync(ct)
            : null;
        var comments = await db.CrmTaskComments.AsNoTracking()
            .Where(x => x.TaskId == taskId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new CrmTaskCommentDto(
                x.Id,
                x.TaskId,
                x.AuthorUserId,
                x.AuthorName,
                x.Text,
                x.CreatedAtUtc))
            .ToListAsync(ct);
        var attachments = await db.CrmTaskAttachments.AsNoTracking()
            .Where(x => x.TaskId == taskId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new CrmTaskAttachmentDto(
                x.Id,
                x.FileName,
                x.ContentType,
                x.SizeBytes,
                x.UploadedByName,
                x.CreatedAtUtc))
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        IReadOnlyDictionary<string, int> noLoads = new Dictionary<string, int>(StringComparer.Ordinal);
        return new CrmTaskDetailDto(
            ToTaskDto(task, names, candidateName, now),
            comments,
            isAdmin || task.AssigneeUserId == userId,
            attachments,
            canManage,
            canManage
                ? managers.Select(x => ToManagerDto(x, noLoads, now)).ToList()
                : []);
    }

    public async Task<(CrmTaskAttachmentDto? Attachment, string? Error)> AddTaskAttachmentAsync(
        Guid taskId,
        Stream content,
        long contentLength,
        string fileName,
        string? contentType,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (taskAttachments is null)
        {
            return (null, "Хранилище вложений недоступно.");
        }

        if (contentLength <= 0 || contentLength > taskAttachments.MaxUploadBytes)
        {
            return (null, "Размер файла вне допустимого диапазона.");
        }

        var task = await db.CrmTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return (null, "Задача не найдена.");
        }

        var safeFileName = Path.GetFileName(fileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            safeFileName = "Вложение";
        }
        else if (safeFileName.Length > 255)
        {
            return (null, "Название файла слишком длинное.");
        }

        var safeContentType = string.IsNullOrWhiteSpace(contentType) || contentType.Length > 128
            ? "application/octet-stream"
            : contentType.Trim();
        var now = DateTime.UtcNow;
        var attachment = new CrmTaskAttachmentEntity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            FileName = safeFileName,
            ContentType = safeContentType,
            SizeBytes = contentLength,
            UploadedByUserId = userId,
            UploadedByName = await ResolveDisplayNameAsync(userId, ct),
            CreatedAtUtc = now
        };

        try
        {
            attachment.RelativePath = await taskAttachments.SaveAsync(taskId, attachment.Id, content, ct);
            db.CrmTaskAttachments.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(attachment.RelativePath))
            {
                taskAttachments.TryDelete(attachment.RelativePath);
            }

            return (null, "Не удалось сохранить вложение.");
        }

        return (new CrmTaskAttachmentDto(
            attachment.Id,
            attachment.FileName,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.UploadedByName,
            attachment.CreatedAtUtc), null);
    }

    public async Task<(Stream? Stream, string? FileName, string? ContentType)> OpenTaskAttachmentAsync(
        Guid taskId,
        Guid attachmentId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (taskAttachments is null)
        {
            return (null, null, null);
        }

        var task = await db.CrmTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return (null, null, null);
        }

        var attachment = await db.CrmTaskAttachments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == attachmentId && x.TaskId == taskId, ct);
        if (attachment is null)
        {
            return (null, null, null);
        }

        return (taskAttachments.OpenRead(attachment.RelativePath), attachment.FileName, attachment.ContentType);
    }

    public async Task<CrmTaskCommentDto?> AddTaskCommentAsync(
        Guid taskId,
        string text,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var task = await db.CrmTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var comment = new CrmTaskCommentEntity
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            AuthorUserId = userId,
            AuthorName = await ResolveDisplayNameAsync(userId, ct),
            Text = text.Trim(),
            CreatedAtUtc = now
        };
        db.CrmTaskComments.Add(comment);
        await db.SaveChangesAsync(ct);

        return new CrmTaskCommentDto(
            comment.Id,
            comment.TaskId,
            comment.AuthorUserId,
            comment.AuthorName,
            comment.Text,
            comment.CreatedAtUtc);
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

    private async Task<CrmCandidateCardEntity?> FindAccessibleCardAsync(Guid cardId, string userId, bool isAdmin, CancellationToken ct)
    {
        var card = await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null)
        {
            return null;
        }

        return await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct)
            ? card
            : null;
    }

    private async Task<PanelUserProfileEntity?> GetManagerProfileAsync(
        Guid officeId,
        string userId,
        CancellationToken ct,
        bool allowAdmin = true)
    {
        var user = await users.FindByIdAsync(userId);
        if (user is null || !await IsCrmDeskUserAsync(user))
        {
            // Admin may set capacity / assign without desk role on some setups — still require profile.
            if (!allowAdmin || user is null || !await users.IsInRoleAsync(user, PanelRoles.Admin))
            {
                return null;
            }
        }

        return await db.PanelUserProfiles.FirstOrDefaultAsync(x => x.OfficeId == officeId && x.UserId == userId, ct);
    }

    private async Task<bool> IsCrmDeskUserAsync(IdentityUser user)
    {
        foreach (var role in PanelRoles.CrmDeskRoles)
        {
            if (await users.IsInRoleAsync(user, role))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOnShift(PanelUserProfileEntity profile, DateTime utcNow) =>
        CrmShiftRules.IsEffectivelyOnShift(profile.CrmShiftActive, profile.CrmShiftStartedAtUtc, utcNow);

    private sealed record ManagerShiftMeta(
        IReadOnlyDictionary<string, DateTime> OpenStartedAtUtc,
        IReadOnlyDictionary<string, DateTime> LastEndedAtUtc);

    private async Task<ManagerShiftMeta> LoadManagerShiftMetaAsync(
        Guid officeId,
        IEnumerable<string> managerUserIds,
        CancellationToken ct)
    {
        var ids = managerUserIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return new ManagerShiftMeta(
                new Dictionary<string, DateTime>(StringComparer.Ordinal),
                new Dictionary<string, DateTime>(StringComparer.Ordinal));
        }

        var shifts = await db.CrmManagerShifts.AsNoTracking()
            .Where(x => x.OfficeId == officeId && ids.Contains(x.ManagerUserId))
            .Select(x => new { x.ManagerUserId, x.StartedAtUtc, x.EndedAtUtc })
            .ToListAsync(ct);

        var open = shifts
            .Where(x => x.EndedAtUtc is null)
            .GroupBy(x => x.ManagerUserId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Max(x => x.StartedAtUtc),
                StringComparer.Ordinal);

        var lastEnded = shifts
            .Where(x => x.EndedAtUtc is DateTime)
            .GroupBy(x => x.ManagerUserId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Max(x => x.EndedAtUtc!.Value),
                StringComparer.Ordinal);

        return new ManagerShiftMeta(open, lastEnded);
    }

    private static CrmManagerDto ToManagerDto(
        (PanelUserProfileEntity Profile, string Name) manager,
        IReadOnlyDictionary<string, int> loads,
        DateTime utcNow,
        ManagerShiftMeta? shiftMeta = null)
    {
        var onShift = IsOnShift(manager.Profile, utcNow);
        DateTime? startedAt = null;
        if (onShift)
        {
            startedAt = manager.Profile.CrmShiftStartedAtUtc;
            if (startedAt is null
                && shiftMeta is not null
                && shiftMeta.OpenStartedAtUtc.TryGetValue(manager.Profile.UserId, out var openStart))
            {
                startedAt = openStart;
            }
        }

        DateTime? lastEnded = null;
        if (shiftMeta is not null
            && shiftMeta.LastEndedAtUtc.TryGetValue(manager.Profile.UserId, out var ended))
        {
            lastEnded = ended;
        }

        return new CrmManagerDto(
            manager.Profile.UserId,
            manager.Name,
            onShift,
            manager.Profile.CrmCapacity,
            loads.GetValueOrDefault(manager.Profile.UserId),
            startedAt,
            lastEnded);
    }

    private static bool CanAccessTask(CrmTaskEntity task, string userId, bool isAdmin) =>
        isAdmin || task.AssigneeUserId == userId || task.CreatorUserId == userId;

    private static bool CanManageTask(CrmTaskEntity task, string userId, bool isAdmin) =>
        isAdmin || task.CreatorUserId == userId;

    private async Task RefreshTaskCardNextActionAsync(
        CrmTaskEntity task,
        DateTime now,
        bool markContact,
        CancellationToken ct)
    {
        if (task.CardId is not Guid cardId)
        {
            return;
        }

        var card = await db.CrmCandidateCards.FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null)
        {
            return;
        }

        var nextOther = await db.CrmTasks.AsNoTracking()
            .Where(x => x.CardId == cardId
                        && x.Id != task.Id
                        && x.Status == CrmTaskStatuses.Open
                        && x.DueAtUtc != null)
            .OrderBy(x => x.DueAtUtc)
            .Select(x => x.DueAtUtc)
            .FirstOrDefaultAsync(ct);
        var ownNext = task.Status == CrmTaskStatuses.Open ? task.DueAtUtc : null;
        card.NextActionAtUtc = ownNext is null
            ? nextOther
            : nextOther is null ? ownNext : ownNext < nextOther ? ownNext : nextOther;
        card.UpdatedAtUtc = now;
        if (markContact)
        {
            card.LastContactAtUtc = now;
        }
    }

    private async Task<List<(PanelUserProfileEntity Profile, string Name)>> GetManagersAsync(Guid officeId, CancellationToken ct)
    {
        var deskUsers = await LoadCrmDeskUsersAsync(ct);
        var ids = deskUsers.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var profiles = await db.PanelUserProfiles.Where(x => x.OfficeId == officeId && ids.Contains(x.UserId)).ToListAsync(ct);
        var names = deskUsers.ToDictionary(x => x.Id, x => DisplayName(x), StringComparer.Ordinal);
        return profiles.Select(x => (
            x,
            string.IsNullOrWhiteSpace(x.FullName)
                ? names.GetValueOrDefault(x.UserId, x.UserId)
                : x.FullName)).ToList();
    }

    private async Task<List<IdentityUser>> LoadCrmDeskUsersAsync(CancellationToken ct)
    {
        _ = ct;
        var byId = new Dictionary<string, IdentityUser>(StringComparer.Ordinal);
        foreach (var role in PanelRoles.CrmDeskRoles)
        {
            foreach (var user in await users.GetUsersInRoleAsync(role))
            {
                byId[user.Id] = user;
            }
        }

        return byId.Values.ToList();
    }

    private async Task<string> ResolveDisplayNameAsync(string userId, CancellationToken ct)
    {
        if (userId is "system")
        {
            return "Система";
        }

        var fullName = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.FullName)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        var user = await users.FindByIdAsync(userId);
        return user is null ? userId : DisplayName(user);
    }

    private static string DisplayName(IdentityUser user) =>
        user.Email ?? user.UserName ?? user.Id;

    private static string NormalizeScope(string? scope) =>
        scope switch
        {
            CrmBoardScopes.Team => CrmBoardScopes.Team,
            CrmBoardScopes.Unassigned => CrmBoardScopes.Unassigned,
            CrmBoardScopes.Closed => CrmBoardScopes.Closed,
            _ => CrmBoardScopes.Mine
        };

    /// <summary>
    /// Card access: owner always; elevated roles only within their office (global Admin: any office).
    /// </summary>
    private async Task<bool> CanAccessCardAsync(
        Guid cardOfficeId,
        string? managerUserId,
        string userId,
        bool isElevated,
        CancellationToken ct)
    {
        if (string.Equals(managerUserId, userId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!isElevated)
        {
            return false;
        }

        var user = await users.FindByIdAsync(userId);
        if (user is not null && await users.IsInRoleAsync(user, PanelRoles.Admin))
        {
            return true;
        }

        var ownOfficeId = await db.PanelUserProfiles.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.OfficeId)
            .FirstOrDefaultAsync(ct);
        return ownOfficeId is Guid oid && oid != Guid.Empty && oid == cardOfficeId;
    }

    /// <summary>
    /// Board badges: hash compare only (no chat JSON parse). Value is 1 when unread, 0 when read/absent.
    /// Exact counts are computed on card detail.
    /// </summary>
    private async Task<Dictionary<Guid, int>> LoadChatUnreadCountsAsync(
        IReadOnlyList<Guid> cardIds,
        string userId,
        IReadOnlyList<CrmCandidateCardEntity> cards,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        if (cardIds.Count == 0)
        {
            return result;
        }

        var reads = await db.CrmCardChatReads.AsNoTracking()
            .Where(x => x.UserId == userId && cardIds.Contains(x.CardId))
            .ToDictionaryAsync(x => x.CardId, x => x.ContentHash, ct);

        foreach (var card in cards)
        {
            var json = card.Response.ChatMessagesJson;
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            var hash = ComputeChatContentHash(json);
            if (reads.TryGetValue(card.Id, out var readHash)
                && string.Equals(readHash, hash, StringComparison.Ordinal))
            {
                continue;
            }

            // 1 = has unread (badge); full count only on card open.
            result[card.Id] = 1;
        }

        return result;
    }

    private static CrmCandidateCardDto ToCardDto(
        CrmCandidateCardEntity card,
        IReadOnlyDictionary<string, string> names,
        int openTaskCount,
        bool hasOverdue,
        DateTime now,
        int chatUnreadCount = 0)
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
            Math.Round(hours, 1),
            string.IsNullOrWhiteSpace(card.Response.SourceUrl) ? null : card.Response.SourceUrl,
            string.IsNullOrWhiteSpace(card.Response.VacancyUrl) ? null : card.Response.VacancyUrl,
            string.IsNullOrWhiteSpace(card.Response.AccountName) ? null : card.Response.AccountName,
            string.IsNullOrWhiteSpace(card.Response.SourceResponseId) ? null : card.Response.SourceResponseId,
            chatUnreadCount);
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
            names.GetValueOrDefault(
                task.CreatorUserId,
                string.IsNullOrWhiteSpace(task.CreatorName) ? task.CreatorUserId : task.CreatorName),
            task.DueAtUtc,
            task.Status,
            task.CreatedAtUtc,
            task.CompletedAtUtc,
            task.Status == CrmTaskStatuses.Open && task.DueAtUtc is DateTime due && due < now,
            task.Importance);

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
        "CardUpdated" => "Карточка изменена",
        "Created" => "Карточка создана",
        "Assigned" => "Назначен ответственный",
        "StageChanged" => "Смена этапа",
        "ReturnedToLoad" => "Вернули в нагрузку",
        "RemovedFromLoad" => "Сняли с нагрузки",
        "Closed" => "Карточка закрыта",
        "Reopened" => "Карточка открыта снова",
        "TaskUpdated" => "Задача изменена",
        "TaskCancelled" => "Задача отменена",
        "TaskReopened" => "Задача возвращена в работу",
        _ => action
    };

    private void NotifyBoardChanged(Guid officeId) =>
        panelRealtime?.Notify([PanelChangeKind.Crm], officeId);

    private async Task EnsureContactPhonesSeededAsync(
        CrmCandidateCardEntity card,
        string actorUserId,
        CancellationToken ct)
    {
        var personId = card.Response.PersonId;
        if (await db.CandidateContactPhones.AnyAsync(x => x.PersonId == personId, ct))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(card.Response.PhoneNormalized))
        {
            return;
        }

        // Same unit of work as caller — avoid intermediate SaveChanges (unique-index races).
        db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            PhoneRaw = card.Response.PhoneRaw,
            PhoneNormalized = card.Response.PhoneNormalized,
            IsPrimary = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserId = actorUserId
        });
    }

    private async Task ClearPrimaryContactFlagsAsync(Guid personId, CancellationToken ct)
    {
        var phones = await db.CandidateContactPhones.Where(x => x.PersonId == personId && x.IsPrimary).ToListAsync(ct);
        foreach (var phone in phones)
        {
            phone.IsPrimary = false;
        }
    }

    private async Task SyncPrimaryContactPhoneAsync(
        Guid personId,
        string phoneRaw,
        string phoneNormalized,
        string actorUserId,
        DateTime now,
        CancellationToken ct)
    {
        await ClearPrimaryContactFlagsAsync(personId, ct);
        var existing = await db.CandidateContactPhones
            .FirstOrDefaultAsync(x => x.PersonId == personId && x.PhoneNormalized == phoneNormalized, ct);
        if (existing is null)
        {
            db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
            {
                Id = Guid.NewGuid(),
                PersonId = personId,
                PhoneRaw = phoneRaw,
                PhoneNormalized = phoneNormalized,
                IsPrimary = true,
                CreatedAtUtc = now,
                CreatedByUserId = actorUserId
            });
        }
        else
        {
            existing.PhoneRaw = phoneRaw;
            existing.IsPrimary = true;
        }
    }

    /// <summary>Stages a desk alert without SaveChanges — caller must SaveChanges then FlushPhoneChangedRealtimeAsync.</summary>
    private async Task QueuePhoneChangedAlertAsync(
        CrmCandidateCardEntity card,
        string actorUserId,
        string previousPhoneRaw,
        string newPhoneRaw,
        DateTime now,
        CancellationToken ct)
    {
        var recipient = card.ManagerUserId;
        if (string.IsNullOrWhiteSpace(recipient) || string.Equals(recipient, actorUserId, StringComparison.Ordinal))
        {
            return;
        }

        var title = card.Response?.FullName;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = await db.CandidateResponses.AsNoTracking()
                .Where(x => x.Id == card.ResponseId)
                .Select(x => x.FullName)
                .FirstOrDefaultAsync(ct) ?? "Кандидат";
        }

        var alert = new CrmDeskAlertEntity
        {
            Id = Guid.NewGuid(),
            OfficeId = card.OfficeId,
            RecipientUserId = recipient,
            Kind = CrmTaskNotificationKinds.PhoneChanged,
            CardId = card.Id,
            Title = title,
            Message = $"Изменён телефон: {previousPhoneRaw} → {newPhoneRaw}",
            CreatedAtUtc = now
        };
        db.CrmDeskAlerts.Add(alert);
        _pendingPhoneRealtime.Add((
            recipient,
            new CrmTaskNotificationDto(
                alert.Id,
                Guid.Empty,
                card.Id,
                alert.Kind,
                alert.Title,
                alert.Message,
                now,
                now,
                null)));
    }

    private async Task FlushPhoneChangedRealtimeAsync(CancellationToken ct)
    {
        if (crmNotificationRealtime is null || _pendingPhoneRealtime.Count == 0)
        {
            _pendingPhoneRealtime.Clear();
            return;
        }

        foreach (var (recipient, dto) in _pendingPhoneRealtime)
        {
            await crmNotificationRealtime.NotifyAsync(recipient, dto, ct);
        }

        _pendingPhoneRealtime.Clear();
    }

    private async Task<IReadOnlyList<CrmContactPhoneDto>> LoadContactPhonesAsync(
        CandidateResponseEntity response,
        CancellationToken ct)
    {
        var phones = await db.CandidateContactPhones.AsNoTracking()
            .Where(x => x.PersonId == response.PersonId)
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        if (phones.Count > 0)
        {
            return phones
                .Select(x => new CrmContactPhoneDto(x.Id, x.PhoneRaw, x.PhoneNormalized, x.IsPrimary, x.Label, x.CreatedAtUtc))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(response.PhoneNormalized))
        {
            return [];
        }

        return
        [
            new CrmContactPhoneDto(
                Guid.Empty,
                response.PhoneRaw,
                response.PhoneNormalized,
                true,
                null,
                response.CreatedAt)
        ];
    }

    private static string ComputeChatContentHash(string? chatMessagesJson)
    {
        if (string.IsNullOrWhiteSpace(chatMessagesJson))
        {
            return string.Empty;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chatMessagesJson));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static IReadOnlyList<CrmPhoneHistoryDto> BuildPhoneHistory(CandidateResponseEntity response)
    {
        var history = response.Person?.PhoneHistory
            ?.Select(x => new CrmPhoneHistoryDto(x.PhoneRaw, x.PhoneNormalized, x.RecordedAtUtc))
            .ToList() ?? [];

        if (!string.IsNullOrWhiteSpace(response.PreviousPhoneNormalized) &&
            !history.Any(x => string.Equals(x.PhoneNormalized, response.PreviousPhoneNormalized, StringComparison.Ordinal)))
        {
            history.Add(new CrmPhoneHistoryDto(
                string.IsNullOrWhiteSpace(response.PreviousPhoneRaw)
                    ? response.PreviousPhoneNormalized
                    : response.PreviousPhoneRaw,
                response.PreviousPhoneNormalized,
                response.PhoneChangedAtUtc ?? response.CreatedAt));
        }

        if (!string.IsNullOrWhiteSpace(response.PhoneNormalized) &&
            !history.Any(x => string.Equals(x.PhoneNormalized, response.PhoneNormalized, StringComparison.Ordinal)))
        {
            history.Add(new CrmPhoneHistoryDto(response.PhoneRaw, response.PhoneNormalized, response.CollectedAt));
        }

        return history.OrderBy(x => x.RecordedAtUtc).ToList();
    }

    private static IReadOnlyList<CrmChatMessageDto> ParseChatMessages(string? chatMessagesJson)
    {
        if (string.IsNullOrWhiteSpace(chatMessagesJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(chatMessagesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<CrmChatMessageDto>();
            foreach (var message in doc.RootElement.EnumerateArray())
            {
                var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var side = message.TryGetProperty("side", out var sideProp) ? sideProp.GetString() : null;
                var isPlatform = message.TryGetProperty("isPlatform", out var platformProp)
                    && platformProp.ValueKind == JsonValueKind.True;
                var at = message.TryGetProperty("at", out var atProp) ? atProp.GetString() : null;

                var tone = string.Equals(side, "right", StringComparison.OrdinalIgnoreCase)
                    ? "outgoing"
                    : isPlatform
                        ? "system"
                        : "incoming";

                results.Add(new CrmChatMessageDto(text.Trim(), FormatMessageTime(at), tone));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FormatMessageTime(string? at)
    {
        if (string.IsNullOrWhiteSpace(at))
        {
            return string.Empty;
        }

        if (DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            var local = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
            return local.ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
        }

        return at;
    }
}
