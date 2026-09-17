using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Data;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    private static int SalesCount(CrmAnalyticsDto data, string metric) => data.Sales!.Results.Single(x => x.Key == metric).Count;

    [Fact]
    public async Task Sales_ClosureWithoutReasonIsNotInventedContact()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-1));
        SalesClose(h, card, "Лид", "", from.AddHours(1)).Details = null;
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Refusals));
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(1, data.Sales!.UnclassifiedClosures);
    }

    [Fact]
    public async Task Sales_EmployeeFiltersAndEvidenceReconcileWithOfficeTotals()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        foreach (var manager in new[] { ManagerOneId, ManagerTwoId })
        foreach (var age in new[] { -15, 0 })
        {
            var card = SalesCard(h, manager, from.AddDays(age).AddMinutes(1));
            SalesMove(h, card, "Лид", "Переговоры", from.AddHours(1), manager);
            SalesMove(h, card, "Переговоры", "Анкета", from.AddHours(2), manager);
            SalesMove(h, card, "Анкета", "Билет", from.AddHours(3), manager);
            SalesClose(h, card, "Билет", age == 0 ? "Успех" : "Контракт", from.AddHours(4), manager);
        }
        await h.Db.SaveChangesAsync();
        var office = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        var sum = new Dictionary<string, int>();
        foreach (var manager in new[] { ManagerOneId, ManagerTwoId })
        {
            var q = SalesQuery(from, manager: manager);
            var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
            Assert.Single(data.Sales!.Managers);
            foreach (var metric in data.Sales.Results)
            {
                var proof = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, metric.Key, 1)).Data!;
                Assert.Equal(metric.Count, proof.Total);
                Assert.Equal(metric.Count, proof.Rows.Count);
                sum[metric.Key] = sum.GetValueOrDefault(metric.Key) + metric.Count;
            }
        }
        foreach (var metric in office.Sales!.Results) Assert.Equal(metric.Count, sum[metric.Key]);
        Assert.Equal(office.Sales.Results[1].Count, office.Sales.Managers.Sum(x => x.Contacts));
    }

    [Fact]
    public async Task Sales_PaginationAndUnassignedReceiptsDoNotDropCards()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        for (var i = 0; i < 67; i++)
        {
            var card = SalesCard(h, ManagerOneId, from.AddMinutes(1));
            SalesMove(h, card, "Лид", "Переговоры", from.AddHours(1).AddMinutes(i));
        }
        for (var i = 0; i < 3; i++) SalesCard(h, null, from.AddHours(1));
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(70, data.Sales!.Cohort.Received);
        Assert.Equal(3, data.Sales.Cohort.Unassigned);
        foreach (var metric in new[] { CrmSalesMetrics.Contacts, "sales.cohort.contacts" })
        {
            var first = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, metric, 1)).Data!;
            var second = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, metric, 2)).Data!;
            Assert.Equal(67, first.Total); Assert.Equal(50, first.Rows.Count); Assert.Equal(17, second.Rows.Count);
            Assert.Equal(67, first.Rows.Concat(second.Rows).Select(x => x.CardId).Distinct().Count());
        }
        var unassigned = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, CrmSalesMetrics.Unassigned, 1)).Data!;
        Assert.Equal(3, unassigned.Total);
    }

    [Fact]
    public async Task Sales_ConfigurationAndSameStageEventsAreNotSalesActions()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        foreach (var details in new[] { "Лид → Лид", "Лид → ЛИД", "Лид → Переговоры (воронка обновлена)", "Нет перехода" })
        {
            var card = SalesCard(h, ManagerOneId, from.AddMinutes(1));
            var row = SalesMove(h, card, "Лид", "Лид", from.AddHours(1));
            row.Details = CrmActivityDetails.WithComment(details, "Комментарий → не переход");
        }
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(4, data.Sales!.Cohort.Received);
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Empty(data.Sales.Offices.Single().Transitions);
    }

    [Fact]
    public async Task Sales_LongCloseReasonStillHasWorkingEvidence()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-1));
        var reason = new string('я', 320);
        SalesClose(h, card, "НДЗ", reason, from.AddHours(1));
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        var group = Assert.Single(data.Sales!.CloseReasons);
        Assert.Equal(reason, group.Label);
        Assert.True(group.Key.Length < 256);
        var proof = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, group.Key, 1)).Data!;
        Assert.Equal(card.Id, Assert.Single(proof.Rows).CardId);
    }

    [Fact]
    public async Task Sales_UnknownActorIsVisibleWithoutAttributingToCurrentOwner()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-1));
        SalesClose(h, card, "Лид", "Успех", from.AddHours(1), "");
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Successes));
        Assert.Equal(1, data.Sales!.UnattributedActions);
        Assert.Equal(0, data.Sales.Managers.Single().Successes);
    }

    [Fact]
    public async Task Sales_EntryOfficeKeepsReceiptAttributionWhenFirstAssignmentWasElsewhere()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddOffice(OfficeTwoId, "Второй", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeTwoId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddHours(1), office: OfficeTwoId);
        card.EntryOfficeId = OfficeOneId;
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Received));
        Assert.Equal(1, Assert.Single(data.Sales!.Managers).NewLeads);
        var selected = await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from, manager: ManagerOneId));
        Assert.Equal(1, selected.Data!.Sales!.Cohort.Received);
    }
    private static CrmAnalyticsQuery SalesQuery(DateTime from, Guid? office = null, string? manager = null) =>
        new(from, from.AddDays(1), office ?? OfficeOneId, manager, CrmAnalyticsCohortBases.Received);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_CohortConversionUsesPreviousStepAsDenominator(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        for (var i = 0; i < 10; i++)
        {
            var card = SalesCard(h, ManagerOneId, from.AddMinutes(i + 1));
            if (i >= 8) continue;
            SalesMove(h, card, CrmStages.Lead, CrmStages.Negotiations, from.AddHours(1).AddMinutes(i));
            if (i >= 4) continue;
            SalesMove(h, card, CrmStages.Negotiations, CrmStages.Questionnaire, from.AddHours(2).AddMinutes(i));
            if (i >= 2) continue;
            SalesMove(h, card, CrmStages.Questionnaire, CrmStages.Ticket, from.AddHours(3).AddMinutes(i));
            if (i == 0) SalesClose(h, card, CrmStages.Ticket, CrmCloseReasons.Success, from.AddHours(4));
        }
        await h.Db.SaveChangesAsync();

        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        var metrics = data.Sales!.Cohort.Results;
        Assert.Equal(new[] { 8, 4, 2, 1 }, metrics.Select(x => x.Count));
        Assert.Equal(new double?[] { 80, 50, 50, 50 }, metrics.Select(x => x.Percent));
        Assert.Equal(new[] { "Новый лид → Дозвон", "Дозвон → Анкета", "Анкета → Билет", "Билет → Успех" },
            metrics.Select(x => x.Label));
    }

    private static CrmCandidateCardEntity SalesCard(Harness h, string? manager, DateTime entered,
        string entryStage = CrmStages.Lead, Guid? office = null)
    {
        var card = NewCard(office ?? OfficeOneId, manager, entryStage, entered);
        card.EntryStage = entryStage; card.EntryOfficeId = card.OfficeId;
        card.InitialAssignedOfficeId = card.OfficeId;
        AttachReviewResponse(card);
        h.Db.CrmCandidateCards.Add(card);
        return card;
    }

    private static CrmCandidateHistoryEntity SalesMove(Harness h, CrmCandidateCardEntity card,
        string from, string to, DateTime at, string manager = ManagerOneId, Guid? office = null)
    {
        var history = NewStageHistory(card.Id, CrmActivityDetails.WithComment($"{from} → {to}", "Комментарий → не этап"), at, manager, manager);
        history.OfficeId = office ?? card.OfficeId;
        history.StageAtEvent = to; history.ResponsibleUserId = manager;
        h.Db.CrmCandidateHistory.Add(history);
        return history;
    }

    private static CrmCandidateHistoryEntity SalesClose(Harness h, CrmCandidateCardEntity card,
        string stage, string reason, DateTime at, string manager = ManagerOneId)
    {
        var history = NewCloseHistory(card.Id, reason, at, manager, manager);
        history.OfficeId = card.OfficeId; history.StageAtEvent = stage; history.ResponsibleUserId = manager;
        h.Db.CrmCandidateHistory.Add(history);
        return history;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_FiftyNewLeadsThreeNewAndSeventeenOldContactsAreTwentyNotThree(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var expectedContacts = new HashSet<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var card = SalesCard(h, ManagerOneId, from.AddHours(1));
            if (i < 3) { SalesMove(h, card, CrmStages.Lead, CrmStages.Negotiations, from.AddHours(3)); expectedContacts.Add(card.Id); }
        }
        for (var i = 0; i < 17; i++)
        {
            var card = SalesCard(h, ManagerOneId, from.AddDays(-20));
            SalesMove(h, card, CrmStages.Lead, CrmStages.Ndz73, from.AddDays(-19));
            SalesMove(h, card, CrmStages.Ndz73, CrmStages.Negotiations, from.AddHours(4));
            expectedContacts.Add(card.Id);
        }
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(50, SalesCount(data, CrmSalesMetrics.Received));
        Assert.Equal(20, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(20, data.Sales!.ContactSources.Sum(x => x.Count));
        Assert.Equal(17, data.Sales.ContactSources.Single(x => x.Label == CrmStages.Ndz73).Count);
        Assert.Equal(3, data.Sales.Cohort.Results[0].Count);
        Assert.Equal(6, data.Sales.Cohort.Results[0].Percent);
        Assert.Equal(20, data.Sales.Managers.Single().Contacts);
        var evidence = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, CrmSalesMetrics.Contacts, 1)).Data!;
        Assert.Equal(20, evidence.Total);
        Assert.True(expectedContacts.SetEquals(evidence.Rows.Select(x => x.CardId)));
        var own = (await h.Sut.GetAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId, false, q)).Data!;
        Assert.Equal(20, SalesCount(own, CrmSalesMetrics.Contacts)); // no shift records
    }

    [Theory]
    [InlineData("Лид", "Переговоры", true)]
    [InlineData("НДЗ", "Переговоры", true)]
    [InlineData("НДЗ 2", "Переговоры долгосрок", true)]
    [InlineData("НДЗ с ПОДМЕННЫМ", "Переговоры", true)]
    [InlineData("НДЗ с ПОДМЕННЫМ 2", "Анкета", true)]
    [InlineData("Недоступные подменные", "Переговоры", true)]
    [InlineData("НДЗ 73", "Переговоры", true)]
    [InlineData("НДЗ 2.6", "Переговоры", true)]
    [InlineData("Лид(Важный)", "Переговоры", true)]
    [InlineData("Лид", "НДЗ", false)]
    [InlineData("НДЗ", "НДЗ 2", false)]
    [InlineData("Переговоры", "Анкета", false)]
    [InlineData("Анкета", "Билет", false)]
    [InlineData("Робот", "Переговоры", false)]
    [InlineData("Лид", "Билет", false)]
    public async Task Sales_ContactDefinitionMatchesApprovedStagePairs(string source, string target, bool expected)
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-10), source);
        SalesMove(h, card, source, target, from.AddHours(1));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(expected ? 1 : 0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(1, data.Sales!.Offices.Single().Transitions.Sum(x => x.Count));
    }

    [Theory]
    [InlineData("Лид", "Контракт", true)]
    [InlineData("НДЗ", "Исчез, слился", true)]
    [InlineData("НДЗ 2", "Левый лид", true)]
    [InlineData("Недоступные подменные", "Случайно", true)]
    [InlineData("Лид", "Новая причина офиса", true)]
    [InlineData("НДЗ", "Успех", true)]
    [InlineData("Лид", "НДЗ", false)]
    [InlineData("НДЗ 2", "НДЗ", false)]
    [InlineData("Лид", "ндз 2", false)]
    [InlineData("Переговоры", "Контракт", false)]
    [InlineData("На подписании", "Успех", false)]
    public async Task Sales_ClosuresUseSourceAndExcludeOnlyNoAnswerReasons(string source, string reason, bool expected)
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-10), source);
        SalesClose(h, card, source, reason, from.AddHours(1));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(expected ? 1 : 0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Successes) + SalesCount(data, CrmSalesMetrics.Refusals));
        Assert.Equal(1, data.Sales!.CloseReasons.Sum(x => x.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_LateSuccessBelongsToActorEvenAfterCardAndProfileMoveOffice(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddOffice(OfficeTwoId, "Второй", CrmStages.All);
        h.AddFormerManager(ManagerOneId, OfficeTwoId, "Анна", 20);
        h.AddManager(ManagerTwoId, OfficeTwoId, "Борис", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-19), office: OfficeOneId);
        var success = SalesClose(h, card, CrmStages.Signing, CrmCloseReasons.Success, from.AddHours(4));
        card.OfficeId = OfficeTwoId; card.ManagerUserId = ManagerTwoId;
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from, manager: ManagerOneId);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Received));
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Successes));
        Assert.Equal(1, data.Sales!.Managers.Single().Successes);
        Assert.All(data.Sales.Cohort.Results, x => Assert.Null(x.Percent));
        var evidence = (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), "lead", true, q, CrmSalesMetrics.Successes, 1)).Data!;
        Assert.Equal(1, evidence.Total);
        Assert.Equal(1, evidence.RestrictedCount);
        Assert.Empty(evidence.Rows);
        var visible = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, CrmSalesMetrics.Successes, 1)).Data!;
        Assert.Equal(success.CreatedAtUtc, Assert.Single(visible.Rows).AtUtc);
        Assert.Equal(0, SalesCount((await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            SalesQuery(from, OfficeTwoId, ManagerTwoId))).Data!, CrmSalesMetrics.Successes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_FirstContactIsNotCreditedAgainToAnotherManagerOrOnAnotherDay(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerTwoId, from.AddDays(-10));
        SalesMove(h, card, CrmStages.Lead, CrmStages.Negotiations, from.AddHours(-1));
        SalesMove(h, card, CrmStages.Negotiations, CrmStages.Ndz73, from.AddHours(1), ManagerTwoId);
        SalesMove(h, card, CrmStages.Ndz73, CrmStages.Negotiations, from.AddHours(2), ManagerTwoId);
        SalesMove(h, card, CrmStages.Negotiations, CrmStages.Questionnaire, from.AddHours(3), ManagerTwoId);
        SalesMove(h, card, CrmStages.Questionnaire, CrmStages.Negotiations, from.AddHours(4), ManagerTwoId);
        SalesMove(h, card, CrmStages.Negotiations, CrmStages.Questionnaire, from.AddHours(5), ManagerTwoId);
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from, manager: ManagerTwoId);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(2, SalesCount(data, CrmSalesMetrics.Questionnaires));
        Assert.Equal(5, data.Sales!.Offices.Single().Transitions.Sum(x => x.Count));
        var contacts = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, CrmSalesMetrics.Contacts, 1)).Data!;
        Assert.Equal(0, contacts.Total);
    }

    [Fact]
    public async Task Sales_CohortRetainsSubsequentOwnersProgressAndIgnoresFutureEvents()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddHours(1));
        card.ManagerUserId = ManagerTwoId;
        SalesMove(h, card, CrmStages.Lead, CrmStages.Questionnaire, from.AddHours(2), ManagerTwoId);
        SalesMove(h, card, CrmStages.Questionnaire, CrmStages.Ticket, from.AddDays(1), ManagerTwoId);
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from, manager: ManagerOneId);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Contacts)); // actor is Boris
        Assert.Equal(1, data.Sales!.Cohort.Results[0].Count); // Anna's received cohort still progresses
        Assert.Equal(1, data.Sales.Cohort.Results[1].Count);
        Assert.Equal(0, data.Sales.Cohort.Results[2].Count); // exclusive end
        var restricted = (await h.Sut.GetEvidenceAsync(OfficeScope.ForOffice(OfficeOneId), ManagerOneId, false,
            q, "sales.cohort.contacts", 1)).Data!;
        Assert.Equal(1, restricted.RestrictedCount);
        Assert.Empty(restricted.Rows);
    }

    [Fact]
    public async Task Sales_EmptyHistoryIsWarnedAboutAndCloseCountStillVisible()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-10));
        card.EntryStage = null; card.Stage = CrmStages.Negotiations;
        var close = NewCloseHistory(card.Id, CrmCloseReasons.Contract, from.AddHours(1), ManagerOneId, "Анна");
        h.Db.CrmCandidateHistory.Add(close);
        await h.Db.SaveChangesAsync();
        var q = SalesQuery(from);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Refusals));
        Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(1, data.Sales!.UnclassifiedClosures);
        var evidence = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q, CrmSalesMetrics.UnknownClosures, 1)).Data!;
        Assert.Equal(card.Id, Assert.Single(evidence.Rows).CardId);
    }

    [Fact]
    public async Task Sales_LegacyCloseReconstructsStageFromEarlierHistoryNotCurrentStage()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddDays(-10));
        card.EntryStage = null; card.Stage = CrmStages.Signing;
        SalesMove(h, card, CrmStages.Lead, CrmStages.Ndz26, from.AddDays(-2));
        h.Db.CrmCandidateHistory.Add(NewCloseHistory(card.Id, "Случайно", from.AddHours(1), ManagerOneId, "Анна"));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Contacts));
        Assert.Equal(CrmStages.Ndz26, Assert.Single(data.Sales!.ContactSources).Label);
    }

    [Fact]
    public async Task Sales_ImportantEntryAndArchivedStagesPreserveOfficeFunnelWithoutInventedMoves()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Четвёртый", ["Лид", "Лид(Важный)", "НДЗ 73", "Переговоры"]);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddHours(1), "Лид(Важный)");
        SalesMove(h, card, "Лид(Важный)", "Переговоры", from.AddHours(2));
        SalesMove(h, card, "Переговоры", "Удалённый этап", from.AddHours(3));
        await h.Db.SaveChangesAsync();
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        var stages = data.Sales!.Offices.Single().Stages;
        Assert.Equal(new[] { "Лид", "Лид(Важный)", "НДЗ 73", "Переговоры", "Удалённый этап" }, stages.Select(x => x.Stage));
        Assert.Equal(0, stages.Single(x => x.Stage == "Лид(Важный)").Count);
        Assert.True(stages.Last().Archived);
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Contacts));
    }

    [Fact]
    public async Task Sales_OfficeAliasAndTimezoneBoundaryAreRespected()
    {
        await using var h = await Harness.CreateSqliteAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", ["Новый", "Связались"]);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 20, false);
        var sut = new CrmAnalyticsQueryService(h.Db, new FixedTimeProvider(Now), Options.Create(new CrmAnalyticsOptions {
            StageMilestonesByOffice = new() { [OfficeOneId.ToString()] = new() { ["Новый"] = "Лид", ["Связались"] = "Переговоры" } }
        }));
        var from = new DateTime(2026, 8, 3, 19, 0, 0, DateTimeKind.Utc); // local 04 Aug 00:00
        foreach (var at in new[] { from.AddTicks(-1), from, from.AddDays(1) })
        {
            var card = SalesCard(h, ManagerOneId, from.AddDays(-10), "Новый");
            SalesMove(h, card, "Новый", "Связались", at);
        }
        await h.Db.SaveChangesAsync();
        var data = (await sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, SalesQuery(from))).Data!;
        Assert.Equal(1, SalesCount(data, CrmSalesMetrics.Contacts));
    }
}
