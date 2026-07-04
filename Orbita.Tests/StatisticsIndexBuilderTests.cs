using Microsoft.AspNetCore.Http;
using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class StatisticsIndexBuilderTests
{
    [Fact]
    public void Build_MapsLowBalanceKpiTone()
    {
        var data = CreateData(lowBalanceCount: 2);
        var period = DashboardPeriod.Today;
        var office = new FakeOfficeContext();

        var model = StatisticsIndexBuilder.Build(data, period, office);

        var accountsKpi = model.KpiCards.Single(c => c.Key == "accounts");
        Assert.Equal("bad", accountsKpi.DeltaTone);
        Assert.Contains("Низкий баланс: 2", accountsKpi.Delta);
        Assert.Single(model.BalanceRows, r => r.IsLowBalance);
    }

    [Fact]
    public void Build_OrdersBalanceRowsByAdvanceAscending()
    {
        var data = CreateData(lowBalanceCount: 1);
        var model = StatisticsIndexBuilder.Build(data, DashboardPeriod.Today, new FakeOfficeContext());

        Assert.Equal(1000m, model.BalanceRows[0].Advance);
        Assert.Equal(8000m, model.BalanceRows[^1].Advance);
    }

    private static OfficeStatisticsDto CreateData(int lowBalanceCount)
    {
        var accounts = new List<AccountBalanceStatDto>
        {
            new(Guid.NewGuid(), "Low", Guid.NewGuid(), "w2", "Office", 1000m, 0m, [], true),
            new(Guid.NewGuid(), "High", Guid.NewGuid(), "w1", "Office", 8000m, 1000m, [], false)
        };

        return new OfficeStatisticsDto(
            new BalanceStatisticsSection(9000m, 1000m, lowBalanceCount, accounts),
            new AccountInfrastructureSection(2, new DashboardAccountStatusCounts(2, 0, 0, 0), 3, 0),
            new WorkerInfrastructureSection(1, 1, []),
            new ResponsesPeriodSection(10, 8, 2, 7, 1, 0, 1, 6, 12),
            [new DailyResponseBucketDto(DateTime.Today, 10, 7, 1, 0, 2, 1)],
            new HrInsightsDto([], [], [], [], "н/д", "0%"),
            DateTime.UtcNow);
    }

    private sealed class FakeOfficeContext : IOfficeContext
    {
        public bool IsAdmin => false;
        public bool ShowAllOffices => false;
        public bool ShowOfficeColumn => false;
        public Guid? EffectiveOfficeId => null;
        public string? ContextLabel => null;
        public void Bind(HttpContext context) { }
    }
}