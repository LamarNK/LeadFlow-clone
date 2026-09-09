using System.Globalization;
using System.IO.Compression;
using System.Text;
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
    CrmSuccessDocumentStorageService? successDocuments = null,
    CrmCallRecordingStorageService? callRecordings = null,
    CrmDeadlineNotificationService? deadlineNotifications = null,
    PhoneNormalizer? phoneNormalizer = null,
    CandidateParser? candidateParser = null,
    CandidatePersonPhoneService? personPhone = null,
    ICrmNotificationRealtimeNotifier? crmNotificationRealtime = null,
    IOrbitaQueryCache? queryCache = null)
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
            EnteredCrmAtUtc = now,
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
        // Keep the existing self-healing behaviour out of the cached payload.
        await ExpireStaleShiftsAsync(ct);
        query ??= new CrmBoardQuery();

        Task<CrmBoardDto?> Load(CancellationToken token) =>
            GetBoardUncachedAsync(officeId, userId, isAdmin, query, token);

        return queryCache is null
            ? await Load(ct)
            : await queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Crm,
                officeId,
                $"{userId}:{isAdmin}",
                new { Kind = "crm-board", query.Scope, query.View, query.Page, query.PageSize, query.Sort, query.SortDir, query.ManagerUserId, query.Search, query.City, query.Vacancy, query.Stage, query.CreatedFromUtc, query.CreatedToUtc, query.CloseReason, query.ActiveLoadOnly, query.OverdueOnly, query.IncludeClosed, query.TimeZoneOffsetMinutes },
                OrbitaCachePolicy.Realtime,
                Load,
                ct);
    }

    private async Task<CrmBoardDto?> GetBoardUncachedAsync(
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
        // isAdmin here means elevated office access (Admin / OfficeLead / SeniorManager), not only global admin.
        var scope = NormalizeScope(query.Scope);
        var boardView = CrmBoardViews.Normalize(query.View);
        var pageSize = CrmBoardListOptions.NormalizePageSize(query.PageSize);
        var requestedPage = CrmBoardListOptions.NormalizePage(query.Page);
        var sort = CrmBoardSorts.Normalize(query.Sort);
        var sortDir = CrmBoardSorts.NormalizeDirection(query.SortDir);
        if (!isAdmin && scope is CrmBoardScopes.Team or CrmBoardScopes.Unassigned)
        {
            // Manager: only own leads — force Mine (Team / queue are elevated-only).
            scope = CrmBoardScopes.Mine;
        }

        var profile = await db.PanelUserProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        var managers = await GetManagersAsync(officeId, ct);
        var selectedManagerUserId = isAdmin
                                    && scope is CrmBoardScopes.Team or CrmBoardScopes.Closed
                                    && !string.IsNullOrWhiteSpace(query.ManagerUserId)
                                    && managers.Any(x => string.Equals(
                                        x.Profile.UserId,
                                        query.ManagerUserId.Trim(),
                                        StringComparison.Ordinal))
            ? query.ManagerUserId.Trim()
            : null;
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var loads = await leadDistribution.GetActiveLoadsAsync(officeId, ct);
        var now = DateTime.UtcNow;
        var hasSearch = !string.IsNullOrWhiteSpace(query.Search);
        var includeClosed = query.IncludeClosed || hasSearch;

        var cardsQuery = db.CrmCandidateCards.AsNoTracking()
            .Include(x => x.Response)
            .Where(x => x.OfficeId == officeId);

        cardsQuery = scope switch
        {
            CrmBoardScopes.Unassigned => cardsQuery.Where(x => x.ManagerUserId == null && (!x.IsClosed || includeClosed)),
            // Elevated: all closed in office. Manager: only own closed.
            CrmBoardScopes.Closed => isAdmin
                ? selectedManagerUserId is null
                    ? cardsQuery.Where(x => x.IsClosed)
                    : cardsQuery.Where(x => x.IsClosed && x.ManagerUserId == selectedManagerUserId)
                : cardsQuery.Where(x => x.IsClosed && x.ManagerUserId == userId),
            CrmBoardScopes.Team => selectedManagerUserId is null
                ? cardsQuery.Where(x => !x.IsClosed || includeClosed)
                : cardsQuery.Where(x => x.ManagerUserId == selectedManagerUserId && (!x.IsClosed || includeClosed)),
            _ => cardsQuery.Where(x => x.ManagerUserId == userId && (!x.IsClosed || includeClosed))
        };

        if (hasSearch)
        {
            var term = query.Search!.Trim();
            var lowered = term.ToLower();
            var digits = SearchQueryNormalizer.ExtractDigits(term);
            var normalizedPhoneDigits = digits.Length == 11 && digits.StartsWith('8')
                ? $"7{digits[1..]}"
                : digits;
            var searchByPhone = digits.Length >= 4;
            cardsQuery = cardsQuery.Where(x =>
                x.Response.FullName.ToLower().Contains(lowered) ||
                x.Response.City.ToLower().Contains(lowered) ||
                x.Response.Vacancy.ToLower().Contains(lowered) ||
                x.Response.PhoneRaw.Contains(term) ||
                (searchByPhone && (
                    x.Response.PhoneNormalized.Contains(normalizedPhoneDigits) ||
                    x.Response.PreviousPhoneNormalized.Contains(normalizedPhoneDigits) ||
                    db.CandidatePhoneHistory.Any(history =>
                        history.PersonId == x.Response.PersonId && history.PhoneNormalized.Contains(normalizedPhoneDigits)))));
        }

        if (!string.IsNullOrWhiteSpace(query.City))
        {
            var city = query.City.Trim().ToLower();
            cardsQuery = cardsQuery.Where(x => x.Response.City.ToLower().Contains(city));
        }

        if (!string.IsNullOrWhiteSpace(query.Vacancy))
        {
            var vacancy = query.Vacancy.Trim().ToLower();
            cardsQuery = cardsQuery.Where(x => x.Response.Vacancy.ToLower().Contains(vacancy));
        }

        var selectedStage = string.IsNullOrWhiteSpace(query.Stage) ? null : query.Stage.Trim();
        if (selectedStage is not null)
        {
            cardsQuery = cardsQuery.Where(x => x.Stage == selectedStage);
        }

        var createdFromUtc = DateTimeUtcHelper.EnsureUtc(query.CreatedFromUtc);
        var createdToUtc = DateTimeUtcHelper.EnsureUtc(query.CreatedToUtc);
        if (createdFromUtc is not null)
        {
            cardsQuery = cardsQuery.Where(x => x.CreatedAtUtc >= createdFromUtc.Value);
        }

        if (createdToUtc is not null)
        {
            cardsQuery = cardsQuery.Where(x => x.CreatedAtUtc < createdToUtc.Value);
        }

        var selectedCloseReason = scope == CrmBoardScopes.Closed
                                  && CrmCloseReasons.IsValid(query.CloseReason)
            ? query.CloseReason!.Trim()
            : null;
        if (selectedCloseReason is not null)
        {
            cardsQuery = cardsQuery.Where(x => x.CloseReason == selectedCloseReason);
        }

        if (query.ActiveLoadOnly)
        {
            var excludedLoadStages = CrmManagerLoadRules.GetExcludedStages(office.Name).ToArray();
            cardsQuery = cardsQuery.Where(x =>
                x.IsInActiveLoad
                && !excludedLoadStages.Contains(x.Stage));
        }

        if (query.OverdueOnly)
        {
            cardsQuery = cardsQuery.Where(x =>
                (x.NextActionAtUtc != null && x.NextActionAtUtc < now)
                || db.CrmTasks.Any(task =>
                    task.CardId == x.Id
                    && task.Status == CrmTaskStatuses.Open
                    && task.DueAtUtc != null
                    && task.DueAtUtc < now));
        }

        var totalItems = await cardsQuery.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
        var page = boardView == CrmBoardViews.List
            ? Math.Min(requestedPage, totalPages)
            : 1;

        var officeStages = CrmStages.Resolve(office.CrmStagesJson);

        IOrderedQueryable<CrmCandidateCardEntity> orderedCards = (sort, sortDir) switch
        {
            (CrmBoardSorts.Candidate, "asc") => cardsQuery.OrderBy(x => x.Response.FullName).ThenBy(x => x.Id),
            (CrmBoardSorts.Candidate, _) => cardsQuery.OrderByDescending(x => x.Response.FullName).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Phone, "asc") => cardsQuery.OrderBy(x => x.Response.PhoneNormalized).ThenBy(x => x.Id),
            (CrmBoardSorts.Phone, _) => cardsQuery.OrderByDescending(x => x.Response.PhoneNormalized).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Vacancy, "asc") => cardsQuery.OrderBy(x => x.Response.Vacancy).ThenBy(x => x.Id),
            (CrmBoardSorts.Vacancy, _) => cardsQuery.OrderByDescending(x => x.Response.Vacancy).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Stage, "asc") => cardsQuery.OrderBy(x => x.Stage).ThenBy(x => x.Id),
            (CrmBoardSorts.Stage, _) => cardsQuery.OrderByDescending(x => x.Stage).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Manager, "asc") => cardsQuery.OrderBy(x => x.ManagerUserId).ThenBy(x => x.Id),
            (CrmBoardSorts.Manager, _) => cardsQuery.OrderByDescending(x => x.ManagerUserId).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Changed, "asc") => cardsQuery.OrderBy(x => x.StageChangedAtUtc).ThenBy(x => x.Id),
            (CrmBoardSorts.Changed, _) => cardsQuery.OrderByDescending(x => x.StageChangedAtUtc).ThenByDescending(x => x.Id),
            (CrmBoardSorts.Created, "asc") => cardsQuery.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id),
            _ => cardsQuery.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
        };

        Dictionary<string, int>? boardOpenStageCounts = null;
        var boardClosedCount = 0;
        List<CrmCandidateCardEntity> cards;
        if (boardView == CrmBoardViews.List)
        {
            cards = await orderedCards
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);
        }
        else if (scope is CrmBoardScopes.Mine or CrmBoardScopes.Team)
        {
            var counts = await cardsQuery
                .GroupBy(x => new { x.Stage, x.IsClosed })
                .Select(group => new { group.Key.Stage, group.Key.IsClosed, Count = group.Count() })
                .ToListAsync(ct);
            boardOpenStageCounts = counts
                .Where(x => !x.IsClosed)
                .GroupBy(x => x.Stage, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(x => x.Count), StringComparer.Ordinal);
            boardClosedCount = counts.Where(x => x.IsClosed).Sum(x => x.Count);

            var stageNames = officeStages
                .Concat(boardOpenStageCounts.Keys.Where(stage => !officeStages.Contains(stage, StringComparer.Ordinal)))
                .ToList();
            cards = [];
            foreach (var stageName in stageNames)
            {
                cards.AddRange(await orderedCards
                    .Where(x => !x.IsClosed && x.Stage == stageName)
                    .Take(CrmBoardStageOptions.PageSize)
                    .ToListAsync(ct));
            }

            if (includeClosed && boardClosedCount > 0)
            {
                cards.AddRange(await orderedCards
                    .Where(x => x.IsClosed)
                    .Take(CrmBoardStageOptions.PageSize)
                    .ToListAsync(ct));
            }
        }
        else
        {
            // Queue and closed archive keep their existing bounded table payload.
            cards = await orderedCards.Take(500).ToListAsync(ct);
        }

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
        var personIds = cards.Select(x => x.Response.PersonId).Distinct().ToList();
        var contactPhoneRows = await db.CandidateContactPhones.AsNoTracking()
            .Where(x => personIds.Contains(x.PersonId))
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.CreatedAtUtc)
            .Select(x => new { x.PersonId, x.PhoneRaw })
            .ToListAsync(ct);
        var contactPhonesByPerson = contactPhoneRows
            .GroupBy(x => x.PersonId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(x => x.PhoneRaw)
                    .Where(phone => !string.IsNullOrWhiteSpace(phone))
                    .Distinct(StringComparer.Ordinal)
                    .ToList());

        CrmCandidateCardDto MapCard(CrmCandidateCardEntity x)
        {
            var stats = taskStats.GetValueOrDefault(x.Id);
            return ToCardDto(
                x,
                names,
                stats.Count,
                stats.Overdue,
                now,
                chatUnreadByCard.GetValueOrDefault(x.Id),
                office.Name,
                contactPhonesByPerson.GetValueOrDefault(x.Response.PersonId));
        }

        var stageDtos = officeStages.Select(stage =>
        {
            var stageCards = cards.Where(x => x.Stage == stage && !x.IsClosed).Select(MapCard).ToList();
            var totalCount = boardOpenStageCounts?.GetValueOrDefault(stage) ?? stageCards.Count;
            return new CrmStageDto(stage, stageCards, totalCount);
        }).ToList();

        // Cards left on stages removed from the funnel stay visible until remapped.
        var knownStages = new HashSet<string>(officeStages, StringComparer.Ordinal);
        var orphanStages = (boardOpenStageCounts is null
                ? cards.Where(x => !x.IsClosed).Select(x => x.Stage).Distinct(StringComparer.Ordinal)
                : boardOpenStageCounts.Keys)
            .Where(stage => !knownStages.Contains(stage))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (var orphan in orphanStages)
        {
            var stageCards = cards.Where(x => x.Stage == orphan && !x.IsClosed).Select(MapCard).ToList();
            var totalCount = boardOpenStageCounts?.GetValueOrDefault(orphan) ?? stageCards.Count;
            stageDtos.Add(new CrmStageDto(orphan, stageCards, totalCount));
        }

        if (scope == CrmBoardScopes.Closed || includeClosed)
        {
            var closedCards = cards.Where(x => x.IsClosed).Select(MapCard).ToList();
            if (closedCards.Count > 0)
            {
                var totalCount = boardOpenStageCounts is null ? closedCards.Count : boardClosedCount;
                stageDtos = stageDtos.Concat([new CrmStageDto("Закрыто", closedCards, totalCount)]).ToList();
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

        var timeZoneOffsetMinutes = Math.Clamp(query.TimeZoneOffsetMinutes, -14 * 60, 14 * 60);
        var localToday = LocalCalendarDateRange.GetLocalCalendarDate(
            now,
            timeZoneOffsetMinutes);
        var (dayStart, dayEnd) = LocalCalendarDateRange.GetUtcRangeForLocalCalendarDay(
            localToday,
            timeZoneOffsetMinutes);
        var assignmentEventsToday = await (
                from history in db.CrmCandidateHistory.AsNoTracking()
                join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
                where card.OfficeId == officeId
                      && history.Action == "Assigned"
                      && history.CreatedAtUtc >= dayStart
                      && history.CreatedAtUtc < dayEnd
                select new
                {
                    history.CardId,
                    history.CreatedAtUtc,
                    history.Details,
                    card.InitialAssignedAtUtc
                })
            .ToListAsync(ct);
        var redistributedAssignmentGroups = assignmentEventsToday
            .GroupBy(x => x.CardId)
            .Where(group => group.Any(item =>
                !item.InitialAssignedAtUtc.HasValue
                || item.CreatedAtUtc > item.InitialAssignedAtUtc.Value))
            .ToList();
        var teamStats = new CrmTeamStatsDto(
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && !x.IsClosed, ct),
            await db.CrmCandidateCards.CountAsync(x => x.OfficeId == officeId && x.ManagerUserId == null && !x.IsClosed, ct),
            managers.Count(x => IsOnShift(x.Profile, now)),
            managers.Count,
            await db.CrmCandidateCards.CountAsync(x =>
                x.OfficeId == officeId
                && x.IsClosed
                && x.ClosedAtUtc >= dayStart
                && x.ClosedAtUtc < dayEnd, ct),
            await db.CrmCandidateCards.CountAsync(x =>
                x.OfficeId == officeId
                && x.InitialManagerUserId != null
                && x.InitialManagerUserId != ""
                && x.InitialAssignedAtUtc >= dayStart
                && x.InitialAssignedAtUtc < dayEnd, ct),
            redistributedAssignmentGroups.Count,
            redistributedAssignmentGroups.Count(group => group.Any(item =>
                string.Equals(
                    item.Details,
                    CrmLeadDistributionService.ReasonDailyNdz,
                    StringComparison.Ordinal)
                && (!item.InitialAssignedAtUtc.HasValue
                    || item.CreatedAtUtc > item.InitialAssignedAtUtc.Value))),
            (await db.CrmCandidateCards.AsNoTracking()
                .Where(x => x.OfficeId == officeId && !x.IsClosed)
                .Select(x => x.Stage)
                .ToListAsync(ct))
            .GroupBy(stage => stage)
            .Select(g => new CrmStageCountDto(g.Key, g.Count()))
            .ToList());

        var taskAssigneeUserId = scope == CrmBoardScopes.Mine
            ? userId
            : selectedManagerUserId;
        var openTaskCount = await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId
                 && x.Status == CrmTaskStatuses.Open
                 && (!isAdmin || taskAssigneeUserId == null || x.AssigneeUserId == taskAssigneeUserId)
                 && (isAdmin || x.AssigneeUserId == userId), ct);
        var overdueTaskCount = await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId
                 && x.Status == CrmTaskStatuses.Open
                 && x.DueAtUtc != null
                 && x.DueAtUtc < now
                 && (!isAdmin || taskAssigneeUserId == null || x.AssigneeUserId == taskAssigneeUserId)
                 && (isAdmin || x.AssigneeUserId == userId), ct);

        return new CrmBoardDto(
            office.CrmEnabled,
            true,
            profile is not null && IsOnShift(profile, now),
            profile?.CrmCapacity ?? 300,
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
            office.CrmDeadlineNotificationsEnabled,
            selectedManagerUserId,
            selectedCloseReason,
            boardView,
            page,
            pageSize,
            totalItems,
            sort,
            sortDir,
            boardView == CrmBoardViews.List ? cards.Select(MapCard).ToList() : null,
            selectedStage,
            query.CreatedFrom,
            query.CreatedTo);
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
        await leadDistribution.ScheduleDailyDistributionAsync(
            officeId,
            userId,
            now,
            ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        NotifyBoardChanged(officeId);
        return true;
    }

    public async Task<int> ProcessDueDailyDistributionsAsync(CancellationToken ct = default)
    {
        var changedOffices = await leadDistribution.ProcessDueDailyDistributionsAsync(ct);
        foreach (var officeId in changedOffices)
        {
            NotifyBoardChanged(officeId);
        }

        return changedOffices.Count;
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
        if (capacity is < 1 or > 300)
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
        office.CrmRequireStageComment = true;
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
        else if (nextDeadlineNotificationsEnabled && office.CrmDeadlineNotificationsEnabledAtUtc is null)
        {
            office.CrmDeadlineNotificationsEnabledAtUtc = now;
        }
        else if (!nextDeadlineNotificationsEnabled && office.CrmDeadlineNotificationsEnabledAtUtc is not null)
        {
            office.CrmDeadlineNotificationsEnabledAtUtc = null;
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
                true,
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

        var officeInfo = await db.Offices.AsNoTracking()
            .Where(x => x.Id == card.OfficeId)
            .Select(x => new { x.Name, x.CrmStagesJson })
            .FirstOrDefaultAsync(ct);
        var officeStages = CrmStages.Resolve(officeInfo?.CrmStagesJson);
        var managers = await GetManagersAsync(card.OfficeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var loads = await leadDistribution.GetActiveLoadsAsync(card.OfficeId, ct);
        var now = DateTime.UtcNow;
        var notes = await db.CrmCandidateNotes.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.IsPinned)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderBy(x => x.Status == CrmTaskStatuses.Open ? 0 : x.Status == CrmTaskStatuses.Completed ? 1 : 2)
            .ThenBy(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc != null && x.DueAtUtc < now ? 0 : 1)
            .ThenBy(x => x.DueAtUtc ?? DateTime.MaxValue)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var taskIds = tasks.Select(x => x.Id).ToList();
        var taskComments = await db.CrmTaskComments.AsNoTracking()
            .Where(x => taskIds.Contains(x.TaskId))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var history = await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var calls = await db.CrmCalls.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToListAsync(ct);
        var callIds = calls.Select(x => x.Id).ToArray();
        var callAiStatuses = callIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await db.CrmCallAiInsights.AsNoTracking()
                .Where(x => callIds.Contains(x.CallId))
                .ToDictionaryAsync(x => x.CallId, x => x.Status, ct);
        var openCount = tasks.Count(x => x.Status == CrmTaskStatuses.Open);
        var hasOverdue = tasks.Any(x => x.Status == CrmTaskStatuses.Open && x.DueAtUtc is DateTime due && due < now);

        var activity = BuildActivity(notes, tasks, taskComments, history, calls, callAiStatuses, names, userId, isAdmin, canEdit);
        var avitoChat = ParseChatMessages(card.Response.ChatMessagesJson);
        var outboundChat = await LoadOutboundChatAsync(cardId, userId, isAdmin, ct);
        var chat = CrmChatThreadMerger.Merge(avitoChat, outboundChat);
        var phoneHistory = BuildPhoneHistory(card.Response);
        var contactPhones = await LoadContactPhonesAsync(card.Response, ct);
        var chatHash = ComputeChatContentHash(card.Response.ChatMessagesJson);
        var chatRead = await db.CrmCardChatReads.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CardId == cardId && x.UserId == userId, ct);
        var successReport = await db.CrmSuccessDocuments.AsNoTracking()
            .Where(x => x.CardId == cardId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new CrmSuccessDocumentDto(
                x.Id,
                x.Category,
                x.FileName,
                x.ContentType,
                x.SizeBytes,
                x.UploadedByName,
                x.CreatedAtUtc))
            .ToListAsync(ct);
        var chatUnread = chat.Count > 0
            && (chatRead is null || !string.Equals(chatRead.ContentHash, chatHash, StringComparison.Ordinal))
            ? chat.Count
            : 0;
        return new CrmCandidateDetailDto(
            ToCardDto(card, names, openCount, hasOverdue, now, chatUnread, officeInfo?.Name),
            notes.Select(x =>
            {
                var canManageNote = isAdmin || x.AuthorUserId == userId;
                return new CrmNoteDto(
                    x.Id,
                    x.AuthorUserId,
                    x.AuthorName,
                    x.Text,
                    x.CreatedAtUtc,
                    x.IsPinned,
                    x.UpdatedAtUtc,
                    canManageNote,
                    canManageNote,
                    canEdit);
            }).ToList(),
            tasks.Select(x => ToTaskDto(x, names, card.Response.FullName, now)).ToList(),
            history.Select(x => new CrmHistoryDto(x.Id, x.Action, x.Details, x.ActorUserId, x.ActorName, x.CreatedAtUtc)).ToList(),
            activity,
            managers.Select(x => ToManagerDto(x, loads, now)).ToList(),
            officeStages,
            canEdit,
            chat,
            phoneHistory,
            contactPhones,
            chatUnread,
            taskComments.Select(x =>
            {
                var canManageComment = isAdmin || x.AuthorUserId == userId;
                return new CrmTaskCommentDto(
                    x.Id,
                    x.TaskId,
                    x.AuthorUserId,
                    x.AuthorName,
                    x.Text,
                    x.CreatedAtUtc,
                    x.UpdatedAtUtc,
                    canManageComment,
                    canManageComment);
            }).ToList(),
            CrmClientTimeResolver.Resolve(card.Response.City, now),
            successReport,
            card.SuccessContractMissingReason);
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

    public async Task<(bool Ok, string? Error, string? CardName)> DeleteCardAsync(
        Guid cardId,
        bool isAdministrator,
        CancellationToken ct = default)
    {
        if (!isAdministrator)
        {
            return (false, "Удалить карточку может только администратор.", null);
        }

        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.", null);
        }

        var tasks = await db.CrmTasks.Where(x => x.CardId == cardId).ToListAsync(ct);
        var taskIds = tasks.Select(x => x.Id).ToList();
        var attachments = taskIds.Count == 0
            ? []
            : await db.CrmTaskAttachments.Where(x => taskIds.Contains(x.TaskId)).ToListAsync(ct);
        var successReport = await db.CrmSuccessDocuments.Where(x => x.CardId == cardId).ToListAsync(ct);

        if (taskIds.Count > 0)
        {
            db.CrmTaskNotifications.RemoveRange(
                await db.CrmTaskNotifications.Where(x => taskIds.Contains(x.TaskId)).ToListAsync(ct));
            db.CrmTaskComments.RemoveRange(
                await db.CrmTaskComments.Where(x => taskIds.Contains(x.TaskId)).ToListAsync(ct));
            db.CrmTaskAttachments.RemoveRange(attachments);
            db.CrmTasks.RemoveRange(tasks);
        }

        db.CrmCandidateNotes.RemoveRange(
            await db.CrmCandidateNotes.Where(x => x.CardId == cardId).ToListAsync(ct));
        db.CrmCandidateHistory.RemoveRange(
            await db.CrmCandidateHistory.Where(x => x.CardId == cardId).ToListAsync(ct));
        db.CrmOutboundChatMessages.RemoveRange(
            await db.CrmOutboundChatMessages.Where(x => x.CardId == cardId).ToListAsync(ct));
        db.CrmCardChatReads.RemoveRange(
            await db.CrmCardChatReads.Where(x => x.CardId == cardId).ToListAsync(ct));
        db.CrmSuccessDocuments.RemoveRange(successReport);

        var calls = await db.CrmCalls.Where(x => x.CardId == cardId).ToListAsync(ct);
        calls.ForEach(x => x.CardId = null);
        var deliveries = await db.ResponseCrmDeliveries.Where(x => x.CardId == cardId).ToListAsync(ct);
        deliveries.ForEach(x => x.CardId = null);
        var alerts = await db.CrmDeskAlerts.Where(x => x.CardId == cardId).ToListAsync(ct);
        alerts.ForEach(x => x.CardId = null);

        var officeId = card.OfficeId;
        var cardName = card.Response.FullName;
        db.CrmCandidateCards.Remove(card);
        await db.SaveChangesAsync(ct);

        if (taskAttachments is not null)
        {
            foreach (var attachment in attachments)
            {
                taskAttachments.TryDelete(attachment.RelativePath);
            }
        }
        if (successDocuments is not null)
        {
            foreach (var document in successReport)
            {
                successDocuments.TryDelete(document.RelativePath);
            }
        }

        NotifyBoardChanged(officeId);
        return (true, null, cardName);
    }

    public async Task<IReadOnlyList<CrmTaskDto>> GetTasksAsync(Guid officeId, string userId, bool isAdmin, CancellationToken ct = default)
        => await GetTasksAsync(officeId, userId, isAdmin, managerUserId: null, ct);

    public async Task<IReadOnlyList<CrmTaskDto>> GetTasksAsync(
        Guid officeId,
        string userId,
        bool isAdmin,
        string? managerUserId,
        CancellationToken ct = default)
    {
        var managers = await GetManagersAsync(officeId, ct);
        var names = managers.ToDictionary(x => x.Profile.UserId, x => x.Name, StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var selectedManagerUserId = isAdmin && !string.IsNullOrWhiteSpace(managerUserId)
            ? managerUserId.Trim()
            : userId;
        var tasks = await db.CrmTasks.AsNoTracking()
            .Where(x => x.OfficeId == officeId
                        && (isAdmin && string.IsNullOrWhiteSpace(managerUserId)
                            || x.AssigneeUserId == selectedManagerUserId))
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
            .Select(x => new { x.CrmStagesJson })
            .FirstOrDefaultAsync(ct);
        var officeStages = CrmStages.Resolve(officeMeta?.CrmStagesJson);
        if (!CrmStages.Contains(officeStages, stage))
        {
            return (false, "Неизвестный этап.");
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            return (false, "Нужен комментарий при смене этапа.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var previous = card.Stage;
        if (string.Equals(previous, stage, StringComparison.Ordinal))
        {
            return (true, null);
        }
        card.Stage = stage;
        card.StageChangedAtUtc = now;
        card.UpdatedAtUtc = now;
        card.LastContactAtUtc = now;
        AddHistory(
            card.Id,
            "StageChanged",
            CrmActivityDetails.WithComment($"{previous} → {stage}", comment),
            actorUserId,
            actorName,
            now);

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

        // Reassigning a card to its current manager is not a real change and must not
        // create a duplicate "Assigned" event in the activity feed.
        if (string.Equals(card.ManagerUserId, managerUserId, StringComparison.Ordinal))
        {
            return true;
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

    public async Task<CrmBulkActionResult> BulkAssignAsync(
        CrmBulkAssignRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (request.OfficeId is Guid officeId && !string.IsNullOrWhiteSpace(request.AllCardsInStage))
        {
            return await BulkAssignStageAsync(
                officeId,
                request.AllCardsInStage.Trim(),
                request.ManagerUserId,
                actorUserId,
                ct);
        }

        var cardIds = request.CardIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(500)
            .ToArray();
        var updated = 0;
        var errors = new List<string>();

        foreach (var cardId in cardIds)
        {
            if (await AssignAsync(cardId, request.ManagerUserId, actorUserId, isAdmin: true, ct))
            {
                updated++;
            }
            else
            {
                errors.Add("Не удалось изменить ответственного у одной из карточек.");
            }
        }

        return new CrmBulkActionResult(
            cardIds.Length,
            updated,
            cardIds.Length - updated,
            errors.Distinct(StringComparer.Ordinal).Take(3).ToArray());
    }

    public async Task<CrmBulkActionResult> BulkTransitionAsync(
        CrmBulkTransitionRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (request.OfficeId is Guid officeId && !string.IsNullOrWhiteSpace(request.AllCardsInStage))
        {
            return await BulkTransitionStageAsync(
                officeId,
                request.AllCardsInStage.Trim(),
                request,
                actorUserId,
                ct);
        }

        var cardIds = request.CardIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(500)
            .ToArray();
        var updated = 0;
        var errors = new List<string>();

        foreach (var cardId in cardIds)
        {
            (bool Ok, string? Error) result = request.Operation switch
            {
                CrmBulkTransitionOperations.Move => await MoveAsync(
                    cardId,
                    request.Stage ?? string.Empty,
                    request.Comment,
                    actorUserId,
                    isAdmin: true,
                    ct),
                CrmBulkTransitionOperations.Close => await CloseAsync(
                    cardId,
                    request.CloseReason ?? string.Empty,
                    request.Comment,
                    actorUserId,
                    isAdmin: true,
                    ct),
                _ => (false, "Неизвестная массовая операция.")
            };

            if (result.Ok)
            {
                updated++;
            }
            else
            {
                errors.Add(result.Error ?? "Не удалось изменить одну из карточек.");
            }
        }

        return new CrmBulkActionResult(
            cardIds.Length,
            updated,
            cardIds.Length - updated,
            errors.Distinct(StringComparer.Ordinal).Take(3).ToArray());
    }

    private async Task<CrmBulkActionResult> BulkAssignStageAsync(
        Guid officeId,
        string sourceStage,
        string managerUserId,
        string actorUserId,
        CancellationToken ct)
    {
        if (await GetManagerProfileAsync(officeId, managerUserId, ct) is null)
        {
            return new CrmBulkActionResult(0, 0, 0, ["Ответственный должен работать в выбранном офисе."]);
        }

        var cards = await db.CrmCandidateCards
            .Where(card => card.OfficeId == officeId
                           && !card.IsClosed
                           && card.Stage == sourceStage)
            .OrderBy(card => card.Id)
            .ToListAsync(ct);
        if (cards.Count == 0)
        {
            return new CrmBulkActionResult(0, 0, 0, ["В выбранном этапе нет открытых карточек."]);
        }

        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var managerName = await ResolveDisplayNameAsync(managerUserId, ct);
        var now = DateTime.UtcNow;
        foreach (var card in cards.Where(card => !string.Equals(
                     card.ManagerUserId,
                     managerUserId,
                     StringComparison.Ordinal)))
        {
            leadDistribution.AssignManually(
                card,
                managerUserId,
                managerName,
                actorUserId,
                actorName,
                now);
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(officeId);
        return new CrmBulkActionResult(cards.Count, cards.Count, 0, []);
    }

    private async Task<CrmBulkActionResult> BulkTransitionStageAsync(
        Guid officeId,
        string sourceStage,
        CrmBulkTransitionRequest request,
        string actorUserId,
        CancellationToken ct)
    {
        var comment = request.Comment?.Trim();
        if (string.IsNullOrWhiteSpace(comment))
        {
            return new CrmBulkActionResult(0, 0, 0, ["Для массового изменения нужен комментарий."]);
        }

        if (request.Operation == CrmBulkTransitionOperations.Close
            && !CrmCloseReasons.IsValid(request.CloseReason))
        {
            return new CrmBulkActionResult(0, 0, 0, ["Неизвестная причина закрытия."]);
        }

        if (request.Operation == CrmBulkTransitionOperations.Move)
        {
            var configuredStages = await db.Offices.AsNoTracking()
                .Where(office => office.Id == officeId)
                .Select(office => office.CrmStagesJson)
                .FirstOrDefaultAsync(ct);
            if (!CrmStages.Contains(CrmStages.Resolve(configuredStages), request.Stage))
            {
                return new CrmBulkActionResult(0, 0, 0, ["Неизвестный этап назначения."]);
            }
        }

        var cards = await db.CrmCandidateCards
            .Where(card => card.OfficeId == officeId
                           && !card.IsClosed
                           && card.Stage == sourceStage)
            .OrderBy(card => card.Id)
            .ToListAsync(ct);
        if (cards.Count == 0)
        {
            return new CrmBulkActionResult(0, 0, 0, ["В выбранном этапе нет открытых карточек."]);
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        if (request.Operation == CrmBulkTransitionOperations.Close)
        {
            var cardIds = cards.Select(card => card.Id).ToArray();
            var plannedMessages = await db.CrmOutboundChatMessages
                .Where(message => cardIds.Contains(message.CardId)
                                  && message.Status == CrmOutboundChatStatuses.Planned
                                  && message.CancelledAtUtc == null)
                .ToListAsync(ct);
            foreach (var message in plannedMessages)
            {
                message.CancelledAtUtc = now;
                AddHistory(message.CardId, "ChatCancelled", message.Text, actorUserId, actorName, now);
            }

            foreach (var card in cards)
            {
                card.IsClosed = true;
                card.CloseReason = request.CloseReason;
                card.ClosedAtUtc = now;
                card.IsInActiveLoad = false;
                card.UpdatedAtUtc = now;
                card.LastContactAtUtc = now;
                AddHistory(
                    card.Id,
                    "Closed",
                    CrmActivityDetails.WithComment(request.CloseReason ?? string.Empty, comment),
                    actorUserId,
                    actorName,
                    now);
            }
        }
        else
        {
            var targetStage = request.Stage!.Trim();
            foreach (var card in cards)
            {
                var previous = card.Stage;
                card.Stage = targetStage;
                card.StageChangedAtUtc = now;
                card.UpdatedAtUtc = now;
                card.LastContactAtUtc = now;
                AddHistory(
                    card.Id,
                    "StageChanged",
                    CrmActivityDetails.WithComment($"{previous} → {targetStage}", comment),
                    actorUserId,
                    actorName,
                    now);
            }
        }

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(officeId);
        return new CrmBulkActionResult(cards.Count, cards.Count, 0, []);
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

        if (string.Equals(reason, CrmCloseReasons.Success, StringComparison.Ordinal))
        {
            return (false, "Для успешного закрытия заполните отчёт и приложите обязательные файлы.");
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
        if (card.IsClosed) return (false, "Карточка уже закрыта.");
        var commentText = comment.Trim();
        card.IsClosed = true;
        card.CloseReason = reason;
        card.ClosedAtUtc = now;
        card.IsInActiveLoad = false;
        card.UpdatedAtUtc = now;
        card.LastContactAtUtc = now;
        var pendingMessages = await db.CrmOutboundChatMessages
            .Where(message => message.CardId == card.Id
                              && message.Status == CrmOutboundChatStatuses.Planned
                              && message.CancelledAtUtc == null)
            .ToListAsync(ct);
        foreach (var message in pendingMessages)
        {
            message.CancelledAtUtc = now;
            AddHistory(card.Id, "ChatCancelled", message.Text, actorUserId, actorName, now);
        }
        AddHistory(
            card.Id,
            "Closed",
            CrmActivityDetails.WithComment(reason, commentText),
            actorUserId,
            actorName,
            now);

        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CloseSuccessAsync(
        Guid cardId,
        string? comment,
        string? contractMissingReason,
        IReadOnlyList<CrmSuccessDocumentUpload> uploads,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (successDocuments is null)
        {
            return (false, "Хранилище отчётов успешного закрытия не настроено.");
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            return (false, "При успешном закрытии обязателен комментарий.");
        }

        var normalizedContractMissingReason = string.IsNullOrWhiteSpace(contractMissingReason)
            ? null
            : contractMissingReason.Trim();
        var validationError = await ValidateSuccessReportAsync(uploads, normalizedContractMissingReason, ct);
        if (validationError is not null)
        {
            return (false, validationError);
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }

        if (card.IsClosed)
        {
            return (false, "Карточка уже закрыта.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var stored = new List<CrmSuccessDocumentEntity>(uploads.Count);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var upload in uploads)
            {
                var document = new CrmSuccessDocumentEntity
                {
                    Id = Guid.NewGuid(),
                    CardId = card.Id,
                    Category = upload.Category,
                    FileName = NormalizeUploadFileName(upload.FileName),
                    ContentType = string.IsNullOrWhiteSpace(upload.ContentType)
                        ? "application/octet-stream"
                        : upload.ContentType.Trim()[..Math.Min(upload.ContentType.Trim().Length, 128)],
                    SizeBytes = upload.Length,
                    UploadedByUserId = actorUserId,
                    UploadedByName = actorName,
                    CreatedAtUtc = now
                };
                await using var content = upload.OpenReadStream();
                document.RelativePath = await successDocuments.SaveAsync(card.Id, document.Id, content, ct);
                stored.Add(document);
                db.CrmSuccessDocuments.Add(document);
            }

            card.IsClosed = true;
            card.CloseReason = CrmCloseReasons.Success;
            card.ClosedAtUtc = now;
            card.IsInActiveLoad = false;
            card.UpdatedAtUtc = now;
            card.LastContactAtUtc = now;
            card.SuccessContractMissingReason = uploads.Any(x =>
                string.Equals(x.Category, CrmSuccessDocumentCategories.Contract, StringComparison.Ordinal))
                ? null
                : normalizedContractMissingReason;

            var pendingMessages = await db.CrmOutboundChatMessages
                .Where(message => message.CardId == card.Id
                                  && message.Status == CrmOutboundChatStatuses.Planned
                                  && message.CancelledAtUtc == null)
                .ToListAsync(ct);
            foreach (var message in pendingMessages)
            {
                message.CancelledAtUtc = now;
                AddHistory(card.Id, "ChatCancelled", message.Text, actorUserId, actorName, now);
            }

            AddHistory(
                card.Id,
                "SuccessReportUploaded",
                card.SuccessContractMissingReason is null
                    ? $"Загружено файлов: {stored.Count}"
                    : $"Загружено файлов: {stored.Count}. Фото контракта отсутствует: {card.SuccessContractMissingReason}",
                actorUserId,
                actorName,
                now);
            AddHistory(
                card.Id,
                "Closed",
                CrmActivityDetails.WithComment(CrmCloseReasons.Success, comment.Trim()),
                actorUserId,
                actorName,
                now);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var document in stored)
            {
                successDocuments.TryDelete(document.RelativePath);
            }
            return (false, "Не удалось сохранить отчёт. Карточка не закрыта — попробуйте ещё раз.");
        }

        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> UpdateSuccessReportAsync(
        Guid cardId,
        IReadOnlyCollection<Guid> keptDocumentIds,
        string? contractMissingReason,
        IReadOnlyList<CrmSuccessDocumentUpload> uploads,
        string actorUserId,
        bool canEditReport,
        CancellationToken ct = default)
    {
        if (!canEditReport)
        {
            return (false, "Редактировать отчёт может только управляющий или администратор.");
        }
        if (successDocuments is null)
        {
            return (false, "Хранилище отчётов успешного закрытия не настроено.");
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin: true, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }
        if (!card.IsClosed || !string.Equals(card.CloseReason, CrmCloseReasons.Success, StringComparison.Ordinal))
        {
            return (false, "Редактировать отчёт можно только у карточки, закрытой в успех.");
        }

        var existingDocuments = await db.CrmSuccessDocuments
            .Where(document => document.CardId == cardId)
            .ToListAsync(ct);
        var existingIds = existingDocuments.Select(document => document.Id).ToHashSet();
        var requestedKeptIds = keptDocumentIds.Where(documentId => documentId != Guid.Empty).ToHashSet();
        if (requestedKeptIds.Any(documentId => !existingIds.Contains(documentId)))
        {
            return (false, "Один из сохраняемых файлов не относится к этому отчёту.");
        }

        var keptDocuments = existingDocuments
            .Where(document => requestedKeptIds.Contains(document.Id))
            .ToList();
        var removedDocuments = existingDocuments
            .Where(document => !requestedKeptIds.Contains(document.Id))
            .ToList();
        var normalizedContractMissingReason = string.IsNullOrWhiteSpace(contractMissingReason)
            ? null
            : contractMissingReason.Trim();
        var validationError = await ValidateSuccessReportAsync(
            uploads,
            normalizedContractMissingReason,
            ct,
            keptDocuments);
        if (validationError is not null)
        {
            return (false, validationError);
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var stored = new List<CrmSuccessDocumentEntity>(uploads.Count);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var upload in uploads)
            {
                var document = new CrmSuccessDocumentEntity
                {
                    Id = Guid.NewGuid(),
                    CardId = card.Id,
                    Category = upload.Category,
                    FileName = NormalizeUploadFileName(upload.FileName),
                    ContentType = string.IsNullOrWhiteSpace(upload.ContentType)
                        ? "application/octet-stream"
                        : upload.ContentType.Trim()[..Math.Min(upload.ContentType.Trim().Length, 128)],
                    SizeBytes = upload.Length,
                    UploadedByUserId = actorUserId,
                    UploadedByName = actorName,
                    CreatedAtUtc = now
                };
                await using var content = upload.OpenReadStream();
                document.RelativePath = await successDocuments.SaveAsync(card.Id, document.Id, content, ct);
                stored.Add(document);
                db.CrmSuccessDocuments.Add(document);
            }

            db.CrmSuccessDocuments.RemoveRange(removedDocuments);
            var hasContractPhoto = keptDocuments.Concat(stored).Any(document =>
                string.Equals(document.Category, CrmSuccessDocumentCategories.Contract, StringComparison.Ordinal));
            card.SuccessContractMissingReason = hasContractPhoto ? null : normalizedContractMissingReason;
            card.UpdatedAtUtc = now;
            AddHistory(
                card.Id,
                "SuccessReportUpdated",
                $"Файлов в отчёте: {keptDocuments.Count + stored.Count}. Добавлено: {stored.Count}. Удалено: {removedDocuments.Count}",
                actorUserId,
                actorName,
                now);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            foreach (var document in stored)
            {
                successDocuments.TryDelete(document.RelativePath);
            }
            return (false, "Не удалось сохранить изменения отчёта. Исходные файлы не изменены.");
        }

        foreach (var document in removedDocuments)
        {
            successDocuments.TryDelete(document.RelativePath);
        }
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(Stream? Stream, string? FileName, string? ContentType)> OpenSuccessDocumentAsync(
        Guid cardId,
        Guid documentId,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (successDocuments is null)
        {
            return (null, null, null);
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (null, null, null);
        }

        var document = await db.CrmSuccessDocuments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == documentId && x.CardId == cardId, ct);
        if (document is null)
        {
            return (null, null, null);
        }

        return (successDocuments.OpenRead(document.RelativePath), document.FileName, document.ContentType);
    }

    public async Task<(Stream? Stream, string? FileName)> OpenSuccessReportArchiveAsync(
        Guid cardId,
        string actorUserId,
        bool isElevated,
        CancellationToken ct = default)
    {
        if (successDocuments is null)
        {
            return (null, null);
        }

        var card = await FindAccessibleCardAsync(cardId, actorUserId, isElevated, ct);
        if (card is null
            || !card.IsClosed
            || !string.Equals(card.CloseReason, CrmCloseReasons.Success, StringComparison.Ordinal))
        {
            return (null, null);
        }

        var documents = await db.CrmSuccessDocuments.AsNoTracking()
            .Where(document => document.CardId == cardId)
            .ToListAsync(ct);
        documents = documents
            .OrderBy(document => SuccessReportArchiveOrder(document.Category))
            .ThenBy(document => document.CreatedAtUtc)
            .ThenBy(document => document.FileName)
            .ToList();
        if (documents.Count == 0)
        {
            return (null, null);
        }

        var candidateName = await db.CandidateResponses.AsNoTracking()
            .Where(response => response.Id == card.ResponseId)
            .Select(response => response.FullName)
            .FirstOrDefaultAsync(ct);
        var safeCandidateName = SanitizeArchiveSegment(candidateName, "Кандидат", 80);
        var reportDate = DateTimeUtcHelper.EnsureUtc(card.ClosedAtUtc ?? card.UpdatedAtUtc).ToString("yyyy-MM-dd");
        var downloadFileName = $"Отчёт-{safeCandidateName}-{reportDate}.zip";
        var tempPath = Path.Combine(Path.GetTempPath(), $"orbita-success-report-{Guid.NewGuid():N}.zip");

        try
        {
            await using (var output = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var manifest = archive.CreateEntry("Отчёт.txt", CompressionLevel.Fastest);
                await using (var manifestStream = manifest.Open())
                {
                    var manifestBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                        .GetBytes(BuildSuccessReportManifest(card, candidateName, documents));
                    await manifestStream.WriteAsync(manifestBytes, ct);
                }
                usedEntryNames.Add(manifest.FullName);

                foreach (var document in documents)
                {
                    await using var source = successDocuments.OpenRead(document.RelativePath)
                        ?? throw new FileNotFoundException("Файл отчёта отсутствует в хранилище.", document.RelativePath);
                    var folder = SuccessReportArchiveFolder(document.Category);
                    var fileName = SanitizeArchiveSegment(document.FileName, "Файл", 140);
                    var entryName = EnsureUniqueArchiveEntryName(folder, fileName, usedEntryNames);
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await source.CopyToAsync(entryStream, ct);
                }
            }

            var stream = new FileStream(
                tempPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (stream, downloadFileName);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // The response must fail closed even if temporary-file cleanup is delayed.
            }
            return (null, null);
        }
    }

    public async Task<bool> ReopenAsync(Guid cardId, string actorUserId, bool isAdmin, CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return false;
        }

        if (!card.IsClosed) return true;

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
        var normalized = text?.Trim();
        if (card is null || string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4000)
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
            Text = normalized,
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

        var response = card.Response;
        var sourceResponseId = request.SourceResponseId is null ? response.SourceResponseId : Clamp(request.SourceResponseId, 128);
        var accountName = request.AccountName is null ? response.AccountName : Clamp(request.AccountName, 256);
        // The CRM deliberately does not disclose source or advertisement URLs.
        // Preserve those integration-managed values when the CRM updates other card fields.
        var sourceUrl = request.SourceUrl is null ? response.SourceUrl : Clamp(request.SourceUrl, 1024);
        var vacancyUrl = request.VacancyUrl is null ? response.VacancyUrl : Clamp(request.VacancyUrl, 1024);
        var messengerUrl = request.MessengerUrl is null ? response.MessengerUrl : Clamp(request.MessengerUrl, 1024);
        var citizenship = request.Citizenship is null
            ? card.Response.Citizenship
            : CandidateCitizenshipResolver.Normalize(request.Citizenship);

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
        Track("гражданство", response.Citizenship, citizenship);

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
        response.Citizenship = citizenship;

        var locks = response.OperatorLockedFields;
        if (changes.Contains("ФИО"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.FullName);
        }

        if (changes.Contains("город"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.City);
        }

        if (changes.Contains("вакансия"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.Vacancy);
        }

        if (changes.Contains("возраст"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.Age);
        }

        if (changes.Contains("объявление"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.VacancyUrl);
        }

        if (changes.Contains("ссылка"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.SourceUrl);
        }

        if (changes.Contains("мессенджер"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.MessengerUrl);
        }

        if (changes.Contains("гражданство"))
        {
            locks = ResponseOperatorLocks.Add(locks, ResponseOperatorLocks.Citizenship);
        }

        response.OperatorLockedFields = locks;

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

    public async Task<(bool Ok, string? Error)> QueueChatMessageAsync(
        Guid cardId,
        string text,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await db.CrmCandidateCards
            .Include(x => x.Response)
            .FirstOrDefaultAsync(x => x.Id == cardId, ct);
        if (card is null || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, actorUserId, isAdmin, ct))
        {
            return (false, "Карточка не найдена.");
        }

        var canEdit = isAdmin || string.Equals(card.ManagerUserId, actorUserId, StringComparison.Ordinal);
        if (!canEdit)
        {
            return (false, "Недостаточно прав, чтобы писать в чат.");
        }

        if (card.IsClosed)
        {
            return (false, "Нельзя писать в чат закрытой карточки.");
        }

        if (string.IsNullOrWhiteSpace(card.Response.SourceResponseId))
        {
            return (false, "Нет привязки к отклику Avito — сообщение нельзя поставить в очередь.");
        }

        var normalized = text?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return (false, "Введите текст сообщения.");
        }

        if (normalized.Length > CrmOutboundChatStatuses.MaxTextLength)
        {
            return (false, $"Сообщение длиннее {CrmOutboundChatStatuses.MaxTextLength} символов.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        db.CrmOutboundChatMessages.Add(new CrmOutboundChatMessageEntity
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            ResponseId = card.ResponseId,
            AuthorUserId = actorUserId,
            AuthorName = actorName,
            Text = normalized,
            Status = CrmOutboundChatStatuses.Planned,
            CreatedAtUtc = now
        });
        card.LastContactAtUtc = now;
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "ChatQueued", normalized, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> CancelChatMessageAsync(
        Guid cardId,
        Guid messageId,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена.");
        }

        var message = await db.CrmOutboundChatMessages.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == messageId && x.CardId == cardId, ct);
        if (message is null || message.CancelledAtUtc is not null)
        {
            return (false, "Сообщение не найдено.");
        }

        if (!string.Equals(message.Status, CrmOutboundChatStatuses.Planned, StringComparison.Ordinal))
        {
            return (false, "Отменить можно только запланированное сообщение.");
        }

        if (!isAdmin && !string.Equals(message.AuthorUserId, actorUserId, StringComparison.Ordinal))
        {
            return (false, "Недостаточно прав, чтобы отменить это сообщение.");
        }

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        var cancellable = db.CrmOutboundChatMessages
            .Where(x => x.Id == messageId
                        && x.CardId == cardId
                        && x.Status == CrmOutboundChatStatuses.Planned
                        && x.CancelledAtUtc == null);
        var cancelled = 0;
        if (db.Database.IsRelational())
        {
            cancelled = await cancellable.ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CancelledAtUtc, now), ct);
        }
        else if (await cancellable.SingleOrDefaultAsync(ct) is { } trackedMessage)
        {
            trackedMessage.CancelledAtUtc = now;
            await db.SaveChangesAsync(ct);
            cancelled = 1;
        }
        if (cancelled != 1)
        {
            return (false, "Сообщение уже передано воркеру для отправки.");
        }

        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "ChatCancelled", message.Text, actorUserId, actorName, now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
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
            var managerProfile = await GetManagerProfileAsync(
                officeId,
                actorUserId,
                ct,
                allowAdmin: false);
            if (managerProfile is null)
            {
                return (null, "Создавать отклики вручную могут только сотрудники CRM своего офиса.");
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
        var citizenship = CandidateCitizenshipResolver.Normalize(request.Citizenship);

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
            Citizenship = citizenship,
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
            EnteredCrmAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            StageChangedAtUtc = now,
            IsInActiveLoad = true
        };
        var assignToActor = !isAdmin || request.AssignToMe;
        if (assignToActor)
        {
            card.ManagerUserId = actorUserId;
            card.InitialManagerUserId = actorUserId;
            card.InitialAssignedAtUtc = now;
        }

        db.CrmCandidateCards.Add(card);
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        AddHistory(card.Id, "Created", "Карточка создана вручную", actorUserId, actorName, now);
        if (assignToActor)
        {
            AddHistory(card.Id, "Assigned", actorName, actorUserId, actorName, now, actorUserId);
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

    public async Task<(CrmLeadFileImportResult? Result, string? Error)> ImportLeadFileAsync(
        Guid officeId,
        CrmLeadFileImportRequest request,
        string actorUserId,
        CancellationToken ct = default)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            return (null, "В файле не найдено ни одного лида.");
        }

        if (request.Entries.Count > CrmLeadFileParser.MaximumEntries)
        {
            return (null, $"В одном файле допускается не более {CrmLeadFileParser.MaximumEntries} лидов.");
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

        var officeStages = CrmStages.Resolve(office.CrmStagesJson);
        if (!officeStages.Contains(CrmStages.Lead, StringComparer.Ordinal))
        {
            return (null, "В воронке офиса отсутствует этап «Лид».");
        }

        var normalizedEntries = new List<(CrmLeadFileImportEntry Entry, string PhoneNormalized)>();
        var seenPhones = new HashSet<string>(StringComparer.Ordinal);
        var defensiveDuplicates = 0;
        foreach (var entry in request.Entries)
        {
            var fullName = CollapseLeadImportWhitespace(entry.FullName);
            var phoneRaw = entry.PhoneRaw?.Trim() ?? string.Empty;
            var phoneNormalized = _phoneNormalizer.Normalize(phoneRaw);
            if (fullName.Length == 0 || fullName.Length > 256 || string.IsNullOrWhiteSpace(phoneNormalized))
            {
                return (null, $"Некорректная запись в строке {entry.SourceLine}.");
            }

            if (!seenPhones.Add(phoneNormalized))
            {
                defensiveDuplicates++;
                continue;
            }

            normalizedEntries.Add((entry with
            {
                FullName = fullName,
                PhoneRaw = phoneRaw,
                Vacancy = string.IsNullOrWhiteSpace(entry.Vacancy)
                    ? null
                    : entry.Vacancy.Trim()[..Math.Min(entry.Vacancy.Trim().Length, 512)]
            }, phoneNormalized));
        }

        // One person may be listed more than once with different contact numbers.
        // Group before creating and assigning cards so the person is distributed only once,
        // while every unique phone remains available in the card contacts.
        var entryGroups = normalizedEntries
            .GroupBy(
                x => BuildLeadImportGroupKey(x.Entry.FullName, x.PhoneNormalized),
                StringComparer.Ordinal)
            .Select(x => x.ToList())
            .ToList();

        var normalizedPhones = normalizedEntries.Select(x => x.PhoneNormalized).ToArray();
        var existingPrimaryPhones = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.OfficeId == officeId && normalizedPhones.Contains(x.Response.PhoneNormalized))
            .Select(x => x.Response.PhoneNormalized)
            .Distinct()
            .ToListAsync(ct);

        // Additional contact numbers must also protect against re-imports. Otherwise a
        // secondary phone from a previously grouped person could create a second card.
        var existingContactPhones = await db.CandidateContactPhones.AsNoTracking()
            .Where(x => x.Person.OfficeId == officeId && normalizedPhones.Contains(x.PhoneNormalized))
            .Select(x => x.PhoneNormalized)
            .Distinct()
            .ToListAsync(ct);
        var existingPhoneSet = existingPrimaryPhones
            .Concat(existingContactPhones)
            .ToHashSet(StringComparer.Ordinal);
        var groupsToCreate = entryGroups
            .Where(group => group.All(x => !existingPhoneSet.Contains(x.PhoneNormalized)))
            .ToList();
        var skippedExistingGroups = entryGroups.Count - groupsToCreate.Count;
        var duplicateRows = Math.Max(0, request.DuplicateRowsInFile) + defensiveDuplicates;

        if (groupsToCreate.Count == 0)
        {
            return (new CrmLeadFileImportResult(
                entryGroups.Count,
                0,
                skippedExistingGroups,
                duplicateRows,
                0,
                0), null);
        }

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var now = DateTime.UtcNow;
        var actorName = await ResolveDisplayNameAsync(actorUserId, ct);
        const string initialStage = CrmStages.Lead;
        var safeFileName = Path.GetFileName(request.FileName ?? string.Empty).Trim();
        if (safeFileName.Length == 0)
        {
            safeFileName = "список лидов.txt";
        }
        else if (safeFileName.Length > 180)
        {
            safeFileName = safeFileName[..180];
        }

        var cards = new List<CrmCandidateCardEntity>(groupsToCreate.Count);
        foreach (var group in groupsToCreate)
        {
            var (entry, phoneNormalized) = group[0];
            var (firstName, lastName, middleName) = _candidateParser.ParseName(entry.FullName);
            var person = new CandidatePersonEntity
            {
                Id = Guid.NewGuid(),
                OfficeId = officeId,
                FullName = entry.FullName,
                FirstName = firstName,
                LastName = lastName,
                MiddleName = middleName,
                City = string.Empty,
                PhoneRaw = entry.PhoneRaw,
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
                AccountName = "Импорт файла",
                Source = "FileImport",
                SourceResponseId = $"file-{Guid.NewGuid():N}",
                FullName = entry.FullName,
                FirstName = firstName,
                LastName = lastName,
                MiddleName = middleName,
                Citizenship = CandidateCitizenshipResolver.Normalize(null),
                PhoneRaw = entry.PhoneRaw,
                PhoneNormalized = phoneNormalized,
                City = string.Empty,
                Vacancy = entry.Vacancy ?? string.Empty,
                Status = ResponseStatuses.New,
                CreatedAt = now,
                CollectedAt = now
            };
            var card = new CrmCandidateCardEntity
            {
                Id = Guid.NewGuid(),
                ResponseId = response.Id,
                OfficeId = officeId,
                Stage = initialStage,
                EnteredCrmAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                StageChangedAtUtc = now,
                IsInActiveLoad = true
            };

            db.CandidatePersons.Add(person);
            db.CandidateResponses.Add(response);
            for (var phoneIndex = 0; phoneIndex < group.Count; phoneIndex++)
            {
                var (phoneEntry, contactPhoneNormalized) = group[phoneIndex];
                db.CandidateContactPhones.Add(new CandidateContactPhoneEntity
                {
                    Id = Guid.NewGuid(),
                    PersonId = person.Id,
                    PhoneRaw = phoneEntry.PhoneRaw,
                    PhoneNormalized = contactPhoneNormalized,
                    IsPrimary = phoneIndex == 0,
                    CreatedAtUtc = now,
                    CreatedByUserId = actorUserId
                });
                db.CandidatePhoneHistory.Add(new CandidatePhoneHistoryEntity
                {
                    Id = Guid.NewGuid(),
                    PersonId = person.Id,
                    ResponseId = response.Id,
                    PhoneRaw = phoneEntry.PhoneRaw,
                    PhoneNormalized = contactPhoneNormalized,
                    RecordedAtUtc = now
                });
            }
            db.CrmCandidateCards.Add(card);
            var sourceLines = string.Join(", ", group
                .Select(x => x.Entry.SourceLine)
                .Distinct()
                .OrderBy(x => x));
            AddHistory(
                card.Id,
                "Created",
                group.Count == 1
                    ? $"Карточка создана из файла «{safeFileName}», строка {sourceLines}"
                    : $"Карточка создана из файла «{safeFileName}», строки {sourceLines}; телефонов: {group.Count}",
                actorUserId,
                actorName,
                now);
            cards.Add(card);
        }

        var (assignedCount, managersOnShift) = await leadDistribution.AssignImportedLeadBatchAsync(
            officeId,
            cards,
            actorUserId,
            actorName,
            ct);
        if (managersOnShift == 0)
        {
            return (null, "В офисе нет менеджеров или старших менеджеров на смене. Импорт отменён.");
        }

        try
        {
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        catch (DbUpdateException)
        {
            return (null, "Не удалось импортировать файл. Данные не были сохранены.");
        }

        NotifyBoardChanged(officeId);
        return (new CrmLeadFileImportResult(
            entryGroups.Count,
            cards.Count,
            skippedExistingGroups,
            duplicateRows,
            assignedCount,
            managersOnShift), null);
    }

    private static string CollapseLeadImportWhitespace(string? value) =>
        string.Join(" ", (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string NormalizeLeadImportFullNameKey(string value) =>
        CollapseLeadImportWhitespace(value)
            .Replace('ё', 'е')
            .Replace('Ё', 'Е')
            .ToUpperInvariant();

    private static string BuildLeadImportGroupKey(string fullName, string phoneNormalized)
    {
        var collapsedName = CollapseLeadImportWhitespace(fullName);
        var words = collapsedName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A single word is not a reliable full-name identity: it may be a first name,
        // a profession or another annotation. Keep such rows independent so malformed
        // input can never merge several people and their phones into one card.
        return words.Length >= 2
            ? $"name:{NormalizeLeadImportFullNameKey(collapsedName)}"
            : $"phone:{phoneNormalized}";
    }

    public async Task<(bool Ok, string? Error)> UpdateNoteAsync(
        Guid cardId,
        Guid noteId,
        string text,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена или недоступна для изменения.");
        }

        var note = await db.CrmCandidateNotes.FirstOrDefaultAsync(x => x.Id == noteId && x.CardId == cardId, ct);
        if (note is null)
        {
            return (false, "Комментарий не найден.");
        }

        if (!isAdmin && note.AuthorUserId != actorUserId)
        {
            return (false, "Изменить комментарий может его автор или администратор.");
        }

        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4000)
        {
            return (false, "Комментарий обязателен и не должен превышать 4000 символов.");
        }

        var now = DateTime.UtcNow;
        note.Text = normalized;
        note.UpdatedAtUtc = now;
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "NoteUpdated", null, actorUserId, await ResolveDisplayNameAsync(actorUserId, ct), now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteNoteAsync(
        Guid cardId,
        Guid noteId,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена или недоступна для изменения.");
        }

        var note = await db.CrmCandidateNotes.FirstOrDefaultAsync(x => x.Id == noteId && x.CardId == cardId, ct);
        if (note is null)
        {
            return (false, "Комментарий не найден.");
        }

        if (!isAdmin && note.AuthorUserId != actorUserId)
        {
            return (false, "Удалить комментарий может его автор или администратор.");
        }

        var now = DateTime.UtcNow;
        db.CrmCandidateNotes.Remove(note);
        card.UpdatedAtUtc = now;
        AddHistory(card.Id, "NoteDeleted", null, actorUserId, await ResolveDisplayNameAsync(actorUserId, ct), now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> SetNotePinnedAsync(
        Guid cardId,
        Guid noteId,
        bool isPinned,
        string actorUserId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var card = await FindAccessibleCardAsync(cardId, actorUserId, isAdmin, ct);
        if (card is null)
        {
            return (false, "Карточка не найдена или недоступна для изменения.");
        }

        var note = await db.CrmCandidateNotes.FirstOrDefaultAsync(x => x.Id == noteId && x.CardId == cardId, ct);
        if (note is null)
        {
            return (false, "Комментарий не найден.");
        }

        if (note.IsPinned == isPinned)
        {
            return (true, null);
        }

        var now = DateTime.UtcNow;
        note.IsPinned = isPinned;
        note.UpdatedAtUtc = now;
        card.UpdatedAtUtc = now;
        AddHistory(
            card.Id,
            isPinned ? "NotePinned" : "NoteUnpinned",
            null,
            actorUserId,
            await ResolveDisplayNameAsync(actorUserId, ct),
            now);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(card.OfficeId);
        return (true, null);
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
            || !CrmTaskTypes.IsValid(request.TaskType)
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
            TaskType = request.TaskType,
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
                task.Importance,
                task.TaskType,
                task.UpdatedAtUtc);
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
            new CrmTaskCreateRequest(
                cardId,
                label,
                null,
                actorUserId,
                due,
                CrmTaskImportances.Medium,
                CrmTaskTypes.CallBack),
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
            return (false, "Изменять задачу может её автор, ответственный или администратор.");
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

        var taskType = request.TaskType ?? task.TaskType;
        if (!CrmTaskTypes.IsValid(taskType))
        {
            return (false, "Выберите тип задачи.");
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
        task.TaskType = taskType;
        task.UpdatedAtUtc = now;
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
            return (false, "Отменять задачу может её автор, ответственный или администратор.");
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
            return (false, "Возвращать задачу в работу может её автор, ответственный или администратор.");
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

    public async Task<(bool Ok, string? Error)> DeleteTaskAsync(
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
            return (false, "Удалить задачу может её автор, ответственный или администратор.");
        }

        var attachmentPaths = taskAttachments is null
            ? []
            : await db.CrmTaskAttachments.AsNoTracking()
                .Where(x => x.TaskId == taskId)
                .Select(x => x.RelativePath)
                .ToListAsync(ct);
        var now = DateTime.UtcNow;
        if (deadlineNotifications is not null)
        {
            await deadlineNotifications.DismissTaskAsync(task.Id, now, ct);
        }

        if (task.CardId is Guid cardId)
        {
            AddHistory(cardId, "TaskDeleted", task.Title, userId, await ResolveDisplayNameAsync(userId, ct), now);
            // Исключаем удаляемую задачу из расчёта следующего действия карточки.
            task.Status = CrmTaskStatuses.Cancelled;
            await RefreshTaskCardNextActionAsync(task, now, markContact: false, ct);
        }

        var officeId = task.OfficeId;
        db.CrmTasks.Remove(task);
        await db.SaveChangesAsync(ct);
        if (taskAttachments is not null)
        {
            foreach (var path in attachmentPaths)
            {
                taskAttachments.TryDelete(path);
            }
        }

        NotifyBoardChanged(officeId);
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
        var commentEntities = await db.CrmTaskComments.AsNoTracking()
            .Where(x => x.TaskId == taskId)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        var comments = commentEntities.Select(x =>
        {
            var canManageComment = isAdmin || x.AuthorUserId == userId;
            return new CrmTaskCommentDto(
                x.Id,
                x.TaskId,
                x.AuthorUserId,
                x.AuthorName,
                x.Text,
                x.CreatedAtUtc,
                x.UpdatedAtUtc,
                canManageComment,
                canManageComment);
        }).ToList();
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

    public async Task<(Stream? Stream, string? FileName, string? ContentType)> OpenCallRecordingAsync(
        Guid callId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (callRecordings is null)
        {
            return (null, null, null);
        }

        var call = await db.CrmCalls.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == callId, ct);
        if (call?.CardId is not Guid cardId || string.IsNullOrWhiteSpace(call.RecordingStoragePath))
        {
            return (null, null, null);
        }

        var card = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.Id == cardId)
            .Select(x => new { x.OfficeId, x.ManagerUserId })
            .FirstOrDefaultAsync(ct);
        if (card is null || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct))
        {
            return (null, null, null);
        }

        return (
            callRecordings.OpenRead(call.RecordingStoragePath),
            string.IsNullOrWhiteSpace(call.RecordingFileName) ? $"Звонок-{call.Id:N}.wav" : call.RecordingFileName,
            string.IsNullOrWhiteSpace(call.RecordingContentType) ? "audio/wav" : call.RecordingContentType);
    }

    public async Task<CrmCallAiInsightDto?> GetCallAiInsightAsync(
        Guid callId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var call = await db.CrmCalls.AsNoTracking()
            .Where(x => x.Id == callId)
            .Select(x => new { x.CardId })
            .FirstOrDefaultAsync(ct);
        if (call?.CardId is not Guid cardId) return null;

        var card = await db.CrmCandidateCards.AsNoTracking()
            .Where(x => x.Id == cardId)
            .Select(x => new { x.OfficeId, x.ManagerUserId })
            .FirstOrDefaultAsync(ct);
        if (card is null || !await CanAccessCardAsync(card.OfficeId, card.ManagerUserId, userId, isAdmin, ct))
        {
            return null;
        }

        var insight = await db.CrmCallAiInsights.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CallId == callId, ct);
        if (insight is null) return null;

        return new CrmCallAiInsightDto(
            insight.CallId,
            insight.Status,
            insight.TranscriptText,
            DeserializeOrEmpty<CrmCallTranscriptSegmentDto>(insight.SegmentsJson),
            DeserializeOrDefault<CrmCallAiAnalysisDto>(insight.AnalysisJson),
            insight.AnalysisRawText,
            insight.LastErrorMessage,
            insight.UpdatedAtUtc);
    }

    public async Task<CrmTaskCommentDto?> AddTaskCommentAsync(
        Guid taskId,
        string text,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4000)
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
            Text = normalized,
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
            comment.CreatedAtUtc,
            null,
            true,
            true);
    }

    public async Task<(bool Ok, string? Error)> UpdateTaskCommentAsync(
        Guid taskId,
        Guid commentId,
        string text,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return (false, "Задача не найдена.");
        }

        var comment = await db.CrmTaskComments.FirstOrDefaultAsync(x => x.Id == commentId && x.TaskId == taskId, ct);
        if (comment is null)
        {
            return (false, "Комментарий задачи не найден.");
        }

        if (!isAdmin && comment.AuthorUserId != userId)
        {
            return (false, "Изменить комментарий может его автор или администратор.");
        }

        var normalized = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 4000)
        {
            return (false, "Комментарий обязателен и не должен превышать 4000 символов.");
        }

        comment.Text = normalized;
        comment.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteTaskCommentAsync(
        Guid taskId,
        Guid commentId,
        string userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        var task = await db.CrmTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || !CanAccessTask(task, userId, isAdmin))
        {
            return (false, "Задача не найдена.");
        }

        var comment = await db.CrmTaskComments.FirstOrDefaultAsync(x => x.Id == commentId && x.TaskId == taskId, ct);
        if (comment is null)
        {
            return (false, "Комментарий задачи не найден.");
        }

        if (!isAdmin && comment.AuthorUserId != userId)
        {
            return (false, "Удалить комментарий может его автор или администратор.");
        }

        db.CrmTaskComments.Remove(comment);
        await db.SaveChangesAsync(ct);
        NotifyBoardChanged(task.OfficeId);
        return (true, null);
    }

    public async Task<int> CountOpenTasksAsync(Guid officeId, string userId, bool isAdmin, CancellationToken ct = default) =>
        await db.CrmTasks.CountAsync(
            x => x.OfficeId == officeId
                 && x.Status == CrmTaskStatuses.Open
                 && (isAdmin || x.AssigneeUserId == userId),
            ct);

    private void AddHistory(
        Guid cardId,
        string action,
        string? details,
        string actorUserId,
        string actorName,
        DateTime? at = null,
        string? targetUserId = null) =>
        db.AddCrmHistory(new CrmCandidateHistoryEntity
        {
            Id = Guid.NewGuid(),
            CardId = cardId,
            Action = action,
            Details = details,
            TargetUserId = targetUserId,
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
        isAdmin || task.CreatorUserId == userId || task.AssigneeUserId == userId;

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
        int chatUnreadCount = 0,
        string? officeName = null,
        IReadOnlyList<string>? contactPhones = null)
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
            null,
            card.Stage,
            card.ManagerUserId,
            card.ManagerUserId is null ? null : names.GetValueOrDefault(card.ManagerUserId, card.ManagerUserId),
            CrmManagerLoadRules.CountsTowardsLoad(card.Stage, card.IsInActiveLoad, card.IsClosed, officeName),
            card.CreatedAtUtc,
            stageAt,
            card.LastContactAtUtc,
            card.NextActionAtUtc,
            card.IsClosed,
            card.CloseReason,
            openTaskCount,
            hasOverdue,
            Math.Round(hours, 1),
            null,
            null,
            null,
            null,
            chatUnreadCount,
            CandidateCitizenshipResolver.Resolve(
                card.Response.Citizenship,
                card.Response.RawText,
                card.Response.ChatMessagesJson),
            contactPhones,
            !string.IsNullOrWhiteSpace(card.Response.SourceResponseId));
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
            task.Importance,
            task.TaskType,
            task.UpdatedAtUtc);

    private static IReadOnlyList<CrmActivityItemDto> BuildActivity(
        IReadOnlyList<CrmCandidateNoteEntity> notes,
        IReadOnlyList<CrmTaskEntity> tasks,
        IReadOnlyList<CrmTaskCommentEntity> taskComments,
        IReadOnlyList<CrmCandidateHistoryEntity> history,
        IReadOnlyList<CrmCallEntity> calls,
        IReadOnlyDictionary<Guid, string> callAiStatuses,
        IReadOnlyDictionary<string, string> names,
        string userId,
        bool isAdmin,
        bool canEditCard)
    {
        var items = new List<CrmActivityItemDto>();
        items.AddRange(notes.Select(n =>
        {
            var canManageNote = isAdmin || n.AuthorUserId == userId;
            return new CrmActivityItemDto(
                "note",
                n.IsPinned
                    ? "Закреплённый комментарий"
                    : "Комментарий",
                n.Text,
                n.AuthorName,
                n.CreatedAtUtc,
                NoteId: n.Id,
                IsPinned: n.IsPinned,
                CanEdit: canManageNote,
                CanDelete: canManageNote,
                CanPin: canEditCard,
                UpdatedAtUtc: n.UpdatedAtUtc);
        }));
        var commentsByTask = taskComments
            .GroupBy(comment => comment.TaskId)
            .ToDictionary(group => group.Key, group => group.ToList());
        items.AddRange(tasks.Select(t =>
        {
            var completionComment = FindCompletionComment(t, commentsByTask);
            var creatorName = string.IsNullOrWhiteSpace(t.CreatorName)
                ? names.GetValueOrDefault(t.CreatorUserId, t.CreatorUserId)
                : t.CreatorName;
            return new CrmActivityItemDto(
                t.Status == CrmTaskStatuses.Completed ? "task-done" : "task",
                t.Title,
                t.Description,
                completionComment?.AuthorName ?? creatorName,
                t.CompletedAtUtc ?? t.UpdatedAtUtc ?? t.CreatedAtUtc,
                t.Id,
                CompletionReason: completionComment?.Text);
        }));
        items.AddRange(history
            .Where(h => h.Action is not "Note"
                and not "NoteUpdated"
                and not "NotePinned"
                and not "NoteUnpinned"
                and not "TaskCreated"
                and not "TaskUpdated"
                and not "TaskCompleted"
                and not "SuccessReportUploaded"
                and not "SuccessReportUpdated"
                and not "BitrixCommentImported"
                and not "BitrixActivityImported")
            .Select(h =>
            {
                var activityDetails = CrmActivityDetails.Split(h.Details);
                return new CrmActivityItemDto(
                    "history",
                    MapHistoryTitle(h.Action),
                    activityDetails.Details,
                    h.ActorName,
                    h.CreatedAtUtc,
                    ActionComment: activityDetails.Comment);
            }));
        items.AddRange(calls.Select(call =>
        {
            var title = (call.Direction, call.Status) switch
            {
                (CrmCallDirections.Incoming, CrmCallStatuses.Missed) => "Пропущенный входящий звонок",
                (CrmCallDirections.Incoming, CrmCallStatuses.Rejected) => "Отклонённый входящий звонок",
                (CrmCallDirections.Incoming, CrmCallStatuses.Failed) => "Недоставленный входящий звонок",
                (CrmCallDirections.Incoming, _) => "Входящий звонок",
                (CrmCallDirections.Outgoing, _) => "Исходящий звонок",
                _ => "Телефонный звонок"
            };
            var actorName = string.IsNullOrWhiteSpace(call.ManagerUserId)
                ? call.Provider switch
                {
                    CrmTelephonyProviders.Plusofon => "Плюсофон",
                    CrmTelephonyProviders.Asterisk => "SIP-сервер",
                    _ => "SIPOUT"
                }
                : names.GetValueOrDefault(call.ManagerUserId, call.ManagerUserId);
            return new CrmActivityItemDto(
                "call",
                title,
                null,
                actorName,
                call.StartedAtUtc,
                CallId: call.Id,
                CallDirection: call.Direction,
                CallDurationSeconds: call.DurationSeconds,
                CallRecordingUrl: call.RecordingUrl,
                CallRecordingStored: !string.IsNullOrWhiteSpace(call.RecordingStoragePath),
                CallClientPhone: call.ClientPhoneNormalized,
                CallAiStatus: callAiStatuses.GetValueOrDefault(call.Id),
                CallStatus: call.Status);
        }));
        return items
            .OrderByDescending(x => x.IsPinned)
            .ThenByDescending(x => x.AtUtc)
            .Take(80)
            .ToList();
    }

    private static T? DeserializeOrDefault<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static IReadOnlyList<T> DeserializeOrEmpty<T>(string? json) =>
        DeserializeOrDefault<IReadOnlyList<T>>(json) ?? [];

    private static CrmTaskCommentEntity? FindCompletionComment(
        CrmTaskEntity task,
        IReadOnlyDictionary<Guid, List<CrmTaskCommentEntity>> commentsByTask)
    {
        if (task.Status != CrmTaskStatuses.Completed
            || task.CompletedAtUtc is not DateTime completedAt
            || !commentsByTask.TryGetValue(task.Id, out var comments))
        {
            return null;
        }

        return comments
            .Where(comment => Math.Abs((comment.CreatedAtUtc - completedAt).TotalSeconds) <= 5)
            .OrderBy(comment => Math.Abs((comment.CreatedAtUtc - completedAt).TotalSeconds))
            .FirstOrDefault();
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
        "TaskDeleted" => "Задача удалена",
        "NoteUpdated" => "Комментарий изменён",
        "NoteDeleted" => "Комментарий удалён",
        "NotePinned" => "Комментарий закреплён",
        "NoteUnpinned" => "Комментарий откреплён",
        "SuccessReportUploaded" => "Загружен отчёт по успешному закрытию",
        "SuccessReportUpdated" => "Отчёт по успешному закрытию изменён",
        "ChatQueued" => "Сообщение поставлено в очередь",
        "ChatSent" => "Сообщение отправлено в Avito",
        "BitrixDealImported" => "Карточка импортирована из Bitrix24",
        "ChatCancelled" => "Сообщение в чат отменено",
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

    private async Task<IReadOnlyList<CrmChatMessageDto>> LoadOutboundChatAsync(
        Guid cardId,
        string userId,
        bool isAdmin,
        CancellationToken ct)
    {
        var outbound = await db.CrmOutboundChatMessages.AsNoTracking()
            .Where(x => x.CardId == cardId && x.CancelledAtUtc == null)
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        if (outbound.Count == 0)
        {
            return [];
        }

        return outbound.Select(message =>
        {
            var canCancel = string.Equals(message.Status, CrmOutboundChatStatuses.Planned, StringComparison.Ordinal)
                && (isAdmin || string.Equals(message.AuthorUserId, userId, StringComparison.Ordinal));
            var at = message.Status == CrmOutboundChatStatuses.Sent
                ? message.SentAtUtc ?? message.CreatedAtUtc
                : message.CreatedAtUtc;
            return new CrmChatMessageDto(
                message.Text,
                FormatMessageTime(at),
                "outgoing",
                message.Id,
                message.Status,
                CrmOutboundChatStatuses.GetLabel(message.Status),
                canCancel);
        }).ToList();
    }

    private static int SuccessReportArchiveOrder(string category) => category switch
    {
        CrmSuccessDocumentCategories.Correspondence => 1,
        CrmSuccessDocumentCategories.Ticket => 2,
        CrmSuccessDocumentCategories.TicketReceipt => 3,
        CrmSuccessDocumentCategories.Contract => 4,
        CrmSuccessDocumentCategories.Relationship => 5,
        CrmSuccessDocumentCategories.CandidateDocument => 6,
        CrmSuccessDocumentCategories.Other => 7,
        _ => 99
    };

    private static string SuccessReportArchiveFolder(string category) => category switch
    {
        CrmSuccessDocumentCategories.Correspondence => "01 Переписка",
        CrmSuccessDocumentCategories.Ticket => "02 Билеты",
        CrmSuccessDocumentCategories.TicketReceipt => "03 Чеки на билеты",
        CrmSuccessDocumentCategories.Contract => "04 Контракт",
        CrmSuccessDocumentCategories.Relationship => "05 Отношение",
        CrmSuccessDocumentCategories.CandidateDocument => "06 Документы и прочие файлы/Документы кандидата",
        CrmSuccessDocumentCategories.Other => "06 Документы и прочие файлы/Прочее",
        _ => "06 Документы и прочие файлы/Без категории"
    };

    private static string EnsureUniqueArchiveEntryName(
        string folder,
        string fileName,
        ISet<string> usedEntryNames)
    {
        var candidate = $"{folder}/{fileName}";
        if (usedEntryNames.Add(candidate))
        {
            return candidate;
        }

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = $"{folder}/{stem} ({suffix}){extension}";
            if (usedEntryNames.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeArchiveSegment(string? value, string fallback, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        normalized = normalized.Replace('\\', '_').Replace('/', '_');
        var invalidChars = Path.GetInvalidFileNameChars()
            .Concat("<>:\"|?*".ToCharArray())
            .ToHashSet();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            builder.Append(char.IsControl(character) || invalidChars.Contains(character) ? '_' : character);
        }

        normalized = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(normalized)) normalized = fallback;
        if (normalized.Length <= maxLength) return normalized;

        var extension = Path.GetExtension(normalized);
        if (extension.Length is > 0 and <= 20 && extension.Length < maxLength)
        {
            var stemLength = maxLength - extension.Length;
            return normalized[..stemLength].TrimEnd() + extension;
        }
        return normalized[..maxLength].TrimEnd();
    }

    private static string BuildSuccessReportManifest(
        CrmCandidateCardEntity card,
        string? candidateName,
        IReadOnlyCollection<CrmSuccessDocumentEntity> documents)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ОТЧЁТ ПО УСПЕШНО ЗАКРЫТОМУ КАНДИДАТУ");
        builder.AppendLine();
        builder.AppendLine($"Кандидат: {candidateName?.Trim() ?? "Не указан"}");
        builder.AppendLine($"ID карточки: {card.Id:D}");
        builder.AppendLine($"Дата закрытия: {DateTimeUtcHelper.EnsureUtc(card.ClosedAtUtc ?? card.UpdatedAtUtc):dd.MM.yyyy HH:mm} UTC");
        builder.AppendLine($"Всего файлов: {documents.Count}");
        builder.AppendLine();

        var sections = new[]
        {
            (Number: 1, Title: "Переписка", Categories: new[] { CrmSuccessDocumentCategories.Correspondence }, Optional: false),
            (Number: 2, Title: "Билеты", Categories: new[] { CrmSuccessDocumentCategories.Ticket }, Optional: false),
            (Number: 3, Title: "Чеки на билеты", Categories: new[] { CrmSuccessDocumentCategories.TicketReceipt }, Optional: false),
            (Number: 4, Title: "Контракт", Categories: new[] { CrmSuccessDocumentCategories.Contract }, Optional: false),
            (Number: 5, Title: "Отношение", Categories: new[] { CrmSuccessDocumentCategories.Relationship }, Optional: true),
            (Number: 6, Title: "Документы и прочие файлы", Categories: new[] { CrmSuccessDocumentCategories.CandidateDocument, CrmSuccessDocumentCategories.Other }, Optional: true)
        };
        foreach (var section in sections)
        {
            var sectionDocuments = documents
                .Where(document => section.Categories.Contains(document.Category, StringComparer.Ordinal))
                .ToArray();
            builder.AppendLine($"{section.Number}. {section.Title}");
            if (sectionDocuments.Length > 0)
            {
                foreach (var document in sectionDocuments)
                {
                    builder.AppendLine($"   • {document.FileName}");
                }
            }
            else if (section.Number == 4 && !string.IsNullOrWhiteSpace(card.SuccessContractMissingReason))
            {
                builder.AppendLine("   Фото отсутствует.");
                builder.AppendLine($"   Причина: {card.SuccessContractMissingReason.Trim()}");
            }
            else
            {
                builder.AppendLine(section.Optional ? "   Не приложено." : "   Файлы отсутствуют.");
            }
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> SuccessReportImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic", ".heif", ".tif", ".tiff"
    };

    private static async Task<string?> ValidateSuccessReportAsync(
        IReadOnlyList<CrmSuccessDocumentUpload> uploads,
        string? contractMissingReason,
        CancellationToken ct,
        IReadOnlyCollection<CrmSuccessDocumentEntity>? existingDocuments = null)
    {
        existingDocuments ??= [];
        if (uploads.Count + existingDocuments.Count > CrmSuccessDocumentLimits.MaxFilesPerReport)
        {
            return $"В одном отчёте можно загрузить не более {CrmSuccessDocumentLimits.MaxFilesPerReport} файлов.";
        }

        long totalSize = existingDocuments.Sum(document => document.SizeBytes);
        if (totalSize > CrmSuccessDocumentLimits.MaxReportSizeBytes)
        {
            return "Общий размер отчёта превышает допустимые 200 МБ.";
        }
        foreach (var upload in uploads)
        {
            if (!CrmSuccessDocumentCategories.IsValid(upload.Category))
            {
                return "В отчёте обнаружена неизвестная категория файла.";
            }

            if (upload.Length <= 0)
            {
                return $"Файл «{NormalizeUploadFileName(upload.FileName)}» пустой.";
            }

            if (upload.Length > CrmSuccessDocumentLimits.MaxFileSizeBytes)
            {
                return $"Файл «{NormalizeUploadFileName(upload.FileName)}» превышает допустимые 20 МБ.";
            }

            totalSize += upload.Length;
            if (totalSize > CrmSuccessDocumentLimits.MaxReportSizeBytes)
            {
                return "Общий размер отчёта превышает допустимые 200 МБ.";
            }

            var extension = Path.GetExtension(upload.FileName);
            var isImage = SuccessReportImageExtensions.Contains(extension);
            var isPdf = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
            var formatAllowed = upload.Category switch
            {
                CrmSuccessDocumentCategories.Ticket => isPdf,
                CrmSuccessDocumentCategories.TicketReceipt => isImage || isPdf,
                CrmSuccessDocumentCategories.Other => true,
                _ => isImage
            };
            if (!formatAllowed)
            {
                return upload.Category switch
                {
                    CrmSuccessDocumentCategories.Ticket => "Билеты принимаются только в формате PDF.",
                    CrmSuccessDocumentCategories.TicketReceipt => "Чек должен быть изображением или PDF-файлом.",
                    _ => $"Для раздела «{CrmSuccessDocumentCategories.GetLabel(upload.Category)}» загрузите изображение."
                };
            }

            if (isPdf && !await HasPdfSignatureAsync(upload, ct))
            {
                return $"Файл «{NormalizeUploadFileName(upload.FileName)}» не является корректным PDF.";
            }
        }

        foreach (var category in CrmSuccessDocumentCategories.Required)
        {
            if (!uploads.Any(x => string.Equals(x.Category, category, StringComparison.Ordinal))
                && !existingDocuments.Any(x => string.Equals(x.Category, category, StringComparison.Ordinal)))
            {
                return $"Добавьте обязательный раздел «{CrmSuccessDocumentCategories.GetLabel(category)}».";
            }
        }

        var hasContractPhoto = uploads.Any(x =>
                                   string.Equals(x.Category, CrmSuccessDocumentCategories.Contract, StringComparison.Ordinal))
                               || existingDocuments.Any(x =>
                                   string.Equals(x.Category, CrmSuccessDocumentCategories.Contract, StringComparison.Ordinal));
        if (!hasContractPhoto && string.IsNullOrWhiteSpace(contractMissingReason))
        {
            return "Добавьте фото контракта или укажите обязательную причину, почему фото нет.";
        }

        if (contractMissingReason?.Length > 2000)
        {
            return "Причина отсутствия фото контракта не должна превышать 2000 символов.";
        }

        return null;
    }

    private static async Task<bool> HasPdfSignatureAsync(CrmSuccessDocumentUpload upload, CancellationToken ct)
    {
        await using var stream = upload.OpenReadStream();
        var signature = new byte[5];
        var read = 0;
        while (read < signature.Length)
        {
            var current = await stream.ReadAsync(signature.AsMemory(read, signature.Length - read), ct);
            if (current == 0) break;
            read += current;
        }

        return read == signature.Length
               && signature[0] == '%'
               && signature[1] == 'P'
               && signature[2] == 'D'
               && signature[3] == 'F'
               && signature[4] == '-';
    }

    private static string NormalizeUploadFileName(string? fileName)
    {
        var normalized = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return "Файл";
        return normalized.Length <= 255 ? normalized : normalized[^255..];
    }

    private static string FormatMessageTime(DateTime at)
    {
        var utc = at.Kind == DateTimeKind.Utc ? at : DateTime.SpecifyKind(at, DateTimeKind.Utc);
        return utc.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
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

public sealed record CrmSuccessDocumentUpload(
    string Category,
    string FileName,
    string ContentType,
    long Length,
    Func<Stream> OpenReadStream);
