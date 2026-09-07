using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Orbita.Api.Data;
using Orbita.Api.Helpers;
using Orbita.Api.Options;
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
/// Read-only server-side CRM analytics. Receipt KPIs and the funnel use a period cohort,
/// period activity uses actual stage/close events from every card, and manager load/task
/// metrics are a current snapshot. Work events belong to their actual actor; shift records
/// annotate, but never suppress, them. Receipt belongs to the first assignment, transfers
/// to the recorded recipient. Cohort progress includes work by subsequent owners.
/// </summary>
public sealed partial class CrmAnalyticsQueryService(
    OrbitaDbContext db,
    TimeProvider timeProvider,
    IOptions<CrmAnalyticsOptions> analyticsOptions,
    IOrbitaQueryCache? queryCache = null)
{
    private const string NoCloseReason = "Без причины";
    private const int MaxPeriodDays = LocalCalendarDateRange.MaxCalendarDays;
    private const string FunnelUpdatedSuffix = " (воронка обновлена)";
    private const string ReachedNegotiationsBreakdown = "Переговоры и дальше";
    private const string SuccessfulCloseBreakdown = "Было закрытие «Успех»";
    private readonly CrmAnalyticsOptions analyticsOptions = analyticsOptions.Value;
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

    public Task<CrmAnalyticsQueryResult> GetAsync(
        OfficeScope scope,
        string requesterUserId,
        bool isAdmin,
        CrmAnalyticsQuery query,
        CancellationToken ct = default)
    {
        Task<CrmAnalyticsQueryResult> Load(CancellationToken token) =>
            GetUncachedAsync(scope, requesterUserId, isAdmin, query, token);

        return queryCache is null
            ? Load(ct)
            : queryCache.GetOrCreateAsync(
                OrbitaCacheDomain.Analytics,
                scope.ResolveFilter(query.OfficeId),
                $"{requesterUserId}:{isAdmin}:{scope.IsGlobalAdmin}:{scope.OfficeId?.ToString("D") ?? "-"}",
                new { Kind = "crm-analytics-v2", query.OfficeId, query.ManagerUserId, query.FromUtc, query.ToUtc,
                    query.CohortBasis, analyticsOptions = this.analyticsOptions },
                OrbitaCachePolicy.Analytics,
                Load,
                ct);
    }

    private async Task<CrmAnalyticsQueryResult> GetUncachedAsync(
        OfficeScope scope,
        string requesterUserId,
        bool isAdmin,
        CrmAnalyticsQuery query,
        CancellationToken ct = default)
    {
        evidence.Clear();
        evidenceProofs.Clear();
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
                x.CrmEnabled || db.CrmCandidateCards.Any(card => card.OfficeId == x.Id || card.EntryOfficeId == x.Id)
                || db.CrmCandidateHistory.Any(history => history.OfficeId == x.Id));
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

        // Older API clients retain their receipt convention. The UI always sends an explicit
        // basis, so selecting a manager cannot silently change the period's meaning.
        var cohortBasis = query.CohortBasis ?? (effectiveManagerUserId is null
            ? CrmAnalyticsCohortBases.Received : CrmAnalyticsCohortBases.FirstAssigned);
        if (cohortBasis is not (CrmAnalyticsCohortBases.Received or CrmAnalyticsCohortBases.FirstAssigned))
            return CrmAnalyticsQueryResult.BadRequest("Неизвестное основание отбора лидов.");
        var firstAssignedCohort = cohortBasis == CrmAnalyticsCohortBases.FirstAssigned;

        var cohortQuery = db.CrmCandidateCards
            .AsNoTracking()
            .Where(x => officeIds.Contains(firstAssignedCohort
                ? x.InitialAssignedOfficeId ?? x.OfficeId : x.EntryOfficeId ?? x.OfficeId));
        if (effectiveManagerUserId is not null)
        {
            cohortQuery = cohortQuery.Where(x =>
                (x.InitialManagerUserId == effectiveManagerUserId
                 || ((x.InitialManagerUserId == null || x.InitialManagerUserId == "")
                     && x.ManagerUserId == effectiveManagerUserId)));
        }
        if (firstAssignedCohort)
        {
            cohortQuery = cohortQuery.Where(x =>
                ((x.InitialManagerUserId != null && x.InitialManagerUserId != "")
                 || (x.ManagerUserId != null && x.ManagerUserId != ""))
                && ((x.InitialAssignedAtUtc.HasValue
                     && x.InitialAssignedAtUtc.Value >= fromUtc
                     && x.InitialAssignedAtUtc.Value < toUtc)
                    || (!x.InitialAssignedAtUtc.HasValue
                        && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) >= fromUtc
                        && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) < toUtc)));
        }
        else
        {
            cohortQuery = cohortQuery.Where(x =>
                (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) >= fromUtc
                && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) < toUtc);
        }

        var cohort = await cohortQuery
            .Select(x => new CardRow(
                x.Id,
                firstAssignedCohort ? x.InitialAssignedOfficeId ?? x.OfficeId : x.EntryOfficeId ?? x.OfficeId,
                x.Stage,
                x.ManagerUserId,
                x.IsClosed,
                x.CloseReason,
                x.InitialManagerUserId == null || x.InitialManagerUserId == ""
                    ? x.ManagerUserId
                    : x.InitialManagerUserId,
                x.InitialAssignedAtUtc ?? x.EnteredCrmAtUtc ?? x.CreatedAtUtc,
                x.EntryStage,
                x.EnteredCrmAtUtc ?? x.CreatedAtUtc))
            .ToListAsync(ct);

        var cards = BuildCardMetrics(cohort, effectiveManagerUserId);
        // The activity report must still surface fresh, not-yet-assigned cards even when
        // there are no actions. This uses a fixed entry-date basis, independent of the tabs.
        var periodReceiptsQuery = db.CrmCandidateCards.AsNoTracking()
            .Where(x => officeIds.Contains(x.EntryOfficeId ?? x.OfficeId)
                && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) >= fromUtc && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) < toUtc);
        if (effectiveManagerUserId is not null)
            periodReceiptsQuery = periodReceiptsQuery.Where(x => x.InitialManagerUserId == effectiveManagerUserId
                || ((x.InitialManagerUserId == null || x.InitialManagerUserId == "") && x.ManagerUserId == effectiveManagerUserId));
        var receiptSummary = new CrmAnalyticsReceiptSummaryDto(await periodReceiptsQuery.CountAsync(ct),
            await periodReceiptsQuery.CountAsync(x => x.ManagerUserId == null || x.ManagerUserId == "", ct));
        AddCardEvidence("cohort.received", cohort.Select(x => x.Id));
        AddCardEvidence("cohort.unassigned", cohort.Where(x => string.IsNullOrWhiteSpace(x.CurrentManagerUserId)).Select(x => x.Id));
        AddCardEvidence("cohort.assigned", cohort.Where(x => effectiveManagerUserId is null
            ? !string.IsNullOrWhiteSpace(x.CurrentManagerUserId) : x.CurrentManagerUserId == effectiveManagerUserId).Select(x => x.Id));
        AddCardEvidence("cohort.active", cohort.Where(x => !x.IsClosed).Select(x => x.Id));
        AddCardEvidence("cohort.closed", cohort.Where(x => x.IsClosed).Select(x => x.Id));
        AddCardEvidence("cohort.success", cohort.Where(x => x.IsClosed && x.CloseReason == CrmCloseReasons.Success).Select(x => x.Id));
        var periodActivityResult = await LoadPeriodActivityAsync(
            officeIds,
            effectiveManagerUserId,
            fromUtc,
            toUtc,
            generatedAtUtc,
            ct);
        var closeReasons = BuildCloseReasons(periodActivityResult.CloseEvents);
        var stageHistories = await LoadStageHistoriesAsync(cohort, fromUtc, toUtc, ct);
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
            periodActivityResult.Events,
            ct);
        var decomposition = BuildManagerDecomposition(
            cohort.Count,
            await LoadManagerActivitiesAsync(
                cohort.Select(x => x.Id).ToArray(),
                fromUtc,
                toUtc,
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
            callQuality,
            periodActivityResult.Activity,
            cohortBasis,
            await cohortQuery.CountAsync(x => x.EnteredCrmAtUtc == null || x.EntryOfficeId == null
                || ((firstAssignedCohort || effectiveManagerUserId != null)
                    && (x.InitialManagerUserId == null || x.InitialManagerUserId == "" || x.InitialAssignedAtUtc == null)), ct),
            receiptSummary));
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
            .Where(x => officeIds.Contains(x.InitialAssignedOfficeId ?? x.OfficeId)
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
            .Select(x => new { OfficeId = x.InitialAssignedOfficeId ?? x.OfficeId, UserId = x.InitialManagerUserId! })
            .Distinct()
            .ToListAsync(ct);
        var factualTaskManagers = await factualTaskManagersQuery
            .Select(x => new { x.OfficeId, UserId = x.AssigneeUserId })
            .Distinct()
            .ToListAsync(ct);

        var historicalPeople = await (
            from history in db.CrmCandidateHistory.AsNoTracking()
            join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
            where officeIds.Contains(history.OfficeId ?? card.OfficeId)
                && (history.Action == "StageChanged" || history.Action == "Closed"
                    || history.Action == "Reopened" || history.Action == "Assigned")
            select new { OfficeId = history.OfficeId ?? card.OfficeId, history.ActorUserId, history.ActorName, history.TargetUserId, history.PreviousUserId })
            .Distinct().ToListAsync(ct);
        var dimensionKeys = new HashSet<ManagerKey>();
        foreach (var person in historicalPeople)
        {
            if (!string.IsNullOrWhiteSpace(person.ActorUserId)) dimensionKeys.Add(new(person.OfficeId, person.ActorUserId));
            if (!string.IsNullOrWhiteSpace(person.TargetUserId)) dimensionKeys.Add(new(person.OfficeId, person.TargetUserId));
            if (!string.IsNullOrWhiteSpace(person.PreviousUserId)) dimensionKeys.Add(new(person.OfficeId, person.PreviousUserId));
        }
        var historicalNames = historicalPeople.Where(x => !string.IsNullOrWhiteSpace(x.ActorName))
            .GroupBy(x => x.ActorUserId).ToDictionary(g => g.Key, g => g.First().ActorName);
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
                        ? userNames.GetValueOrDefault(key.UserId, historicalNames.GetValueOrDefault(key.UserId, key.UserId))
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
        DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        if (cohort.Count == 0)
        {
            return [];
        }

        var cardIds = cohort.Select(x => x.Id).ToArray();
        return await db.CrmCandidateHistory
            .AsNoTracking()
            .Where(x => cardIds.Contains(x.CardId) && x.Action == "StageChanged"
                && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc)
            .Select(x => new StageHistoryRow(x.Id, x.CardId, x.Details, x.CreatedAtUtc))
            .ToListAsync(ct);
    }

    private async Task<PeriodActivityResult> LoadPeriodActivityAsync(
        IReadOnlyCollection<Guid> officeIds, string? managerUserId,
        DateTime fromUtc, DateTime toUtc, DateTime generatedAtUtc, CancellationToken ct)
    {
        if (officeIds.Count == 0) return PeriodActivityResult.Empty;
        var events = await (
            from history in db.CrmCandidateHistory.AsNoTracking()
            join card in db.CrmCandidateCards.AsNoTracking() on history.CardId equals card.Id
            where officeIds.Contains(history.OfficeId ?? card.OfficeId)
                && history.CreatedAtUtc >= fromUtc && history.CreatedAtUtc < toUtc
                && (history.Action == "StageChanged" || history.Action == "Closed"
                    || history.Action == "Reopened" || history.Action == "Assigned")
            select new PeriodHistoryRow(history.Id, history.OfficeId ?? card.OfficeId,
                history.ActorUserId, history.CardId, history.Action, history.Details, history.CreatedAtUtc,
                history.TargetUserId, history.PreviousUserId, history.PreviousCloseReason,
                history.ContextInferred || history.OfficeId == null))
            .ToListAsync(ct);
        var actorIds = events.Select(x => x.UserId).Distinct().ToArray();
        var shifts = await LoadShiftWindowsAsync(officeIds, actorIds, fromUtc, toUtc, generatedAtUtc, ct);
        var actions = events.Where(x => managerUserId is null || x.UserId == managerUserId).ToList();
        var stages = actions.Where(IsRealStageEvent).ToList();
        var closures = actions.Where(x => x.Action == "Closed").ToList();
        var reopens = actions.Where(x => x.Action == "Reopened").ToList();
        // Preserve the event ledger but surface suspicious historical repeat closures.
        // Look before the selected period too; a missing reopening must not disappear at midnight.
        var closureIds = closures.Select(x => x.Id).ToArray();
        var repeatedClosureIds = closureIds.Length == 0 ? [] : await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => closureIds.Contains(x.Id))
            .Where(x => db.CrmCandidateHistory.Where(previous => previous.CardId == x.CardId
                    && previous.CreatedAtUtc < x.CreatedAtUtc
                    && (previous.Action == "Closed" || previous.Action == "Reopened"))
                .OrderByDescending(previous => previous.CreatedAtUtc).ThenByDescending(previous => previous.Id)
                .Select(previous => previous.Action).FirstOrDefault() == "Closed")
            .Select(x => x.Id).ToArrayAsync(ct);
        var assignments = events.Where(x => x.Action == "Assigned" && !string.IsNullOrWhiteSpace(x.TargetUserId)
            && x.TargetUserId != x.PreviousUserId
            && (managerUserId is null || x.TargetUserId == managerUserId)).ToList();
        var received = assignments.Where(x => !string.IsNullOrWhiteSpace(x.PreviousUserId)).ToList();
        var sent = events.Where(x => x.Action == "Assigned" && !string.IsNullOrWhiteSpace(x.PreviousUserId)
            && !string.IsNullOrWhiteSpace(x.TargetUserId) && x.TargetUserId != x.PreviousUserId
            && (managerUserId is null || x.PreviousUserId == managerUserId)).ToList();
        AddEventEvidence("activity.transitions", stages);
        AddEventEvidence("activity.closed", closures);
        AddEventEvidence("activity.repeated-closures", closures.Where(x => repeatedClosureIds.Contains(x.Id)));
        AddEventEvidence("activity.success", closures.Where(x => IsSuccessfulClose(x.Details)));
        AddEventEvidence("activity.reopened", reopens);
        AddEventEvidence("activity.assignments", assignments);
        AddEventEvidence("activity.received", received);
        AddEventEvidence("activity.sent", sent);
        foreach (var group in closures.GroupBy(x => string.IsNullOrWhiteSpace(CrmActivityDetails.Split(x.Details).Details)
                     ? NoCloseReason : CrmActivityDetails.Split(x.Details).Details!.Trim()))
            AddEventEvidence("reason:" + group.Key, group);
        var transitions = stages.GroupBy(x => new StageTransitionKey(
                ParseSourceStage(x.Details)!, ParseDestinationStage(x.Details)!))
            .Select(group => {
                AddEventEvidence("transition:" + group.Key.FromStage + "→" + group.Key.ToStage, group);
                return new CrmAnalyticsStageTransitionDto(group.Key.FromStage, group.Key.ToStage, group.Count());
            }).OrderByDescending(x => x.Count).ThenBy(x => x.FromStage).ThenBy(x => x.ToStage).ToList();
        return new PeriodActivityResult(
            new CrmAnalyticsPeriodActivityDto(stages.Count, closures.Count,
                closures.Count(x => IsSuccessfulClose(x.Details)),
                stages.Count(x => !shifts.Contains(x.OfficeId, x.UserId, x.CreatedAtUtc)),
                closures.Count(x => !shifts.Contains(x.OfficeId, x.UserId, x.CreatedAtUtc)), transitions,
                reopens.Count, reopens.Count(x => x.PreviousCloseReason == CrmCloseReasons.Success),
                assignments.Count, received.Count, sent.Count,
                events.Count(x => x.Action == "Assigned" && string.IsNullOrWhiteSpace(x.TargetUserId)
                    && (managerUserId is null || x.UserId == managerUserId)),
                actions.Count(x => x.ContextInferred), repeatedClosureIds.Length),
            closures.Select(x => new CloseEventRow(x.Id, x.CardId, x.Details, x.CreatedAtUtc)).ToList(), events);
    }

    private static bool IsRealStageEvent(PeriodHistoryRow x) =>
        x.Action == "StageChanged"
        && x.Details?.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal) != true
        && ParseSourceStage(x.Details) is { Length: > 0 } from
        && ParseDestinationStage(x.Details) is { Length: > 0 } to
        && !string.Equals(from, to, StringComparison.Ordinal);

    private async Task<IReadOnlyList<ManagerActivityRow>> LoadManagerActivitiesAsync(
        IReadOnlyCollection<Guid> cardIds, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        // This is the progress of the received group, not the actions of only its first owner.
        if (cardIds.Count == 0) return [];
        return await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => cardIds.Contains(x.CardId) && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc
                && (x.Action == "StageChanged" || x.Action == "Closed" || x.Action == "Reopened")
                && (x.Action != "StageChanged" || x.Details == null || !x.Details.EndsWith(FunnelUpdatedSuffix)))
            .Select(x => new ManagerActivityRow(x.Id, x.OfficeId ?? Guid.Empty, x.ActorUserId,
                x.CardId, x.Action, x.Details, x.CreatedAtUtc)).ToListAsync(ct);
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

    private IReadOnlyList<CrmAnalyticsOfficeFunnelDto> BuildFunnels(
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

    private CrmAnalyticsDecompositionDto BuildManagerDecomposition(
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

        foreach (var cardActivities in activities.GroupBy(activity => activity.CardId))
        {
            var reachedStages = cardActivities
                .Where(activity => string.Equals(activity.Action, "StageChanged", StringComparison.Ordinal)
                    && ParseSourceStage(activity.Details) is { Length: > 0 } source
                    && ParseDestinationStage(activity.Details) is { Length: > 0 } destination
                    && source != destination)
                .Select(activity => ParseDestinationStage(activity.Details) is {} stage
                    ? analyticsOptions.ResolveMilestone(activity.OfficeId, stage) : null)
                .Where(stage => !string.IsNullOrWhiteSpace(stage))
                .Select(stage => stage!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var resultingClose = cardActivities
                .Where(activity => string.Equals(activity.Action, "Closed", StringComparison.Ordinal)
                                   || string.Equals(activity.Action, "Reopened", StringComparison.Ordinal))
                .OrderByDescending(activity => activity.CreatedAtUtc)
                .ThenByDescending(activity => activity.Id)
                .FirstOrDefault();
            var closeReasons = new HashSet<string>(StringComparer.Ordinal);
            if (resultingClose is not null
                && string.Equals(resultingClose.Action, "Closed", StringComparison.Ordinal))
            {
                var closeReason = CrmActivityDetails.Split(resultingClose.Details).Details;
                if (!string.IsNullOrWhiteSpace(closeReason))
                {
                    closeReasons.Add(closeReason.Trim());
                }
            }

            var isContract = closeReasons.Contains(CrmCloseReasons.Success);
            // Contact is a historical fact inferred from a qualifying action. Reopening
            // changes the closure result, but must not erase an earlier contact basis.
            var historicalCloseReasons = cardActivities.Where(x => x.Action == "Closed")
                .Select(x => CrmActivityDetails.Split(x.Details).Details?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
            var hadSuccessfulClose = historicalCloseReasons.Contains(CrmCloseReasons.Success);
            var isTicket = reachedStages.Contains(CrmStages.Ticket);
            var isQuestionnaire = reachedStages.Contains(CrmStages.Questionnaire);
            var hasStageContact = isQuestionnaire || isTicket
                                  || reachedStages.Contains(CrmStages.PreparingToSend)
                                  || reachedStages.Contains(CrmStages.InTransit)
                                  || reachedStages.Contains(CrmStages.Signing)
                                  || reachedStages.Any(stage =>
                                      stage.StartsWith(CrmStages.Negotiations, StringComparison.OrdinalIgnoreCase));
            var contactCloseReason = ContactCloseReasons
                .FirstOrDefault(reason => historicalCloseReasons.Contains(reason));
            var hasContact = hasStageContact || hadSuccessfulClose || contactCloseReason is not null;

            if (hasContact)
            {
                contacts++;
                AddCardEvidence("cohort.contacts", [cardActivities.Key]);
                AddProofEvidence("cohort.contacts", cardActivities.Where(IsContactEvidence).Select(x => x.Id));
                var breakdownLabel = hadSuccessfulClose
                    ? SuccessfulCloseBreakdown
                    : hasStageContact
                        ? ReachedNegotiationsBreakdown
                        : contactCloseReason!;
                breakdown[breakdownLabel]++;
            }

            if (isQuestionnaire)
            {
                questionnaires++;
                AddCardEvidence("cohort.questionnaires", [cardActivities.Key]);
                AddProofEvidence("cohort.questionnaires", cardActivities.Where(x => IsMilestoneEvidence(x, CrmStages.Questionnaire)).Select(x => x.Id));
            }

            if (isTicket)
            {
                tickets++;
                AddCardEvidence("cohort.tickets", [cardActivities.Key]);
                AddProofEvidence("cohort.tickets", cardActivities.Where(x => IsMilestoneEvidence(x, CrmStages.Ticket)).Select(x => x.Id));
            }

            if (isContract)
            {
                contracts++;
                AddCardEvidence("cohort.contracts", [cardActivities.Key]);
                AddProofEvidence("cohort.contracts", [resultingClose!.Id]);
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
            Percent(questionnaires, leads),
            Percent(tickets, leads),
            Percent(contracts, leads),
            orderedBreakdown);
    }

    private bool IsMilestoneEvidence(ManagerActivityRow row, string stage) =>
        row.Action == "StageChanged" && ParseSourceStage(row.Details) is { Length: > 0 } source
        && ParseDestinationStage(row.Details) is { Length: > 0 } destination && source != destination
        && string.Equals(analyticsOptions.ResolveMilestone(row.OfficeId, destination), stage, StringComparison.OrdinalIgnoreCase);

    private bool IsContactEvidence(ManagerActivityRow row)
    {
        if (row.Action == "Closed")
        {
            var reason = CrmActivityDetails.Split(row.Details).Details?.Trim();
            return reason == CrmCloseReasons.Success || ContactCloseReasons.Contains(reason!);
        }
        if (row.Action != "StageChanged" || ParseSourceStage(row.Details) is not { Length: > 0 } source
            || ParseDestinationStage(row.Details) is not { Length: > 0 } destination || source == destination) return false;
        var stage = analyticsOptions.ResolveMilestone(row.OfficeId, destination);
        return new[] { CrmStages.Questionnaire, CrmStages.Ticket, CrmStages.PreparingToSend,
                CrmStages.InTransit, CrmStages.Signing }.Contains(stage, StringComparer.OrdinalIgnoreCase)
            || stage.StartsWith(CrmStages.Negotiations, StringComparison.OrdinalIgnoreCase);
    }

    private CrmAnalyticsOfficeFunnelDto BuildFunnel(
        OfficeRow office, IReadOnlyList<CardRow> officeCards,
        IReadOnlyList<StageHistoryRow> histories, DateTime fromUtc, DateTime toUtc)
    {
        var configured = CrmStages.Resolve(office.StagesJson);
        var officeCardIds = officeCards.Select(x => x.Id).ToHashSet();
        var transitions = histories.Where(x => officeCardIds.Contains(x.CardId)
                && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc
                && x.Details?.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal) != true)
            .Select(x => new { x.CardId, From = ParseSourceStage(x.Details), To = ParseDestinationStage(x.Details) })
            .Where(x => !string.IsNullOrWhiteSpace(x.From) && !string.IsNullOrWhiteSpace(x.To) && x.From != x.To)
            .ToList();
        var historicalStages = transitions.Select(x => x.To!).Concat(officeCards.Select(x => x.Stage))
            .Where(x => !configured.Contains(x)).Distinct().Order().ToList();
        var stages = configured.Concat(historicalStages).ToList();
        var result = stages.Select((stage, position) => {
            var ids = transitions.Where(x => x.To == stage).Select(x => x.CardId).Distinct().ToArray();
            AddCardEvidence("stage:" + office.Id + ":" + stage, ids);
            AddProofEvidence("stage:" + office.Id + ":" + stage,
                histories.Where(x => officeCardIds.Contains(x.CardId) && x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc
                    && x.Details?.EndsWith(FunnelUpdatedSuffix, StringComparison.Ordinal) != true
                    && ParseSourceStage(x.Details) is { Length: > 0 } source && source != stage
                    && ParseDestinationStage(x.Details) == stage).Select(x => x.Id));
            return new CrmAnalyticsFunnelStageDto(stage, position,
                officeCards.Count(x => x.Stage == stage), ids.Length, 0, Percent(ids.Length, officeCards.Count),
                IsArchive: !configured.Contains(stage),
                CreatedCount: officeCards.Count(x => x.EntryStage == stage && x.EnteredAtUtc >= fromUtc && x.EnteredAtUtc < toUtc));
        }).ToList();
        var sources = officeCards.GroupBy(x => x.EntryStage ?? "Начальный этап не сохранён")
            .Select(g => new CrmAnalyticsEntrySourceDto(g.Key, g.Count(), Percent(g.Count(), officeCards.Count)))
            .OrderBy(x => x.Stage).ToList();
        return new CrmAnalyticsOfficeFunnelDto(office.Id, office.Name, officeCards.Count, result, sources);
    }

    private async Task<IReadOnlyList<CrmAnalyticsManagerDto>> BuildManagerMetricsAsync(
        IReadOnlyCollection<Guid> officeIds,
        IReadOnlyList<ManagerProfileRow> managers,
        DateTime fromUtc,
        DateTime toUtc,
        DateTime generatedAtUtc,
        IReadOnlyList<PeriodHistoryRow> periodEvents,
        CancellationToken ct)
    {
        if (managers.Count == 0)
        {
            return [];
        }

        var managerIds = managers.Select(x => x.UserId).Distinct(StringComparer.Ordinal).ToArray();
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
            .Where(x => officeIds.Contains(x.InitialAssignedOfficeId ?? x.OfficeId)
                        && ((x.InitialAssignedAtUtc.HasValue
                             && x.InitialAssignedAtUtc.Value >= fromUtc
                             && x.InitialAssignedAtUtc.Value < toUtc)
                             || (!x.InitialAssignedAtUtc.HasValue
                                 && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) >= fromUtc
                                 && (x.EnteredCrmAtUtc ?? x.CreatedAtUtc) < toUtc))
                        && ((x.InitialManagerUserId != null
                             && x.InitialManagerUserId != ""
                             && managerIds.Contains(x.InitialManagerUserId))
                            || ((x.InitialManagerUserId == null || x.InitialManagerUserId == "")
                                && x.ManagerUserId != null
                                && managerIds.Contains(x.ManagerUserId))))
            .Select(x => new ReceivedCardRow(
                x.InitialAssignedOfficeId ?? x.OfficeId,
                x.InitialManagerUserId == null || x.InitialManagerUserId == ""
                    ? x.ManagerUserId!
                    : x.InitialManagerUserId,
                x.InitialAssignedAtUtc ?? x.EnteredCrmAtUtc ?? x.CreatedAtUtc))
            .ToListAsync(ct);
        var receivedCardAggregates = receivedCardRows
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
        var stageActions = periodEvents.Where(IsRealStageEvent)
            .Where(x => managerIds.Contains(x.UserId))
            .Select(x => new StageActionRow(x.OfficeId, x.UserId, x.CardId, x.CreatedAtUtc)).ToList();
        var closeActions = periodEvents.Where(x => x.Action == "Closed" && managerIds.Contains(x.UserId))
            .Select(x => new CloseActionRow(x.Id, x.OfficeId, x.UserId, x.CardId, x.Action, x.Details, x.CreatedAtUtc)).ToList();

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
                    x.Count(),
                    x.Count(item => IsSuccessfulClose(item.Details))));

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
                    overdueTasks,
                    periodEvents.Count(x => x.OfficeId == manager.OfficeId && x.Action == "Assigned"
                        && x.TargetUserId == manager.UserId && !string.IsNullOrWhiteSpace(x.PreviousUserId)
                        && x.PreviousUserId != x.TargetUserId),
                    periodEvents.Count(x => x.OfficeId == manager.OfficeId && x.Action == "Assigned"
                        && x.PreviousUserId == manager.UserId && !string.IsNullOrWhiteSpace(x.TargetUserId)
                        && x.PreviousUserId != x.TargetUserId),
                    periodEvents.Count(x => x.OfficeId == manager.OfficeId && x.Action == "Reopened" && x.UserId == manager.UserId));
            })
            .OrderBy(x => x.OfficeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static CrmAnalyticsCardMetricsDto BuildCardMetrics(
        IReadOnlyCollection<CardRow> cohort,
        string? managerUserId)
    {
        var received = cohort.Count;
        var assigned = managerUserId is null
            ? cohort.Count(x => !string.IsNullOrWhiteSpace(x.CurrentManagerUserId))
            : cohort.Count(x => string.Equals(
                x.CurrentManagerUserId,
                managerUserId,
                StringComparison.Ordinal));
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
        IReadOnlyCollection<CloseEventRow> closeEvents)
    {
        var counts = closeEvents
            .GroupBy(
                x =>
                {
                    var reason = CrmActivityDetails.Split(x.Details).Details;
                    return string.IsNullOrWhiteSpace(reason) ? NoCloseReason : reason.Trim();
                },
                StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        var closedCount = closeEvents.Count;
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

    private static string? ParseSourceStage(string? details)
    {
        var activityDetails = CrmActivityDetails.Split(details).Details;
        if (string.IsNullOrWhiteSpace(activityDetails))
        {
            return null;
        }

        var arrowIndex = activityDetails.IndexOf('→');
        if (arrowIndex >= 0)
        {
            var source = activityDetails[..arrowIndex].Trim();
            return source.Length == 0 ? null : source;
        }

        var asciiArrowIndex = activityDetails.IndexOf("->", StringComparison.Ordinal);
        if (asciiArrowIndex >= 0)
        {
            var source = activityDetails[..asciiArrowIndex].Trim();
            return source.Length == 0 ? null : source;
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
        DateTime AttributedAtUtc,
        string? EntryStage,
        DateTime EnteredAtUtc);

    private sealed record StageHistoryRow(Guid Id, Guid CardId, string? Details, DateTime CreatedAtUtc);

    private sealed record CloseEventRow(
        Guid Id,
        Guid CardId,
        string? Details,
        DateTime CreatedAtUtc);

    private sealed record ManagerActivityRow(
        Guid Id,
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
        Guid Id,
        Guid OfficeId,
        string UserId,
        Guid CardId,
        string Action,
        string? Details,
        DateTime CreatedAtUtc);

    private sealed record CloseActionAggregateRow(int Cards, int SuccessfulCards);

    private sealed record PeriodHistoryRow(
        Guid Id,
        Guid OfficeId,
        string UserId,
        Guid CardId,
        string Action,
        string? Details,
        DateTime CreatedAtUtc,
        string? TargetUserId, string? PreviousUserId, string? PreviousCloseReason, bool ContextInferred);

    private sealed record StageTransitionKey(string FromStage, string ToStage);

    private sealed record PeriodActivityResult(
        CrmAnalyticsPeriodActivityDto Activity,
        IReadOnlyList<CloseEventRow> CloseEvents,
        IReadOnlyList<PeriodHistoryRow> Events)
    {
        public static PeriodActivityResult Empty { get; } = new(
            new CrmAnalyticsPeriodActivityDto(0, 0, 0, 0, 0, []),
            [], []);
    }

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
