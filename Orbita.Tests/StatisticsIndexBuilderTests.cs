using System.Globalization;
using Microsoft.AspNetCore.Http;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class StatisticsIndexBuilderTests
{
    [Fact]
    public void Build_MapsLowBalanceRows()
    {
        var data = CreateData(lowBalanceCount: 2);
        var period = DashboardPeriod.Today;
        var office = new FakeOfficeContext();

        var model = BuildModel(data, period, office);

        Assert.Single(model.BalanceRows, r => r.IsLowBalance);
        Assert.Contains("9", model.Summary.TotalAdvanceText);
        Assert.Equal(1, model.Summary.WorkersOnline);
    }

    [Fact]
    public void Build_OrdersBalanceRowsByAdvanceAscending()
    {
        var data = CreateData(lowBalanceCount: 1);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        Assert.Equal(1000m, model.BalanceRows[0].Advance);
        Assert.Equal(8000m, model.BalanceRows[^1].Advance);
    }

    [Fact]
    public void Build_IncludesBitrixSentKpi()
    {
        var data = CreateData(lowBalanceCount: 0);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var sentKpi = model.KpiCards.Single(c => c.Key == "sent");
        Assert.Equal("В Битрикс24", sentKpi.Label);
        Assert.Equal(7, sentKpi.CountValue);
        Assert.Equal("70%", sentKpi.Delta);
    }

    [Fact]
    public void Build_MapsSubProfileBalancesUnderAccounts()
    {
        var data = CreateData(lowBalanceCount: 0);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var high = model.BalanceRows.Single(r => r.AccountName == "High");
        Assert.Equal(2, high.SubProfiles.Count);
        Assert.Equal("Основной", high.SubProfiles[0].Name);
        Assert.Equal($"{12000m.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} ₽", high.SubProfiles[0].AdvanceText);
        Assert.Equal("~ на 12 дней", high.SubProfiles[0].DurationText);
        Assert.Equal("Доп.", high.SubProfiles[1].Name);
    }

    private static StatisticsViewModel BuildModel(
        OfficeStatisticsDto data,
        DashboardPeriod period,
        IOfficeContext office) =>
        StatisticsIndexBuilder.Build(
            data,
            period,
            office,
            new StatisticsFiltersViewModel(),
            [],
            [],
            []);

    private static OfficeStatisticsDto CreateData(int lowBalanceCount)
    {
        var accounts = new List<AccountBalanceStatDto>
        {
            new(Guid.NewGuid(), "Low", Guid.NewGuid(), "w2", "Office", 1000m, 0m, [], true),
            new(
                Guid.NewGuid(),
                "High",
                Guid.NewGuid(),
                "w1",
                "Office",
                8000m,
                1000m,
                [
                    new SubProfileBalanceDto("Основной", 12000m, 2000m, "~ на 12 дней"),
                    new SubProfileBalanceDto("Доп.", 6500m, 1200m, "~ на 5 дней")
                ],
                false)
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