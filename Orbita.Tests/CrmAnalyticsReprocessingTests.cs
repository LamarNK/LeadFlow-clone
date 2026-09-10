using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_ReprocessingPreservesOriginalManagersClosuresAndReceipts(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddOffice(OfficeTwoId, "Повторная обработка", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 50, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = SalesCard(h, ManagerOneId, from.AddHours(1));
        card.IsClosed = true; card.CloseReason = CrmCloseReasons.NoAnswer; card.ClosedAtUtc = from.AddHours(2);
        h.Db.CrmCandidateHistory.Add(NewCloseHistory(card.Id, CrmCloseReasons.NoAnswer, card.ClosedAtUtc.Value, ManagerOneId, "Анна"));
        await h.Db.SaveChangesAsync();
        var query = SalesQuery(from, manager: ManagerOneId);
        var before = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, query)).Data!;
        var service = new CrmReprocessingService(h.Db, new CrmLeadDistributionService(h.Db, null!),
            Options.Create(new CrmReprocessingOptions { Enabled = true, DestinationOfficeId = OfficeTwoId,
                SourceOfficeNames = ["Первый"], ClosedFromUtc = new DateTimeOffset(from) }));
        Assert.Equal(1, await service.ProcessBatchAsync());
        var after = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, query)).Data!;
        Assert.Equal(1, SalesCount(after, CrmSalesMetrics.Received));
        Assert.Equal(SalesCount(before, CrmSalesMetrics.Refusals), SalesCount(after, CrmSalesMetrics.Refusals));
        Assert.Equal(1, SalesCount(after, CrmSalesMetrics.Refusals));
        Assert.Equal(0, SalesCount(after, CrmSalesMetrics.Contacts));
        var destinationQuery = query with { OfficeId = OfficeTwoId, ManagerUserId = null };
        var destination = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, destinationQuery)).Data!;
        Assert.Equal(0, SalesCount(destination, CrmSalesMetrics.Received));
        Assert.Equal(0, SalesCount(destination, CrmSalesMetrics.Refusals));
        Assert.Equal(OfficeTwoId, (await h.Db.CrmCandidateCards.SingleAsync()).OfficeId);
    }
}
