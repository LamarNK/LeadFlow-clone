using Microsoft.EntityFrameworkCore;
using Orbita.Contracts;

namespace Orbita.Api.Services;

public sealed partial class CrmAnalyticsQueryService
{
    private sealed record EvidenceSet(bool Events, HashSet<Guid> Ids);
    private readonly Dictionary<string, EvidenceSet> evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<Guid>> evidenceProofs = new(StringComparer.Ordinal);

    private void AddProofEvidence(string key, IEnumerable<Guid> ids)
    {
        if (!evidenceProofs.TryGetValue(key, out var set)) evidenceProofs[key] = set = [];
        set.UnionWith(ids);
    }

    private void AddCardEvidence(string key, IEnumerable<Guid> ids)
    {
        if (!evidence.TryGetValue(key, out var set)) evidence[key] = set = new(false, []);
        set.Ids.UnionWith(ids);
    }

    private void AddEventEvidence(string key, IEnumerable<PeriodHistoryRow> rows)
    {
        if (!evidence.TryGetValue(key, out var set)) evidence[key] = set = new(true, []);
        set.Ids.UnionWith(rows.Select(x => x.Id));
    }

    // Uses the same metric computation and scope validation as the dashboard. No separate
    // approximate query behind a clickable number; only current card access narrows the rows.
    public async Task<(CrmAnalyticsQueryOutcome Outcome, CrmAnalyticsEvidenceDto? Data)> GetEvidenceAsync(
        OfficeScope scope, string requesterUserId, bool isAdmin, CrmAnalyticsQuery query,
        string metric, int page, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(metric) || metric.Length > 256 || page < 1 || page > 100000)
            return (CrmAnalyticsQueryOutcome.BadRequest, null);
        if (metric == "manager.primary") query = query with { CohortBasis = CrmAnalyticsCohortBases.FirstAssigned };
        if (metric is "period.received" or "period.unassigned") query = query with { CohortBasis = CrmAnalyticsCohortBases.Received };
        var result = await GetAsync(scope, requesterUserId, isAdmin, query, ct);
        if (result.Outcome != CrmAnalyticsQueryOutcome.Success) return (result.Outcome, null);
        var metricKey = metric switch { "manager.primary" or "period.received" => "cohort.received",
            "period.unassigned" => "cohort.unassigned", _ => metric };
        if (!evidence.TryGetValue(metricKey, out var set))
        {
            if (metric is "cohort.contacts" or "cohort.questionnaires" or "cohort.tickets" or "cohort.contracts")
                set = new(false, []);
            else return (CrmAnalyticsQueryOutcome.NotFound, null);
        }
        const int pageSize = 50;
        var ids = set.Ids.ToArray();
        var accessible = db.CrmCandidateCards.AsNoTracking().Where(x =>
            (scope.IsGlobalAdmin || x.OfficeId == scope.OfficeId)
            && (isAdmin || x.ManagerUserId == requesterUserId));
        var rows = new List<CrmAnalyticsEvidenceRowDto>();
        int visible;
        if (set.Events)
        {
            var source = from history in db.CrmCandidateHistory.AsNoTracking()
                         join card in accessible on history.CardId equals card.Id
                         where ids.Contains(history.Id)
                         select new { History = history, Name = card.Response.FullName, CardOffice = card.OfficeId };
            visible = await source.CountAsync(ct);
            page = Math.Min(page, Math.Max(1, (visible + pageSize - 1) / pageSize));
            var items = await source.OrderByDescending(x => x.History.CreatedAtUtc).ThenBy(x => x.History.Id)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var offices = items.Select(x => x.History.OfficeId ?? x.CardOffice).Distinct().ToArray();
            var actors = items.Select(x => x.History.ActorUserId).Distinct().ToArray();
            var shifts = await LoadShiftWindowsAsync(offices, actors, query.FromUtc, query.ToUtc,
                timeProvider.GetUtcNow().UtcDateTime, ct);
            var responsibleIds = items.Select(x => x.History.ResponsibleUserId).Where(x => x != null).ToArray();
            var names = await db.PanelUserProfiles.AsNoTracking().Where(x => responsibleIds.Contains(x.UserId))
                .ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
            rows.AddRange(items.Select(x => new CrmAnalyticsEvidenceRowDto(x.History.CardId, x.Name,
                x.History.CreatedAtUtc, x.History.Action, x.History.Details, x.History.ActorName,
                x.History.ResponsibleUserId is {} responsible ? names.GetValueOrDefault(responsible, responsible) : null,
                !shifts.Contains(x.History.OfficeId ?? x.CardOffice, x.History.ActorUserId, x.History.CreatedAtUtc),
                x.History.ContextInferred || x.History.OfficeId == null)));
        }
        else
        {
            var firstAssigned = result.Data!.CohortBasis == CrmAnalyticsCohortBases.FirstAssigned;
            var source = accessible.Where(x => ids.Contains(x.Id));
            visible = await source.CountAsync(ct);
            page = Math.Min(page, Math.Max(1, (visible + pageSize - 1) / pageSize));
            var items = await source.OrderByDescending(x => firstAssigned
                    ? x.InitialAssignedAtUtc ?? x.EnteredCrmAtUtc ?? x.CreatedAtUtc
                    : x.EnteredCrmAtUtc ?? x.CreatedAtUtc).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new { x.Id, Name = x.Response.FullName, x.Stage, x.IsClosed, x.CloseReason,
                    At = firstAssigned ? x.InitialAssignedAtUtc ?? x.EnteredCrmAtUtc ?? x.CreatedAtUtc
                        : x.EnteredCrmAtUtc ?? x.CreatedAtUtc, x.ManagerUserId }).ToListAsync(ct);
            var managerIds = items.Select(x => x.ManagerUserId).ToArray();
            var names = await db.PanelUserProfiles.AsNoTracking().Where(x => managerIds.Contains(x.UserId))
                .ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
            var proofsByCard = new Dictionary<Guid, List<CrmAnalyticsEvidenceEventDto>>();
            if (evidenceProofs.TryGetValue(metric, out var proofIds) && proofIds.Count > 0)
            {
                // Query only accessible cards on this page. Hidden-card proof metadata must
                // never leak through a count's drilldown, even to a historical event owner.
                var pageIds = items.Select(x => x.Id).ToArray();
                var proofIdArray = proofIds.ToArray();
                var proofs = await db.CrmCandidateHistory.AsNoTracking()
                    .Where(x => pageIds.Contains(x.CardId) && proofIdArray.Contains(x.Id))
                    .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToListAsync(ct);
                var actorIds = proofs.Select(x => x.ActorUserId).Distinct().ToArray();
                var officeIds = proofs.Where(x => x.OfficeId != null).Select(x => x.OfficeId!.Value).Distinct().ToArray();
                var shifts = await LoadShiftWindowsAsync(officeIds, actorIds, query.FromUtc, query.ToUtc,
                    timeProvider.GetUtcNow().UtcDateTime, ct);
                var responsibleIds = proofs.Select(x => x.ResponsibleUserId).Where(x => x != null).Distinct().ToArray();
                var responsibleNames = await db.PanelUserProfiles.AsNoTracking().Where(x => responsibleIds.Contains(x.UserId))
                    .ToDictionaryAsync(x => x.UserId, x => x.FullName, ct);
                foreach (var proof in proofs)
                {
                    if (!proofsByCard.TryGetValue(proof.CardId, out var list)) proofsByCard[proof.CardId] = list = [];
                    list.Add(new(proof.CreatedAtUtc, proof.Action, proof.Details, proof.ActorName,
                        proof.ResponsibleUserId is {} owner ? responsibleNames.GetValueOrDefault(owner, owner) : null,
                        !shifts.Contains(proof.OfficeId ?? Guid.Empty, proof.ActorUserId, proof.CreatedAtUtc),
                        proof.ContextInferred || proof.OfficeId == null));
                }
            }
            rows.AddRange(items.Select(x => new CrmAnalyticsEvidenceRowDto(x.Id, x.Name, x.At, "Card",
                x.IsClosed ? $"Сейчас закрыта: {x.CloseReason}; этап: {x.Stage}" : $"Сейчас: {x.Stage}",
                "—", x.ManagerUserId is {} manager ? names.GetValueOrDefault(manager, manager) : null, false, false,
                proofsByCard.GetValueOrDefault(x.Id))));
        }
        return (CrmAnalyticsQueryOutcome.Success, new(metric, ids.Length, ids.Length - visible, page, pageSize, rows));
    }
}
