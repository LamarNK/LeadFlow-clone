using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
/// Read-only server-side CRM analytics. General period metrics use a card-created cohort.
/// Decomposition receipt attribution requires the first assignment to fall inside the manager's
/// recorded shift; its activity metrics use the actual actor and action time, and aggregate mode
/// is the sum of the same per-manager rules. Manager load and task metrics are a current snapshot.
/// </summary>
public sealed class CrmAnalyticsQueryService(
    OrbitaDbContext db,
    TimeProvider timeProvider)
{
    private const string NoCloseReason = "Без причины";
    private const string RemovedStagesBucket = "Удалённые этапы";
    private const int MaxPeriodDays = LocalCalendarDateRange.MaxCalendarDays;
    private const string FunnelUpdatedSuffix = " (воронка обновлена)";
    private const string ReachedNegotiationsBreakdown = "Переговоры и дальше";
    private const string SuccessfulCloseBreakdown = "Успешно закрыто";
    private static readonly IReadOnlyList<string> ContactCloseReasons =
    [
        CrmCloseReasons.Woman,
        CrmCloseReasons.Contract,
        CrmCloseReasons.SelectedOthers,
        CrmCloseReasons.Age,
        CrmCloseReasons.Health,
        CrmCloseReasons.AlreadyAtSvo,
        CrmCloseReasons.NotRelevant,
        CrmCloseReasons.Officer
    ];

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

        var generatedAtUtc = DateTimeUtcHelper.EnsureUtc(timeProvider.GetUtcNow().UtcDateTime);
        var activityManagerIds = selectedManagerProfiles
            .Select(x => x.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activityShifts = await LoadShiftWindowsAsync(
            officeIds,
            activityManagerIds,
            fromUtc,
            toUtc,
            generatedAtUtc,
            ct);

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
                x.CloseReason,
                x.InitialManagerUserId == null || x.InitialManagerUserId == ""
                    ? x.ManagerUserId
                    : x.InitialManagerUserId,
                x.InitialAssignedAtUtc ?? x.CreatedAtUtc))
            .ToListAsync(ct);

        if (effectiveManagerUserId is not null)
        {
            cohort = cohort
                .Where(card => card.AttributedManagerUserId is not null
                               && activityShifts.Contains(
                                   card.OfficeId,
                                   card.AttributedManagerUserId,
                                   card.AttributedAtUtc))
                .ToList();
        }

        var cards = BuildCardMetrics(cohort);
        var closeReasons = BuildCloseReasons(cohort, cards.Closed);
        var stageHistories = await LoadStageHistoriesAsync(cohort, ct);
        var funnels = BuildFunnels(offices, cohort, stageHistories, fromUtc, toUtc);
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
        var decompositionLeads = effectiveManagerUserId is null
            ? managers.Sum(x => x.CardsInPeriod)
            : cohort.Count;
        var decomposition = BuildManagerDecomposition(
            decompositionLeads,
            await LoadManagerActivitiesAsync(
                officeIds,
                activityManagerIds,
                fromUtc,
                toUtc,
                activityShifts,
                ct));
        var callQuality = await BuildCallQualityAsync(
            officeIds,
            selectedManagerProfiles,
            effectiveManagerUserId,
            fromUtc,
            toUtc,
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
            decomposition,
            generatedAtUtc,
            callQuality));
    }

    private async Task<CrmCallQualityAnalyticsDto> BuildCallQualityAsync(
        IReadOnlyCollection<Guid> officeIds,
        IReadOnlyList<ManagerProfileRow> managers,
        string? managerUserId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var callsQuery = db.CrmCalls.AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && x.RecordingStoragePath != null
                        && x.CardId != null
                        && x.DurationSeconds > 0
                        && x.StartedAtUtc >= fromUtc
                        && x.StartedAtUtc < toUtc);
        if (managerUserId is not null)
        {
            callsQuery = callsQuery.Where(x => x.ManagerUserId == managerUserId);
        }

        var calls = await callsQuery
            .Select(x => new CallQualityCallRow(x.Id, x.ManagerUserId))
            .ToListAsync(ct);
        if (calls.Count == 0)
        {
            return new CrmCallQualityAnalyticsDto(0, 0, 0, 0, null, [], [], []);
        }

        var callIds = calls.Select(x => x.Id).ToArray();
        var insightRows = await db.CrmCallAiInsights.AsNoTracking()
            .Where(x => callIds.Contains(x.CallId))
            .Select(x => new CallQualityInsightRow(
                x.CallId,
                x.TranscriptText,
                x.AnalysisJson,
                x.Score))
            .ToListAsync(ct);
        var insights = insightRows.ToDictionary(x => x.CallId);
        var analyses = new Dictionary<Guid, CrmCallAiAnalysisDto>();
        foreach (var row in insightRows.Where(x => !string.IsNullOrWhiteSpace(x.AnalysisJson)))
        {
            var parsed = CerioAiClient.TryParseAnalysis(row.AnalysisJson!);
            if (parsed is not null) analyses[row.CallId] = parsed;
        }

        var transcribed = insightRows.Count(x => !string.IsNullOrWhiteSpace(x.TranscriptText));
        var analyzed = analyses.Count;
        var managerNames = managers
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First().DisplayName, StringComparer.Ordinal);
        var managerRows = calls
            .GroupBy(x => x.ManagerUserId ?? string.Empty, StringComparer.Ordinal)
            .Select(group =>
            {
                var groupCalls = group.ToList();
                var groupTranscribed = groupCalls.Count(call =>
                    insights.TryGetValue(call.Id, out var insight)
                    && !string.IsNullOrWhiteSpace(insight.TranscriptText));
                var groupAnalyses = groupCalls
                    .Where(call => analyses.ContainsKey(call.Id))
                    .Select(call => analyses[call.Id])
                    .ToList();
                var normalizedUserId = string.IsNullOrWhiteSpace(group.Key) ? null : group.Key;
                return new CrmCallQualityManagerDto(
                    normalizedUserId,
                    normalizedUserId is null
                        ? "Менеджер не определён"
                        : managerNames.GetValueOrDefault(normalizedUserId, normalizedUserId),
                    groupCalls.Count,
                    groupTranscribed,
                    groupAnalyses.Count,
                    Percent(groupAnalyses.Count, groupCalls.Count),
                    AverageScore(groupAnalyses));
            })
            .OrderByDescending(x => x.AnalyzedCalls)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var analysisValues = analyses.Values.ToList();
        return new CrmCallQualityAnalyticsDto(
            calls.Count,
            transcribed,
            analyzed,
            Percent(analyzed, calls.Count),
            AverageScore(analysisValues),
            BuildCommonFindings(analysisValues, strength: true),
            BuildCommonFindings(analysisValues, strength: false),
            managerRows);
    }

    private static double? AverageScore(IReadOnlyCollection<CrmCallAiAnalysisDto> analyses) =>
        analyses.Count == 0
            ? null
            : Math.Round(analyses.Average(x => x.Score), 2);

    private static IReadOnlyList<CrmCallQualityFindingDto> BuildCommonFindings(
        IReadOnlyCollection<CrmCallAiAnalysisDto> analyses,
        bool strength)
    {
        if (analyses.Count == 0) return [];
        return analyses
            .SelectMany(analysis => (strength ? analysis.Strengths : analysis.Weaknesses)
                .Where(point => !string.IsNullOrWhiteSpace(point.Title))
                .GroupBy(point => FindingKey(point), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()))
            .GroupBy(FindingKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CrmCallQualityFindingDto(
                group.Key,
                group.Select(x => x.Title).First(),
                group.Count(),
                Percent(group.Count(), analyses.Count)))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static string FindingKey(CrmCallAiAnalysisPointDto point) =>
        string.IsNullOrWhiteSpace(point.Code)
            ? point.Title.Trim().ToLowerInvariant()
            : point.Code.Trim().ToLowerInvariant();

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

    private async Task<IReadOnlyList<StageHistoryRow>> LoadStageHistoriesAsync(
        IReadOnlyList<CardRow> cohort,
        CancellationToken ct)
    {
        if (cohort.Count == 0)
        {
            return [];
        }

        var cardIds = cohort.Select(x => x.Id).ToArray();
        return await db.CrmCandidateHistory
            .AsNoTracking()
            .Where(x => cardIds.Contains(x.CardId) && x.Action == "StageChanged")
            .Select(x => new StageHistoryRow(x.CardId, x.Details, x.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<ManagerActivityRow>> LoadManagerActivitiesAsync(
        IReadOnlyCollection<Guid> officeIds,
        IReadOnlyCollection<string> managerUserIds,
        DateTime fromUtc,
        DateTime toUtc,
        ShiftWindowIndex shifts,
        CancellationToken ct)
    {
        if (officeIds.Count == 0 || managerUserIds.Count == 0)
        {
            return [];
        }

        var activities = await (
                from history in db.CrmCandidateHistory.AsNoTracking()
                join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
                where officeIds.Contains(card.OfficeId)
                      && managerUserIds.Contains(history.ActorUserId)
                      && history.CreatedAtUtc >= fromUtc
                      && history.CreatedAtUtc < toUtc
                      && (history.Action == "StageChanged" || history.Action == "Closed")
                      && (history.Action != "StageChanged"
                          || history.Details == null
                          || !history.Details.EndsWith(FunnelUpdatedSuffix))
                select new ManagerActivityRow(
                    card.OfficeId,
                    history.ActorUserId,
                    history.CardId,
                    history.Action,
                    history.Details,
                    history.CreatedAtUtc))
            .ToListAsync(ct);

        return activities
            .Where(activity => shifts.Contains(
                activity.OfficeId,
                activity.UserId,
                activity.CreatedAtUtc))
            .ToList();
    }

    private async Task<ShiftWindowIndex> LoadShiftWindowsAsync(
        IReadOnlyCollection<Guid> officeIds,
        IReadOnlyCollection<string> managerUserIds,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime generatedAtUtc,
        CancellationToken ct)
    {
        if (officeIds.Count == 0 || managerUserIds.Count == 0)
        {
            return ShiftWindowIndex.Empty;
        }

        var rows = await db.CrmManagerShifts
            .AsNoTracking()
            .Where(shift => officeIds.Contains(shift.OfficeId)
                            && managerUserIds.Contains(shift.ManagerUserId)
                            && shift.StartedAtUtc < toUtc
                            && (!shift.EndedAtUtc.HasValue || shift.EndedAtUtc.Value > fromUtc))
            .Select(shift => new ShiftRow(
                shift.OfficeId,
                shift.ManagerUserId,
                shift.StartedAtUtc,
                shift.EndedAtUtc))
            .ToListAsync(ct);

        return ShiftWindowIndex.Create(rows, generatedAtUtc);
    }

    private static IReadOnlyList<CrmAnalyticsOfficeFunnelDto> BuildFunnels(
        IReadOnlyList<OfficeRow> offices,
        IReadOnlyList<CardRow> cohort,
        IReadOnlyList<StageHistoryRow> histories,
        DateTime fromUtc,
        DateTime toUtc) =>
        offices
            .Select(office => BuildFunnel(
                office,
                cohort.Where(x => x.OfficeId == office.Id).ToList(),
                histories,
                fromUtc,
                toUtc))
            .ToList();

    private static CrmAnalyticsDecompositionDto BuildManagerDecomposition(
        int leads,
        IReadOnlyList<ManagerActivityRow> activities)
    {
        var contacts = 0;
        var questionnaires = 0;
        var tickets = 0;
        var contracts = 0;
        var breakdown = ContactCloseReasons
            .Prepend(SuccessfulCloseBreakdown)
            .Prepend(ReachedNegotiationsBreakdown)
            .ToDictionary(label => label, _ => 0, StringComparer.Ordinal);

        foreach (var cardActivities in activities.GroupBy(activity =>
                     new ManagerCardKey(activity.UserId, activity.CardId)))
        {
            var reachedStages = cardActivities
                .Where(activity => string.Equals(activity.Action, "StageChanged", StringComparison.Ordinal))
                .Select(activity => ParseDestinationStage(activity.Details))
                .Where(stage => !string.IsNullOrWhiteSpace(stage))
                .Select(stage => stage!)
                .ToHashSet(StringComparer.Ordinal);
            var closeReasons = cardActivities
                .Where(activity => string.Equals(activity.Action, "Closed", StringComparison.Ordinal))
                .Select(activity => CrmActivityDetails.Split(activity.Details).Details)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Select(reason => reason!.Trim())
                .ToHashSet(StringComparer.Ordinal);

            var isContract = closeReasons.Contains(CrmCloseReasons.Success)
                             || reachedStages.Contains(CrmStages.DealSuccessful);
            var isTicket = isContract
                           || reachedStages.Contains(CrmStages.Ticket)
                           || reachedStages.Contains(CrmStages.PreparingToSend)
                           || reachedStages.Contains(CrmStages.InTransit)
                           || reachedStages.Contains(CrmStages.Signing);
            var isQuestionnaire = isTicket
                                  || reachedStages.Contains(CrmStages.Questionnaire);
            var hasStageContact = isQuestionnaire
                                  || reachedStages.Any(stage =>
                                      stage.StartsWith(CrmStages.Negotiations, StringComparison.OrdinalIgnoreCase));
            var contactCloseReason = ContactCloseReasons
                .FirstOrDefault(reason => closeReasons.Contains(reason));
            var hasContact = hasStageContact || isContract || contactCloseReason is not null;

            if (hasContact)
            {
                contacts++;
                var breakdownLabel = isContract
                    ? SuccessfulCloseBreakdown
                    : hasStageContact
                        ? ReachedNegotiationsBreakdown
                        : contactCloseReason!;
                breakdown[breakdownLabel]++;
            }

            if (isQuestionnaire)
            {
                questionnaires++;
            }

            if (isTicket)
            {
                tickets++;
            }

            if (isContract)
            {
                contracts++;
            }
        }

        var orderedBreakdown = breakdown
            .Select(item => new CrmAnalyticsContactBreakdownDto(
                string.Equals(item.Key, CrmCloseReasons.Contract, StringComparison.Ordinal)
                    ? "Отказ: контракт"
                    : item.Key,
                item.Value))
            .ToList();

        return new CrmAnalyticsDecompositionDto(
            leads,
            contacts,
            questionnaires,
            tickets,
            contracts,
            Percent(contacts, leads),
            Percent(questionnaires, contacts),
            Percent(tickets, questionnaires),
            Percent(contracts, tickets),
            orderedBreakdown);
    }

    private static CrmAnalyticsOfficeFunnelDto BuildFunnel(
        OfficeRow office,
        IReadOnlyList<CardRow> officeCards,
        IReadOnlyList<StageHistoryRow> histories,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var stages = CrmStages.Resolve(office.StagesJson);
        var stageIndex = stages
            .Select((stage, index) => new { stage, index })
            .ToDictionary(x => x.stage, x => x.index, StringComparer.Ordinal);
        var maxReachedByCard = officeCards.ToDictionary(x => x.Id, _ => stages.Count == 0 ? -1 : 0);
        var historyCardIds = histories
            .Select(x => x.CardId)
            .ToHashSet();

        // A card without stage history was created directly on its current stage.
        // Once history exists, only transitions made inside the selected period affect
        // "Reached"; the current stage may have been reached on a later date.
        foreach (var card in officeCards)
        {
            if (!historyCardIds.Contains(card.Id)
                && stageIndex.TryGetValue(card.Stage, out var currentIndex))
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

            if (history.CreatedAtUtc < fromUtc
                || history.CreatedAtUtc >= toUtc
                || history.Details?.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal) == true)
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
        var shiftWindows = await LoadShiftWindowsAsync(
            officeIds,
            managerIds,
            fromUtc,
            toUtc,
            generatedAtUtc,
            ct);
        var currentCardStageAggregates = await db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(x.OfficeId)
                        && x.ManagerUserId != null
                        && managerIds.Contains(x.ManagerUserId)
                        && !x.IsClosed)
            .GroupBy(x => new
            {
                x.OfficeId,
                ManagerUserId = x.ManagerUserId!,
                x.Stage,
                x.IsInActiveLoad
            })
            .Select(group => new CurrentCardStageAggregateRow(
                group.Key.OfficeId,
                group.Key.ManagerUserId,
                group.Key.Stage,
                group.Key.IsInActiveLoad,
                group.Count()))
            .ToListAsync(ct);
        var officeNames = managers
            .GroupBy(x => x.OfficeId)
            .ToDictionary(group => group.Key, group => group.First().OfficeName);
        var currentCardAggregates = currentCardStageAggregates
            .GroupBy(x => new ManagerKey(x.OfficeId, x.UserId))
            .Select(group => new CurrentCardAggregateRow(
                group.Key.OfficeId,
                group.Key.UserId,
                group.Sum(x => x.Count),
                group.Where(x => CrmManagerLoadRules.CountsTowardsLoad(
                        x.Stage,
                        x.IsInActiveLoad,
                        isClosed: false,
                        officeNames.GetValueOrDefault(x.OfficeId)))
                    .Sum(x => x.Count)))
            .ToList();
        var receivedCardRows = await db.CrmCandidateCards
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
            .Select(x => new ReceivedCardRow(
                x.OfficeId,
                x.InitialManagerUserId == null || x.InitialManagerUserId == ""
                    ? x.ManagerUserId!
                    : x.InitialManagerUserId,
                x.InitialAssignedAtUtc ?? x.CreatedAtUtc))
            .ToListAsync(ct);
        var receivedCardAggregates = receivedCardRows
            .Where(row => shiftWindows.Contains(row.OfficeId, row.UserId, row.AssignedAtUtc))
            .GroupBy(row => new ManagerKey(row.OfficeId, row.UserId))
            .Select(group => new ReceivedCardAggregateRow(
                group.Key.OfficeId,
                group.Key.UserId,
                group.Count()))
            .ToList();
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
                select new StageActionRow(
                    card.OfficeId,
                    history.ActorUserId,
                    history.CardId,
                    history.CreatedAtUtc))
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
                    history.Details,
                    history.CreatedAtUtc))
            .ToListAsync(ct);

        stageActions = stageActions
            .Where(action => shiftWindows.Contains(
                action.OfficeId,
                action.UserId,
                action.CreatedAtUtc))
            .ToList();
        closeActions = closeActions
            .Where(action => shiftWindows.Contains(
                action.OfficeId,
                action.UserId,
                action.CreatedAtUtc))
            .ToList();

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
        string? CloseReason,
        string? AttributedManagerUserId,
        DateTime AttributedAtUtc);

    private sealed record StageHistoryRow(Guid CardId, string? Details, DateTime CreatedAtUtc);

    private sealed record ManagerActivityRow(
        Guid OfficeId,
        string UserId,
        Guid CardId,
        string Action,
        string? Details,
        DateTime CreatedAtUtc);

    private sealed record CallQualityCallRow(Guid Id, string? ManagerUserId);

    private sealed record CallQualityInsightRow(
        Guid CallId,
        string? TranscriptText,
        string? AnalysisJson,
        double? Score);

    private sealed record ManagerCardKey(string UserId, Guid CardId);

    private sealed record ShiftRow(
        Guid OfficeId,
        string UserId,
        DateTime StartedAtUtc,
        DateTime? EndedAtUtc);

    private sealed record ManagerKey(Guid OfficeId, string UserId);

    private sealed record CurrentCardAggregateRow(
        Guid OfficeId,
        string UserId,
        int CurrentAssignedCards,
        int ActiveLoad);

    private sealed record CurrentCardStageAggregateRow(
        Guid OfficeId,
        string UserId,
        string Stage,
        bool IsInActiveLoad,
        int Count);

    private sealed record ReceivedCardAggregateRow(Guid OfficeId, string UserId, int Cards);

    private sealed record ReceivedCardRow(
        Guid OfficeId,
        string UserId,
        DateTime AssignedAtUtc);

    private sealed record TaskAggregateRow(
        Guid OfficeId,
        string UserId,
        int Total,
        int Open,
        int Overdue);

    private sealed record StageActionRow(
        Guid OfficeId,
        string UserId,
        Guid CardId,
        DateTime CreatedAtUtc);

    private sealed record StageActionAggregateRow(int Cards, int Changes);

    private sealed record CloseActionRow(
        Guid OfficeId,
        string UserId,
        Guid CardId,
        string? Details,
        DateTime CreatedAtUtc);

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

    private sealed class ShiftWindowIndex(
        IReadOnlyDictionary<ManagerKey, IReadOnlyList<ShiftWindow>> windows)
    {
        public static ShiftWindowIndex Empty { get; } =
            new(new Dictionary<ManagerKey, IReadOnlyList<ShiftWindow>>());

        public static ShiftWindowIndex Create(
            IReadOnlyList<ShiftRow> rows,
            DateTime generatedAtUtc)
        {
            var now = DateTimeUtcHelper.EnsureUtc(generatedAtUtc);
            var normalized = rows
                .Select(row =>
                {
                    var started = DateTimeUtcHelper.EnsureUtc(row.StartedAtUtc);
                    var recordedEnd = row.EndedAtUtc is DateTime ended
                        ? DateTimeUtcHelper.EnsureUtc(ended)
                        : now;
                    var localStarted = started.Add(CrmShiftRules.BusinessUtcOffset);
                    var cutoffLocal = DateTime.SpecifyKind(
                        localStarted.Date.Add(CrmShiftRules.DailyCutoffLocalTime),
                        DateTimeKind.Unspecified);
                    var cutoffUtc = DateTime.SpecifyKind(
                        cutoffLocal - CrmShiftRules.BusinessUtcOffset,
                        DateTimeKind.Utc);
                    var effectiveEnd = recordedEnd <= cutoffUtc ? recordedEnd : cutoffUtc;
                    return new
                    {
                        Key = new ManagerKey(row.OfficeId, row.UserId),
                        Window = new ShiftWindow(started, effectiveEnd)
                    };
                })
                .Where(item => item.Window.EndedAtUtc > item.Window.StartedAtUtc)
                .GroupBy(item => item.Key)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ShiftWindow>)group
                        .Select(item => item.Window)
                        .OrderBy(window => window.StartedAtUtc)
                        .ToList());

            return normalized.Count == 0 ? Empty : new ShiftWindowIndex(normalized);
        }

        public bool Contains(Guid officeId, string userId, DateTime atUtc)
        {
            var at = DateTimeUtcHelper.EnsureUtc(atUtc);
            return windows.TryGetValue(new ManagerKey(officeId, userId), out var managerWindows)
                   && managerWindows.Any(window =>
                       at >= window.StartedAtUtc && at < window.EndedAtUtc);
        }
    }

    private sealed record ShiftWindow(DateTime StartedAtUtc, DateTime EndedAtUtc);
}
