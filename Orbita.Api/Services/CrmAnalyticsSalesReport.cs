using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed partial class CrmAnalyticsQueryService
{
    /// <summary>
    /// Load the history of relevant cards, including events before the period and other
    /// owners/offices. Filtering before first-contact detection would double-count returns.
    /// Only period events in the authorized office scope contribute to the work report.
    /// </summary>
    private async Task<CrmSalesAnalyticsDto> BuildSalesReportAsync(
        IReadOnlyList<OfficeRow> offices, IReadOnlyList<CardRow> cohort,
        IReadOnlyList<CrmAnalyticsManagerDto> managers, PeriodActivityResult activity,
        string? managerUserId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var relevantIds = activity.Events.Select(x => x.CardId).Concat(cohort.Select(x => x.Id)).Distinct().ToArray();
        var cardContexts = await db.CrmCandidateCards.AsNoTracking().Where(x => relevantIds.Contains(x.Id))
            .Select(x => new SalesCardContext(x.Id, x.OfficeId, x.EntryOfficeId ?? x.OfficeId, x.EntryStage))
            .ToDictionaryAsync(x => x.Id, ct);
        var history = await db.CrmCandidateHistory.AsNoTracking()
            .Where(x => relevantIds.Contains(x.CardId) && x.CreatedAtUtc < toUtc
                && (x.Action == "StageChanged" || x.Action == "Closed" || x.Action == "Reopened"))
            .Select(x => new SalesHistory(x.Id, x.CardId, x.OfficeId, x.ActorUserId,
                x.Action, x.Details, x.CreatedAtUtc, x.StageAtEvent, x.ContextInferred))
            .ToListAsync(ct);
        var rules = new CrmSalesRules(analyticsOptions);
        var firstContacts = new List<SalesContact>();
        var unknownClosures = new List<PeriodHistoryRow>();
        foreach (var group in history.GroupBy(x => x.CardId))
        {
            var context = cardContexts[group.Key];
            var previousStage = context.EntryStage;
            var contacted = previousStage is not null && rules.IsContactDestination(context.EntryOfficeId, previousStage);
            foreach (var item in group.OrderBy(x => x.AtUtc).ThenBy(x => x.Id))
            {
                var office = item.OfficeId ?? context.OfficeId;
                var transition = item.Action == "StageChanged" ? CrmSalesRules.Transition(item.Details) : null;
                var source = transition?.From ?? item.StageAtEvent ?? previousStage;
                var periodRow = item.ToPeriodRow(office);
                var hasCloseReason = !string.IsNullOrWhiteSpace(CrmSalesRules.Reason(item.Details));
                if (item.Action == "Closed" && (string.IsNullOrWhiteSpace(source) || !hasCloseReason)) unknownClosures.Add(periodRow);
                if (!contacted && rules.IsContactSource(office, source)
                    && ((transition is { } move && rules.IsContactDestination(office, move.To))
                        || (item.Action == "Closed" && hasCloseReason && !CrmSalesRules.IsNoAnswer(item.Details))))
                {
                    firstContacts.Add(new(periodRow, source!, transition?.To ?? CloseLabel(item.Details)));
                    contacted = true;
                }
                // A transition out of negotiations is evidence of an earlier contact, not
                // a new contact now. Historical returns cannot credit a second employee.
                if (source is not null && rules.IsContactDestination(office, source)) contacted = true;
                if (transition is { } stageMove) previousStage = stageMove.To;
                else if (!string.IsNullOrWhiteSpace(item.StageAtEvent)) previousStage = item.StageAtEvent;
            }
        }

        var officeIds = offices.Select(x => x.Id).ToHashSet();
        bool InOfficePeriod(PeriodHistoryRow row) => officeIds.Contains(row.OfficeId)
            && row.CreatedAtUtc >= fromUtc && row.CreatedAtUtc < toUtc;
        bool SelectedActor(PeriodHistoryRow row) => managerUserId is null || row.UserId == managerUserId;
        var officeContacts = firstContacts.Where(x => InOfficePeriod(x.Event)).ToList();
        var contacts = officeContacts.Where(x => SelectedActor(x.Event)).ToList();
        var events = activity.Events.Where(SelectedActor).ToList();
        var moves = events.Where(IsRealStageEvent).ToList();
        var questionnaires = moves.Where(x => rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Questionnaire)).ToList();
        var tickets = moves.Where(x => rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Ticket)).ToList();
        var closures = events.Where(x => x.Action == "Closed").ToList();
        var successes = closures.Where(x => CrmSalesRules.IsSuccess(x.Details)).ToList();
        var refusals = closures.Where(x => !CrmSalesRules.IsSuccess(x.Details)).ToList();
        var unknown = unknownClosures.Where(x => InOfficePeriod(x) && SelectedActor(x)).ToList();
        var unattributed = events.Where(x => string.IsNullOrWhiteSpace(x.UserId)
            && (IsRealStageEvent(x) || x.Action is "Closed" or "Reopened")).ToList();

        AddCardEvidence(CrmSalesMetrics.Received, cohort.Select(x => x.Id));
        AddCardEvidence(CrmSalesMetrics.Unassigned, cohort.Where(x => string.IsNullOrWhiteSpace(x.CurrentManagerUserId)).Select(x => x.Id));
        AddEventEvidence(CrmSalesMetrics.Contacts, contacts.Select(x => x.Event));
        AddEventEvidence(CrmSalesMetrics.Questionnaires, questionnaires);
        AddEventEvidence(CrmSalesMetrics.Tickets, tickets);
        AddEventEvidence(CrmSalesMetrics.Successes, successes);
        AddEventEvidence(CrmSalesMetrics.Refusals, refusals);
        AddEventEvidence(CrmSalesMetrics.UnknownClosures, unknown);
        AddEventEvidence(CrmSalesMetrics.UnattributedActions, unattributed);
        var results = new List<CrmSalesMetricDto>
        {
            new(CrmSalesMetrics.Received, "Новые лиды", cohort.Count, "карточек", "Поступили в CRM за выбранные даты"),
            new(CrmSalesMetrics.Contacts, "Дозвоны", contacts.Count, "первых контактов", "Из лида и всех НДЗ · включая старые карточки"),
            new(CrmSalesMetrics.Questionnaires, "Анкеты", questionnaires.Count, "переходов", "Переведено в анкету за период"),
            new(CrmSalesMetrics.Tickets, "Билеты", tickets.Count, "переходов", "Переведено в билет за период"),
            new(CrmSalesMetrics.Successes, "Успехи", successes.Count, "закрытий", "Закрыто с причиной «Успех» за период"),
            new(CrmSalesMetrics.Refusals, "Без успеха", refusals.Count, "закрытий", "Все остальные причины, включая НДЗ")
        };
        var sourceBreakdown = contacts.GroupBy(x => x.Source).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
            .Select(g => {
                var key = SalesDetailKey("source", g.Key);
                AddEventEvidence(key, g.Select(x => x.Event));
                return new CrmSalesBreakdownDto(key, g.Key, g.Count());
            }).ToList();
        var closeBreakdown = closures.GroupBy(x => CrmSalesRules.Reason(x.Details)).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
            .Select(g => {
                var key = SalesDetailKey("reason", g.Key);
                AddEventEvidence(key, g);
                return new CrmSalesBreakdownDto(key, CloseLabel(g.Key), g.Count());
            }).ToList();

        var officeReports = offices.Select(office => {
            var officeMoves = moves.Where(x => x.OfficeId == office.Id).ToList();
            var configured = CrmStages.Resolve(office.StagesJson);
            var historical = officeMoves.SelectMany(x => new[] { ParseSourceStage(x.Details)!, ParseDestinationStage(x.Details)! })
                .Except(configured).Distinct().OrderBy(x => x);
            var stages = configured.Concat(historical).Select(stage => {
                var key = SalesDetailKey("stage", office.Id.ToString(), stage);
                var reached = officeMoves.Where(x => ParseDestinationStage(x.Details) == stage).ToList();
                AddEventEvidence(key, reached);
                return new CrmSalesStageDto(key, stage, reached.Count, !configured.Contains(stage));
            }).ToList();
            var transitions = officeMoves.GroupBy(x => new StageTransitionKey(ParseSourceStage(x.Details)!, ParseDestinationStage(x.Details)!))
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key.FromStage).ThenBy(x => x.Key.ToStage)
                .Select(g => {
                    var key = SalesDetailKey("transition", office.Id.ToString(), g.Key.FromStage, g.Key.ToStage);
                    AddEventEvidence(key, g);
                    return new CrmSalesTransitionDto(key, g.Key.FromStage, g.Key.ToStage, g.Count());
                }).ToList();
            return new CrmSalesOfficeDto(office.Id, office.Name, stages, transitions);
        }).ToList();

        var managerRows = managers.Select(manager => {
            bool Own(PeriodHistoryRow row) => row.OfficeId == manager.OfficeId && row.UserId == manager.UserId;
            var ownMoves = activity.Events.Where(x => Own(x) && IsRealStageEvent(x)).ToList();
            return new CrmSalesManagerDto(manager.OfficeId, manager.OfficeName, manager.UserId, manager.DisplayName,
                cohort.Count(x => x.OfficeId == manager.OfficeId && x.AttributedManagerUserId == manager.UserId),
                officeContacts.Count(x => Own(x.Event)),
                ownMoves.Count(x => rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Questionnaire)),
                ownMoves.Count(x => rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Ticket)),
                activity.Events.Count(x => Own(x) && x.Action == "Closed" && CrmSalesRules.IsSuccess(x.Details)),
                activity.Events.Count(x => Own(x) && x.Action == "Closed" && !CrmSalesRules.IsSuccess(x.Details)),
                ownMoves.Count, manager.CardsInPeriod, manager.TransfersReceived, manager.TransfersSent);
        }).ToList();

        // Cohort results include subsequent owners' work, but only between receipt and the
        // selected period's end. Every numerator is a subset of exactly this receipt base.
        var cohortById = cohort.ToDictionary(x => x.Id);
        bool InCohort(PeriodHistoryRow row) => cohortById.TryGetValue(row.CardId, out var card)
            && row.CreatedAtUtc >= fromUtc && row.CreatedAtUtc >= card.EnteredAtUtc && row.CreatedAtUtc < toUtc;
        var cohortEvents = history.Select(x => x.ToPeriodRow(x.OfficeId ?? cardContexts[x.CardId].OfficeId)).Where(InCohort).ToList();
        var cohortContacts = firstContacts.Where(x => InCohort(x.Event)).Select(x => x.Event).ToList();
        var cohortResults = new List<CrmSalesCohortMetricDto>();
        void CohortMetric(string key, string label, IEnumerable<PeriodHistoryRow> rows)
        {
            var selected = rows.ToList();
            var ids = selected.Select(x => x.CardId).Distinct().ToArray();
            AddCardEvidence(key, ids);
            AddProofEvidence(key, selected.Select(x => x.Id));
            cohortResults.Add(new(key, label, ids.Length, cohort.Count == 0 ? null : Percent(ids.Length, cohort.Count)));
        }
        CohortMetric("sales.cohort.contacts", "Установили контакт", cohortContacts);
        CohortMetric("sales.cohort.questionnaires", "Дошли до анкеты", cohortEvents.Where(x => IsRealStageEvent(x)
            && rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Questionnaire)));
        CohortMetric("sales.cohort.tickets", "Дошли до билета", cohortEvents.Where(x => IsRealStageEvent(x)
            && rules.IsMilestone(x.OfficeId, ParseDestinationStage(x.Details)!, CrmStages.Ticket)));
        CohortMetric("sales.cohort.successes", "Достигли успеха", cohortEvents.Where(x => x.Action == "Closed" && CrmSalesRules.IsSuccess(x.Details)));
        foreach (var (oldKey, newKey) in new[] {
            ("cohort.contacts", "sales.cohort.contacts"), ("cohort.questionnaires", "sales.cohort.questionnaires"),
            ("cohort.tickets", "sales.cohort.tickets"), ("cohort.contracts", "sales.cohort.successes") })
        {
            evidence[oldKey] = evidence[newKey];
            evidenceProofs[oldKey] = evidenceProofs[newKey];
        }
        return new(results, sourceBreakdown, closeBreakdown, officeReports, managerRows,
            new(cohort.Count, cohort.Count(x => string.IsNullOrWhiteSpace(x.CurrentManagerUserId)), cohortResults),
            unknown.Count, events.Count(x => x.ContextInferred), unattributed.Count);
    }

    // Stage names and free-form close reasons must never exceed the drilldown key limit
    // or collide when names themselves contain arrows or separators.
    private static string SalesDetailKey(string kind, params string[] parts) =>
        $"sales.{kind}:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));

    private static string CloseLabel(string? details) => CrmSalesRules.Reason(details) switch
    {
        "" => "Без причины", CrmCloseReasons.Contract => "Отказ: контракт",
        CrmCloseReasons.Success => "Успех", var reason => reason
    };

    private sealed record SalesCardContext(Guid Id, Guid OfficeId, Guid EntryOfficeId, string? EntryStage);
    private sealed record SalesContact(PeriodHistoryRow Event, string Source, string Destination);
    private sealed record SalesHistory(Guid Id, Guid CardId, Guid? OfficeId, string UserId, string Action,
        string? Details, DateTime AtUtc, string? StageAtEvent, bool Inferred)
    {
        public PeriodHistoryRow ToPeriodRow(Guid office) => new(Id, office, UserId, CardId, Action, Details,
            AtUtc, null, null, null, Inferred || OfficeId is null);
    }
}
