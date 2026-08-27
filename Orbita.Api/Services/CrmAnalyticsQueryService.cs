using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public enum CrmAnalyticsQueryOutcome
{
    Success,
    BadRequest,
    Forbidden,
    NotFound
}

public sealed record CrmAnalyticsQueryResult(
    CrmAnalyticsQueryOutcome Outcome,
    CrmAnalyticsDto? Data = null,
    string? Error = null)
{
    public static CrmAnalyticsQueryResult Ok(CrmAnalyticsDto data) =>
        new(CrmAnalyticsQueryOutcome.Success, data);

    public static CrmAnalyticsQueryResult BadRequest(string error) =>
        new(CrmAnalyticsQueryOutcome.BadRequest, Error: error);

    public static CrmAnalyticsQueryResult Forbidden(string error) =>
        new(CrmAnalyticsQueryOutcome.Forbidden, Error: error);

    public static CrmAnalyticsQueryResult NotFound(string error) =>
        new(CrmAnalyticsQueryOutcome.NotFound, Error: error);
}

/// <summary>
/// Read-only server-side CRM analytics. General period metrics use a card-created cohort;
/// manager attribution uses the first assignment while activity metrics use the actual actor.
/// Manager load and task metrics are a current snapshot.
/// </summary>
public sealed class CrmAnalyticsQueryService(
    OrbitaDbContext db,
    TimeProvider timeProvider)
{
    private const string NoCloseReason = "Без причины";
    private const string RemovedStagesBucket = "Удалённые этапы";
    private const int MaxPeriodDays = LocalCalendarDateRange.MaxCalendarDays;
    private const string FunnelUpdatedSuffix = " (воронка обновлена)";

    public async Task<CrmAnalyticsQueryResult> GetAsync(
        OfficeScope scope,
        string requesterUserId,
        bool isAdmin,
        CrmAnalyticsQuery query,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requesterUserId) || !scope.HasAccess)
        {
            return CrmAnalyticsQueryResult.Forbidden("Нет доступа к CRM-аналитике.");
        }

        var fromUtc = DateTimeUtcHelper.EnsureUtc(query.FromUtc);
        var toUtc = DateTimeUtcHelper.EnsureUtc(query.ToUtc);
        if (toUtc <= fromUtc)
        {
            return CrmAnalyticsQueryResult.BadRequest("Параметр toUtc должен быть позже fromUtc.");
        }

        if (toUtc - fromUtc > TimeSpan.FromDays(MaxPeriodDays))
        {
            return CrmAnalyticsQueryResult.BadRequest(
                $"Период CRM-аналитики не должен превышать {MaxPeriodDays} дней.");
        }

        var requestedManagerUserId = NormalizeUserId(query.ManagerUserId);
        Guid? effectiveOfficeId;
        string? effectiveManagerUserId;
        if (isAdmin)
        {
            if (!scope.IsGlobalAdmin && query.OfficeId is Guid requestedOfficeId
                                     && !scope.CanAccessOffice(requestedOfficeId))
            {
                return CrmAnalyticsQueryResult.Forbidden("Нет доступа к выбранному офису.");
            }

            effectiveOfficeId = scope.ResolveFilter(query.OfficeId);
            effectiveManagerUserId = requestedManagerUserId;
        }
        else
        {
            if (scope.OfficeId is not Guid managerOfficeId)
            {
                return CrmAnalyticsQueryResult.Forbidden("Менеджеру не назначен офис.");
            }

            if (query.OfficeId is Guid requestedOfficeId && requestedOfficeId != managerOfficeId)
            {
                return CrmAnalyticsQueryResult.Forbidden("Менеджер может смотреть аналитику только своего офиса.");
            }

            if (requestedManagerUserId is not null
                && !string.Equals(requestedManagerUserId, requesterUserId, StringComparison.Ordinal))
            {
                return CrmAnalyticsQueryResult.Forbidden("Менеджер может смотреть только собственные показатели.");
            }

            effectiveOfficeId = managerOfficeId;
            effectiveManagerUserId = requesterUserId;
        }

        var officesQuery = db.Offices.AsNoTracking();
        if (effectiveOfficeId is Guid officeId)
        {
            officesQuery = officesQuery.Where(x => x.Id == officeId);
        }
        else if (scope.IsGlobalAdmin)
        {
            // Keep active CRM offices and offices with historical CRM cards.
            officesQuery = officesQuery.Where(x =>
                x.CrmEnabled || db.CrmCandidateCards.Any(card => card.OfficeId == x.Id));
        }
        else
        {
            officesQuery = officesQuery.Where(_ => false);
        }

        var offices = await officesQuery
            .OrderBy(x => x.Name)
            .Select(x => new OfficeRow(x.Id, x.Name, x.CrmStagesJson))
            .ToListAsync(ct);
        if (effectiveOfficeId is Guid && offices.Count == 0)
        {
            return CrmAnalyticsQueryResult.NotFound("Офис не найден.");
        }

        var officeIds = offices.Select(x => x.Id).ToArray();
        var managerProfilesResult = await LoadManagerProfilesAsync(
            officeIds,
            requesterUserId,
            isAdmin,
            effectiveManagerUserId,
            ct);
        if (managerProfilesResult.Error is not null)
        {
            return isAdmin
                ? CrmAnalyticsQueryResult.NotFound(managerProfilesResult.Error)
                : CrmAnalyticsQueryResult.Forbidden(managerProfilesResult.Error);
        }

        var allManagerProfiles = managerProfilesResult.AllProfiles;
        var selectedManagerProfiles = managerProfilesResult.SelectedProfiles;
        if (effectiveManagerUserId is not null && selectedManagerProfiles.Count == 0)
        {
            return CrmAnalyticsQueryResult.NotFound("Менеджер не найден в выбранном офисе.");
        }

        var cohortQuery = db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId));
        if (effectiveManagerUserId is not null)
        {
            cohortQuery = cohortQuery.Where(x =>
                (x.InitialManagerUserId == effectiveManagerUserId
                 || ((x.InitialManagerUserId == null || x.InitialManagerUserId == "")
                     && x.ManagerUserId == effectiveManagerUserId))
                && ((x.InitialAssignedAtUtc.HasValue
                     && x.InitialAssignedAtUtc.Value >= fromUtc
                     && x.InitialAssignedAtUtc.Value < toUtc)
                    || (!x.InitialAssignedAtUtc.HasValue
                        && x.CreatedAtUtc >= fromUtc
                        && x.CreatedAtUtc < toUtc)));
        }
        else
        {
            cohortQuery = cohortQuery.Where(x =>
                x.CreatedAtUtc >= fromUtc
                && x.CreatedAtUtc < toUtc);
        }

        var cohort = await cohortQuery
            .Select(x => new CardRow(
                x.Id,
                x.OfficeId,
                x.Stage,
                x.ManagerUserId,
                x.IsClosed,
                x.CloseReason))
            .ToListAsync(ct);

        var generatedAtUtc = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
        var cards = BuildCardMetrics(cohort);
        var closeReasons = BuildCloseReasons(cohort, cards.Closed);
        var funnels = await BuildFunnelsAsync(offices, cohort, ct);
        var managerOptions = allManagerProfiles
            .Select(x => new CrmAnalyticsManagerOptionDto(
                x.UserId,
                x.DisplayName,
                x.OfficeId,
                x.OfficeName))
            .OrderBy(x => x.OfficeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var managers = await BuildManagerMetricsAsync(
            officeIds,
            selectedManagerProfiles,
            fromUtc,
            toUtc,
            generatedAtUtc,
            ct);

        return CrmAnalyticsQueryResult.Ok(new CrmAnalyticsDto(
            fromUtc,
            toUtc,
            effectiveOfficeId,
            effectiveManagerUserId,
            cards,
            closeReasons,
            funnels,
            managerOptions,
            managers,
            generatedAtUtc));
    }

    private async Task<ManagerProfilesResult> LoadManagerProfilesAsync(
        IReadOnlyCollection<Guid> officeIds,
        string requesterUserId,
        bool isAdmin,
        string? selectedManagerUserId,
        CancellationToken ct)
    {
        if (officeIds.Count == 0)
        {
            return new ManagerProfilesResult([], [], null);
        }

        var scopedProfiles = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => x.OfficeId != null && officeIds.Contains(x.OfficeId.Value))
            .Select(x => new
            {
                x.UserId,
                OfficeId = x.OfficeId!.Value,
                x.FullName,
                x.CrmShiftActive,
                x.CrmShiftStartedAtUtc,
                x.CrmCapacity
            })
            .ToListAsync(ct);

        if (!isAdmin && !scopedProfiles.Any(x =>
                x.UserId == requesterUserId && officeIds.Contains(x.OfficeId)))
        {
            return new ManagerProfilesResult([], [], "Профиль менеджера не найден в назначенном офисе.");
        }

        var deskRoleNames = PanelRoles.CrmDeskRoles
            .Select(role => role.ToUpperInvariant())
            .ToArray();
        var managerRoleIds = db.Roles
            .AsNoTracking()
            .Where(x => x.NormalizedName != null && deskRoleNames.Contains(x.NormalizedName))
            .Select(x => x.Id);
        var currentManagerUserIds = (await db.UserRoles
                .AsNoTracking()
                .Where(x => managerRoleIds.Contains(x.RoleId))
                .Select(x => x.UserId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var factualCardManagersQuery = db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && x.ManagerUserId != null
                        && x.ManagerUserId != "");
        var initialCardManagersQuery = db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && x.InitialManagerUserId != null
                        && x.InitialManagerUserId != "");
        var factualTaskManagersQuery = db.CrmTasks
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId) && x.AssigneeUserId != "");
        if (!isAdmin)
        {
            factualCardManagersQuery = factualCardManagersQuery.Where(x => x.ManagerUserId == requesterUserId);
            initialCardManagersQuery = initialCardManagersQuery.Where(x => x.InitialManagerUserId == requesterUserId);
            factualTaskManagersQuery = factualTaskManagersQuery.Where(x => x.AssigneeUserId == requesterUserId);
        }

        var factualCardManagers = await factualCardManagersQuery
            .Select(x => new { x.OfficeId, UserId = x.ManagerUserId! })
            .Distinct()
            .ToListAsync(ct);
        var initialCardManagers = await initialCardManagersQuery
            .Select(x => new { x.OfficeId, UserId = x.InitialManagerUserId! })
            .Distinct()
            .ToListAsync(ct);
        var factualTaskManagers = await factualTaskManagersQuery
            .Select(x => new { x.OfficeId, UserId = x.AssigneeUserId })
            .Distinct()
            .ToListAsync(ct);

        var dimensionKeys = new HashSet<ManagerKey>();
        foreach (var profile in scopedProfiles)
        {
            if (currentManagerUserIds.Contains(profile.UserId)
                && (isAdmin || string.Equals(profile.UserId, requesterUserId, StringComparison.Ordinal)))
            {
                dimensionKeys.Add(new ManagerKey(profile.OfficeId, profile.UserId));
            }
        }

        foreach (var item in factualCardManagers)
        {
            dimensionKeys.Add(new ManagerKey(item.OfficeId, item.UserId));
        }

        foreach (var item in initialCardManagers)
        {
            dimensionKeys.Add(new ManagerKey(item.OfficeId, item.UserId));
        }

        foreach (var item in factualTaskManagers)
        {
            dimensionKeys.Add(new ManagerKey(item.OfficeId, item.UserId));
        }

        if (!isAdmin)
        {
            dimensionKeys.RemoveWhere(x => !string.Equals(x.UserId, requesterUserId, StringComparison.Ordinal));
        }

        var userIds = dimensionKeys.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var profilesByUserId = await db.PanelUserProfiles
            .AsNoTracking()
            .Where(x => userIds.Contains(x.UserId))
            .Select(x => new { x.UserId, x.FullName })
            .ToDictionaryAsync(x => x.UserId, x => x.FullName, StringComparer.Ordinal, ct);
        var userNames = await db.Users
            .AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Email, x.UserName })
            .ToDictionaryAsync(
                x => x.Id,
                x => x.Email ?? x.UserName ?? x.Id,
                StringComparer.Ordinal,
                ct);
        var officeNames = await db.Offices
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var scopedProfilesByKey = scopedProfiles.ToDictionary(
            x => new ManagerKey(x.OfficeId, x.UserId));
        var rows = dimensionKeys
            .Select(key =>
            {
                scopedProfilesByKey.TryGetValue(key, out var profile);
                var fullName = profile?.FullName;
                if (string.IsNullOrWhiteSpace(fullName))
                {
                    fullName = profilesByUserId.GetValueOrDefault(key.UserId);
                }

                return new ManagerProfileRow(
                    key.UserId,
                    key.OfficeId,
                    officeNames.GetValueOrDefault(key.OfficeId, key.OfficeId.ToString()),
                    string.IsNullOrWhiteSpace(fullName)
                        ? userNames.GetValueOrDefault(key.UserId, key.UserId)
                        : fullName,
                    profile is not null
                        && CrmShiftRules.IsEffectivelyOnShift(
                            profile.CrmShiftActive,
                            profile.CrmShiftStartedAtUtc,
                            DateTime.UtcNow),
                    profile?.CrmCapacity ?? 0);
            })
            .ToList();
        var selected = selectedManagerUserId is null
            ? rows
            : rows.Where(x => string.Equals(x.UserId, selectedManagerUserId, StringComparison.Ordinal)).ToList();

        return new ManagerProfilesResult(rows, selected, null);
    }

    private async Task<IReadOnlyList<CrmAnalyticsOfficeFunnelDto>> BuildFunnelsAsync(
        IReadOnlyList<OfficeRow> offices,
        IReadOnlyList<CardRow> cohort,
        CancellationToken ct)
    {
        if (cohort.Count == 0)
        {
            return offices
                .Select(office => BuildFunnel(office, [], []))
                .ToList();
        }

        var cardIds = cohort.Select(x => x.Id).ToArray();
        var histories = await db.CrmCandidateHistory
            .AsNoTracking()
            .Where(x => cardIds.Contains(x.CardId) && x.Action == "StageChanged")
            .Select(x => new StageHistoryRow(x.CardId, x.Details))
            .ToListAsync(ct);

        return offices
            .Select(office => BuildFunnel(
                office,
                cohort.Where(x => x.OfficeId == office.Id).ToList(),
                histories))
            .ToList();
    }

    private static CrmAnalyticsOfficeFunnelDto BuildFunnel(
        OfficeRow office,
        IReadOnlyList<CardRow> officeCards,
        IReadOnlyList<StageHistoryRow> histories)
    {
        var stages = CrmStages.Resolve(office.StagesJson);
        var stageIndex = stages
            .Select((stage, index) => new { stage, index })
            .ToDictionary(x => x.stage, x => x.index, StringComparer.Ordinal);
        var maxReachedByCard = officeCards.ToDictionary(x => x.Id, _ => stages.Count == 0 ? -1 : 0);

        foreach (var card in officeCards)
        {
            if (stageIndex.TryGetValue(card.Stage, out var currentIndex))
            {
                maxReachedByCard[card.Id] = Math.Max(maxReachedByCard[card.Id], currentIndex);
            }
        }

        foreach (var history in histories)
        {
            if (!maxReachedByCard.ContainsKey(history.CardId))
            {
                continue;
            }

            var destination = ParseDestinationStage(history.Details);
            if (destination is not null && stageIndex.TryGetValue(destination, out var reachedIndex))
            {
                maxReachedByCard[history.CardId] = Math.Max(maxReachedByCard[history.CardId], reachedIndex);
            }
        }

        var currentCounts = officeCards
            .GroupBy(x => x.Stage, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var result = new List<CrmAnalyticsFunnelStageDto>(stages.Count + 1);
        var previousReached = officeCards.Count;
        for (var position = 0; position < stages.Count; position++)
        {
            var stage = stages[position];
            var reached = maxReachedByCard.Values.Count(max => max >= position);
            result.Add(new CrmAnalyticsFunnelStageDto(
                stage,
                position,
                currentCounts.GetValueOrDefault(stage),
                reached,
                position == 0 ? Percent(reached, officeCards.Count) : Percent(reached, previousReached),
                Percent(reached, officeCards.Count)));
            previousReached = reached;
        }

        // A configured stage can be removed after cards have already been closed on it.
        // Keep those cards visible instead of silently dropping their current/last stage
        // from the funnel snapshot. This is an archive bucket, not another conversion step.
        var removedStageCount = officeCards.Count(x => !stageIndex.ContainsKey(x.Stage));
        if (removedStageCount > 0)
        {
            result.Add(new CrmAnalyticsFunnelStageDto(
                RemovedStagesBucket,
                result.Count,
                removedStageCount,
                removedStageCount,
                0,
                Percent(removedStageCount, officeCards.Count),
                IsArchive: true));
        }

        return new CrmAnalyticsOfficeFunnelDto(office.Id, office.Name, officeCards.Count, result);
    }

    private async Task<IReadOnlyList<CrmAnalyticsManagerDto>> BuildManagerMetricsAsync(
        IReadOnlyCollection<Guid> officeIds,
        IReadOnlyList<ManagerProfileRow> managers,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime generatedAtUtc,
        CancellationToken ct)
    {
        if (managers.Count == 0)
        {
            return [];
        }

        var managerIds = managers.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var currentCardAggregates = await db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && x.ManagerUserId != null
                        && managerIds.Contains(x.ManagerUserId)
                        && !x.IsClosed)
            .GroupBy(x => new { x.OfficeId, ManagerUserId = x.ManagerUserId! })
            .Select(group => new CurrentCardAggregateRow(
                group.Key.OfficeId,
                group.Key.ManagerUserId,
                group.Count(),
                group.Count(x => x.IsInActiveLoad)))
            .ToListAsync(ct);
        var receivedCardAggregates = await db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && ((x.InitialAssignedAtUtc.HasValue
                             && x.InitialAssignedAtUtc.Value >= fromUtc
                             && x.InitialAssignedAtUtc.Value < toUtc)
                            || (!x.InitialAssignedAtUtc.HasValue
                                && x.CreatedAtUtc >= fromUtc
                                && x.CreatedAtUtc < toUtc))
                        && ((x.InitialManagerUserId != null
                             && x.InitialManagerUserId != ""
                             && managerIds.Contains(x.InitialManagerUserId))
                            || ((x.InitialManagerUserId == null || x.InitialManagerUserId == "")
                                && x.ManagerUserId != null
                                && managerIds.Contains(x.ManagerUserId))))
            .GroupBy(x => new
            {
                x.OfficeId,
                UserId = x.InitialManagerUserId == null || x.InitialManagerUserId == ""
                    ? x.ManagerUserId!
                    : x.InitialManagerUserId
            })
            .Select(group => new ReceivedCardAggregateRow(
                group.Key.OfficeId,
                group.Key.UserId,
                group.Count()))
            .ToListAsync(ct);
        var taskAggregates = await db.CrmTasks
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId) && managerIds.Contains(x.AssigneeUserId))
            .GroupBy(x => new { x.OfficeId, x.AssigneeUserId })
            .Select(group => new TaskAggregateRow(
                group.Key.OfficeId,
                group.Key.AssigneeUserId,
                group.Count(),
                group.Count(x => x.Status == CrmTaskStatuses.Open),
                group.Count(x => x.Status == CrmTaskStatuses.Open
                                 && x.DueAtUtc.HasValue
                                 && x.DueAtUtc.Value < generatedAtUtc)))
            .ToListAsync(ct);
        var stageActions = await (
                from history in db.CrmCandidateHistory.AsNoTracking()
                join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
                where officeIds.Contains(card.OfficeId)
                      && history.Action == "StageChanged"
                      && history.CreatedAtUtc >= fromUtc
                      && history.CreatedAtUtc < toUtc
                      && managerIds.Contains(history.ActorUserId)
                      && (history.Details == null || !history.Details.EndsWith(FunnelUpdatedSuffix))
                select new StageActionRow(card.OfficeId, history.ActorUserId, history.CardId))
            .ToListAsync(ct);
        var closeActions = await (
                from history in db.CrmCandidateHistory.AsNoTracking()
                join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
                where officeIds.Contains(card.OfficeId)
                      && history.Action == "Closed"
                      && history.CreatedAtUtc >= fromUtc
                      && history.CreatedAtUtc < toUtc
                      && managerIds.Contains(history.ActorUserId)
                select new CloseActionRow(
                    card.OfficeId,
                    history.ActorUserId,
                    history.CardId,
                    history.Details))
            .ToListAsync(ct);

        var currentCardsByManager = currentCardAggregates.ToDictionary(
            x => new ManagerKey(x.OfficeId, x.UserId));
        var receivedCardsByManager = receivedCardAggregates.ToDictionary(
            x => new ManagerKey(x.OfficeId, x.UserId));
        var tasksByManager = taskAggregates.ToDictionary(
            x => new ManagerKey(x.OfficeId, x.UserId));
        var stageActionsByManager = stageActions
            .GroupBy(x => new ManagerKey(x.OfficeId, x.UserId))
            .ToDictionary(
                x => x.Key,
                x => new StageActionAggregateRow(
                    x.Select(item => item.CardId).Distinct().Count(),
                    x.Count()));
        var closeActionsByManager = closeActions
            .GroupBy(x => new ManagerKey(x.OfficeId, x.UserId))
            .ToDictionary(
                x => x.Key,
                x => new CloseActionAggregateRow(
                    x.Select(item => item.CardId).Distinct().Count(),
                    x.Where(item => IsSuccessfulClose(item.Details))
                        .Select(item => item.CardId)
                        .Distinct()
                        .Count()));

        return managers
            .Select(manager =>
            {
                var managerKey = new ManagerKey(manager.OfficeId, manager.UserId);
                currentCardsByManager.TryGetValue(managerKey, out var currentCards);
                receivedCardsByManager.TryGetValue(managerKey, out var receivedCards);
                tasksByManager.TryGetValue(managerKey, out var tasks);
                stageActionsByManager.TryGetValue(managerKey, out var stageAction);
                closeActionsByManager.TryGetValue(managerKey, out var closeAction);
                var currentAssigned = currentCards?.CurrentAssignedCards ?? 0;
                var activeLoad = currentCards?.ActiveLoad ?? 0;
                var tasksTotal = tasks?.Total ?? 0;
                var openTasks = tasks?.Open ?? 0;
                var overdueTasks = tasks?.Overdue ?? 0;
                var cardsInPeriod = receivedCards?.Cards ?? 0;

                return new CrmAnalyticsManagerDto(
                    manager.OfficeId,
                    manager.OfficeName,
                    manager.UserId,
                    manager.DisplayName,
                    manager.IsShiftActive,
                    manager.Capacity,
                    currentAssigned,
                    activeLoad,
                    Percent(activeLoad, manager.Capacity),
                    cardsInPeriod,
                    stageAction?.Cards ?? 0,
                    stageAction?.Changes ?? 0,
                    closeAction?.Cards ?? 0,
                    closeAction?.SuccessfulCards ?? 0,
                    tasksTotal,
                    openTasks,
                    overdueTasks);
            })
            .OrderBy(x => x.OfficeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CrmAnalyticsCardMetricsDto BuildCardMetrics(IReadOnlyCollection<CardRow> cohort)
    {
        var received = cohort.Count;
        var assigned = cohort.Count(x => !string.IsNullOrWhiteSpace(x.CurrentManagerUserId));
        var active = cohort.Count(x => !x.IsClosed);
        var closed = cohort.Count(x => x.IsClosed);
        var successfulClosed = cohort.Count(x =>
            x.IsClosed && string.Equals(x.CloseReason, CrmCloseReasons.Success, StringComparison.Ordinal));

        return new CrmAnalyticsCardMetricsDto(
            received,
            assigned,
            active,
            closed,
            successfulClosed,
            Percent(assigned, received),
            Percent(closed, received),
            Percent(successfulClosed, received),
            Percent(successfulClosed, closed));
    }

    private static IReadOnlyList<CrmAnalyticsCloseReasonDto> BuildCloseReasons(
        IReadOnlyCollection<CardRow> cohort,
        int closedCount)
    {
        var counts = cohort
            .Where(x => x.IsClosed)
            .GroupBy(
                x => string.IsNullOrWhiteSpace(x.CloseReason) ? NoCloseReason : x.CloseReason.Trim(),
                StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var orderedReasons = CrmCloseReasons.All
            .Concat([NoCloseReason])
            .Concat(counts.Keys.Where(x => !CrmCloseReasons.All.Contains(x, StringComparer.Ordinal)
                                           && !string.Equals(x, NoCloseReason, StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal);

        return orderedReasons
            .Select(reason => new CrmAnalyticsCloseReasonDto(
                reason,
                counts.GetValueOrDefault(reason),
                Percent(counts.GetValueOrDefault(reason), closedCount)))
            .ToList();
    }

    private static string? ParseDestinationStage(string? details)
    {
        var activityDetails = CrmActivityDetails.Split(details).Details;
        if (string.IsNullOrWhiteSpace(activityDetails))
        {
            return null;
        }

        var arrowIndex = activityDetails.LastIndexOf('→');
        if (arrowIndex >= 0)
        {
            var destination = activityDetails[(arrowIndex + 1)..].Trim();
            if (destination.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal))
            {
                destination = destination[..^FunnelUpdatedSuffix.Length].TrimEnd();
            }

            return destination.Length == 0 ? null : destination;
        }

        var asciiArrowIndex = activityDetails.LastIndexOf("->", StringComparison.Ordinal);
        if (asciiArrowIndex >= 0)
        {
            var destination = activityDetails[(asciiArrowIndex + 2)..].Trim();
            if (destination.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal))
            {
                destination = destination[..^FunnelUpdatedSuffix.Length].TrimEnd();
            }

            return destination.Length == 0 ? null : destination;
        }

        return null;
    }

    private static bool IsSuccessfulClose(string? details) =>
        string.Equals(
            CrmActivityDetails.Split(details).Details,
            CrmCloseReasons.Success,
            StringComparison.Ordinal);

    private static string? NormalizeUserId(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? null : userId.Trim();

    private static double Percent(int numerator, int denominator) =>
        denominator <= 0 ? 0 : Math.Round(numerator * 100d / denominator, 2);

    private sealed record OfficeRow(Guid Id, string Name, string? StagesJson);

    private sealed record CardRow(
        Guid Id,
        Guid OfficeId,
        string Stage,
        string? CurrentManagerUserId,
        bool IsClosed,
        string? CloseReason);

    private sealed record StageHistoryRow(Guid CardId, string? Details);

    private sealed record ManagerKey(Guid OfficeId, string UserId);

    private sealed record CurrentCardAggregateRow(
        Guid OfficeId,
        string UserId,
        int CurrentAssignedCards,
        int ActiveLoad);

    private sealed record ReceivedCardAggregateRow(Guid OfficeId, string UserId, int Cards);

    private sealed record TaskAggregateRow(
        Guid OfficeId,
        string UserId,
        int Total,
        int Open,
        int Overdue);

    private sealed record StageActionRow(Guid OfficeId, string UserId, Guid CardId);

    private sealed record StageActionAggregateRow(int Cards, int Changes);

    private sealed record CloseActionRow(Guid OfficeId, string UserId, Guid CardId, string? Details);

    private sealed record CloseActionAggregateRow(int Cards, int SuccessfulCards);

    private sealed record ManagerProfileRow(
        string UserId,
        Guid OfficeId,
        string OfficeName,
        string DisplayName,
        bool IsShiftActive,
        int Capacity);

    private sealed record ManagerProfilesResult(
        IReadOnlyList<ManagerProfileRow> AllProfiles,
        IReadOnlyList<ManagerProfileRow> SelectedProfiles,
        string? Error);
}
