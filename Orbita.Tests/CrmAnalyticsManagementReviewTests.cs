using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    [Theory]
    [InlineData(CrmCloseReasons.Success, false)]
    [InlineData(CrmCloseReasons.Contract, false)]
    [InlineData(CrmCloseReasons.NotRelevant, false)]
    [InlineData(CrmCloseReasons.Health, false)]
    [InlineData(CrmCloseReasons.Success, true)]
    [InlineData(CrmCloseReasons.Contract, true)]
    public async Task Review_ReopeningRetainsContactAndItsHistoricalEvidence(string reason, bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerTwoId, CrmStages.Lead, from,
            initialManagerUserId: ManagerOneId);
        AttachReviewResponse(card);
        var close = NewCloseHistory(card.Id, reason, from.AddHours(3), ManagerOneId, "Анна");
        close.OfficeId = OfficeOneId; close.ResponsibleUserId = ManagerOneId;
        var reopen = NewReopenHistory(card.Id, from.AddHours(4), ManagerTwoId, "Борис");
        reopen.OfficeId = OfficeOneId; reopen.PreviousCloseReason = reason;
        h.Db.CrmCandidateCards.Add(card);
        h.Db.CrmCandidateHistory.AddRange(close, reopen);
        await h.Db.SaveChangesAsync();
        var q = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, null, CrmAnalyticsCohortBases.Received);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(1, data.Decomposition!.Contacts);
        Assert.Equal(0, data.Decomposition.Contracts);
        Assert.Equal(0, data.Cards.SuccessfulClosed);
        Assert.Equal(1, data.PeriodActivity!.ClosedCards);
        Assert.Equal(1, data.PeriodActivity.ReopenedCards);
        Assert.Equal(1, data.Decomposition.ContactBreakdown.Sum(x => x.Count));
        var ev = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, "cohort.contacts", 1)).Data!;
        var row = Assert.Single(ev.Rows);
        var proof = Assert.Single(row.BasisEvents!);
        Assert.Equal(close.CreatedAtUtc, proof.AtUtc);
        Assert.Equal("Анна", proof.ActorName);
        Assert.Equal("Анна", proof.ResponsibleName);
        Assert.Equal("Борис", row.ResponsibleName);
        Assert.True(proof.OutsideShift);
        var restricted = (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId,
            false, q with { ManagerUserId = ManagerOneId }, "cohort.contacts", 1)).Data!;
        Assert.Equal(1, restricted.Total);
        Assert.Equal(1, restricted.RestrictedCount);
        Assert.Empty(restricted.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_ExplicitReceiptBasisDoesNotChangeWhenSelectingManager(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 10, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var old = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddDays(-1), initialAssignedAtUtc: from.AddHours(2));
        var fresh = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddHours(1), initialAssignedAtUtc: from.AddHours(3));
        var nextDayAssignment = NewCard(OfficeOneId, ManagerTwoId, CrmStages.Lead, from.AddHours(4), initialAssignedAtUtc: from.AddHours(26));
        var unassigned = NewCard(OfficeOneId, null, CrmStages.Lead, from.AddHours(5));
        foreach (var card in new[] { old, fresh, nextDayAssignment, unassigned })
        { AttachReviewResponse(card); h.Db.CrmCandidateCards.Add(card); }
        await h.Db.SaveChangesAsync();
        var q = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, null, CrmAnalyticsCohortBases.Received);
        var office = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        var anna = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q with { ManagerUserId = ManagerOneId })).Data!;
        var boris = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q with { ManagerUserId = ManagerTwoId })).Data!;
        Assert.Equal(3, office.Cards.Received);
        Assert.Equal(1, anna.Cards.Received);
        Assert.Equal(1, boris.Cards.Received);
        Assert.Equal(office.Cards.Received, anna.Cards.Received + boris.Cards.Received + 1); // one unassigned receipt
        var primary = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            q with { CohortBasis = CrmAnalyticsCohortBases.FirstAssigned })).Data!;
        Assert.Equal(2, primary.Cards.Received);
        Assert.Equal(primary.Cards.Received, primary.Managers.Sum(x => x.CardsInPeriod));
        Assert.Equal(3, primary.ReceiptSummary!.Received);
        Assert.Equal(1, primary.ReceiptSummary.WithoutResponsible);
        var unassignedEvidence = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true,
            q with { CohortBasis = CrmAnalyticsCohortBases.FirstAssigned }, "period.unassigned", 1)).Data!;
        Assert.Equal(unassigned.Id, Assert.Single(unassignedEvidence.Rows).CardId);
        var drilldown = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true,
            q with { ManagerUserId = ManagerOneId }, "manager.primary", 1)).Data!;
        Assert.Equal(2, drilldown.Total);
        Assert.Equal(from.AddHours(2), drilldown.Rows.Single(x => x.CardId == old.Id).AtUtc);
        Assert.Equal(CrmAnalyticsCohortBases.Received, anna.CohortBasis);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_StageProofsAreActualEventsNotReceiptAndDoNotInventOtherStages(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerTwoId, CrmStages.Questionnaire, from, initialManagerUserId: ManagerOneId);
        AttachReviewResponse(card);
        h.Db.CrmCandidateCards.Add(card);
        foreach (var (hours, actor) in new[] { (3, ManagerOneId), (5, ManagerTwoId) })
        {
            var ev = NewStageHistory(card.Id, $"{CrmStages.Lead} → {CrmStages.Questionnaire}", from.AddHours(hours), actor, actor);
            ev.OfficeId = OfficeOneId; ev.ResponsibleUserId = actor;
            h.Db.CrmCandidateHistory.Add(ev);
        }
        h.Db.CrmCandidateHistory.Add(NewStageHistory(card.Id, $"{CrmStages.Questionnaire} → {CrmStages.Lead}", from.AddHours(4), ManagerOneId));
        await h.Db.SaveChangesAsync();
        var q = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, null, CrmAnalyticsCohortBases.Received);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(1, data.Decomposition!.Questionnaires);
        Assert.Equal(3, data.PeriodActivity!.StageChanges);
        Assert.Equal(0, data.Decomposition.Tickets);
        foreach (var metric in new[] { "cohort.questionnaires", "stage:" + OfficeOneId + ":" + CrmStages.Questionnaire })
        {
            var evidence = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, metric, 1)).Data!;
            Assert.Equal(1, evidence.Total);
            var row = Assert.Single(evidence.Rows);
            Assert.Equal(2, row.BasisEvents!.Count);
            Assert.Equal(from.AddHours(3), row.BasisEvents[0].AtUtc);
            Assert.Equal(ManagerOneId, row.BasisEvents[0].ActorName);
            Assert.Equal(ManagerTwoId, row.BasisEvents[1].ActorName);
        }
    }

    [Fact]
    public async Task Review_LegacyReceiptIsVisibleWithWarningRatherThanSilentlyZeroed()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddHours(1));
        h.Db.CrmCandidateCards.Add(card);
        await h.Db.SaveChangesAsync();
        // Simulate a historical row; normal writes now capture these fields.
        card.EnteredCrmAtUtc = null; card.EntryOfficeId = null;
        card.InitialManagerUserId = null; card.InitialAssignedAtUtc = null;
        await h.Db.SaveChangesAsync();
        var q = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, ManagerOneId, CrmAnalyticsCohortBases.Received);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(1, data.Cards.Received);
        Assert.Equal(1, data.InferredReceiptCards);
    }

    [Fact]
    public async Task Review_BadBasisReturnsErrorNotAnEmptyReport()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        await h.Db.SaveChangesAsync();
        var result = await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            new CrmAnalyticsQuery(Now.UtcDateTime.AddDays(-1), Now.UtcDateTime, OfficeOneId, CohortBasis: "invalid"));
        Assert.Equal(CrmAnalyticsQueryOutcome.BadRequest, result.Outcome);
        Assert.Null(result.Data);
    }

    private static void AttachReviewResponse(CrmCandidateCardEntity card)
    {
        var person = new CandidatePersonEntity { Id = Guid.NewGuid(), FullName = "Тест аналитики", OfficeId = card.OfficeId };
        card.Response = new CandidateResponseEntity {
            Id = card.ResponseId, Person = person, PersonId = person.Id, FullName = person.FullName,
            SourceResponseId = Guid.NewGuid().ToString(), CardFingerprint = Guid.NewGuid().ToString()
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_RepeatClosureWarningLooksBeforePeriodWithoutRemovingEvents(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        var from = Now.UtcDateTime.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Signing, from.AddDays(-2));
        AttachReviewResponse(card);
        h.Db.CrmCandidateCards.Add(card);
        var duplicate = NewCloseHistory(card.Id, CrmCloseReasons.Success, from.AddHours(1), ManagerOneId, "Анна");
        h.Db.CrmCandidateHistory.AddRange(
            NewCloseHistory(card.Id, CrmCloseReasons.Success, from.AddHours(-1), ManagerOneId, "Анна"),
            duplicate,
            NewReopenHistory(card.Id, from.AddHours(2), ManagerOneId, "Анна"),
            NewCloseHistory(card.Id, CrmCloseReasons.Success, from.AddHours(3), ManagerOneId, "Анна"));
        await h.Db.SaveChangesAsync();
        var q = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, ManagerOneId);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(2, data.PeriodActivity!.ClosedCards);
        Assert.Equal(1, data.PeriodActivity.RepeatedClosuresWithoutReopen);
        var evidence = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, "activity.repeated-closures", 1)).Data!;
        Assert.Equal(duplicate.CreatedAtUtc, Assert.Single(evidence.Rows).AtUtc);
    }
}
