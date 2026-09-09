using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_ImportantLeadsAreReceiptsButMovingAnOldCardToImportantIsNotAnotherReceipt(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        const string important = "Лид(Важный)";
        h.AddOffice(OfficeOneId, "Четвёртый", ["Лид", important, "НДЗ", "Переговоры"]);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 100, false);
        h.AddManager(ManagerTwoId, OfficeOneId, "Борис", 100, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var regular = SalesCard(h, ManagerOneId, from.AddHours(1));
        var newImportant = SalesCard(h, ManagerOneId, from.AddHours(1), important);
        var progressedImportant = SalesCard(h, ManagerOneId, from.AddHours(1), important);
        var unassignedImportant = SalesCard(h, null, from.AddHours(1), important);
        // Receipt belongs to the first recipient, progress to the employee who did it.
        progressedImportant.ManagerUserId = ManagerTwoId;
        progressedImportant.Stage = "Переговоры";
        SalesMove(h, progressedImportant, important, "Переговоры", from.AddHours(2), ManagerTwoId);

        var oldMoved = SalesCard(h, ManagerOneId, from.AddDays(-10));
        oldMoved.Stage = important;
        SalesMove(h, oldMoved, "Лид", important, from.AddHours(3));
        SalesCard(h, ManagerOneId, from.AddDays(-10), important);
        SalesCard(h, ManagerOneId, from.AddDays(1), important); // Outside the selected dates.
        await h.Db.SaveChangesAsync();

        var q = SalesQuery(from);
        var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
        Assert.Equal(4, SalesCount(data, CrmSalesMetrics.Received));
        Assert.Equal(4, data.Sales!.Cohort.Received);
        Assert.Equal(1, data.Sales.Cohort.Unassigned);
        Assert.Equal(1, data.Sales.Cohort.Results.Single(x => x.Key == "sales.cohort.contacts").Count);
        Assert.Equal(25d, data.Sales.Cohort.Results.Single(x => x.Key == "sales.cohort.contacts").Percent);
        Assert.Equal(1, data.Sales.Offices.Single().Stages.Single(x => x.Stage == important).Count);
        var proof = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true,
            q, CrmSalesMetrics.Received, 1)).Data!;
        Assert.Equal(4, proof.Total);
        Assert.True(new HashSet<Guid> { regular.Id, newImportant.Id, progressedImportant.Id, unassignedImportant.Id }
            .SetEquals(proof.Rows.Select(x => x.CardId)));

        var firstOwner = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            SalesQuery(from, manager: ManagerOneId))).Data!;
        Assert.Equal(3, SalesCount(firstOwner, CrmSalesMetrics.Received));
        Assert.Equal(3, firstOwner.Sales!.Cohort.Received);
        Assert.Equal(1, firstOwner.Sales.Cohort.Results.Single(x => x.Key == "sales.cohort.contacts").Count);
        var actor = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true,
            SalesQuery(from, manager: ManagerTwoId))).Data!;
        Assert.Equal(0, SalesCount(actor, CrmSalesMetrics.Received));
        Assert.Equal(1, SalesCount(actor, CrmSalesMetrics.Contacts));
    }
}
