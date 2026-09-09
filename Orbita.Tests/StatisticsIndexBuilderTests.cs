using System.Globalization;
using Microsoft.AspNetCore.Http;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class StatisticsIndexBuilderTests
{
    [Fact]
    public void Build_MapsAllBalanceRowsWithSubProfiles()
    {
        var data = CreateData(lowBalanceCount: 2);
        var period = DashboardPeriod.Today;
        var office = new FakeOfficeContext();

        var model = BuildModel(data, period, office);

        Assert.Equal(2, model.BalanceRows.Count);
        Assert.Single(model.BalanceRows, r => r.IsLowBalance);
        var high = model.BalanceRows.Single(r => r.AccountName == "High");
        Assert.Equal(2, high.SubProfiles.Count);
        Assert.Contains("9", model.Summary.TotalAdvanceText);
        Assert.Equal(1, model.Summary.WorkersOnline);
    }

    [Fact]
    public void Build_MapsSubProfileBalancesUnderAccounts()
    {
        var data = CreateData(lowBalanceCount: 0);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var high = model.BalanceRows.Single(r => r.AccountName == "High");
        Assert.Equal("Основной", high.SubProfiles[0].Name);
        Assert.Equal($"{12000m.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} ₽", high.SubProfiles[0].AdvanceText);
        Assert.Equal("~ на 12 дней", high.SubProfiles[0].DurationText);
        Assert.Equal("Доп.", high.SubProfiles[1].Name);
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
    public void Build_MapsCrmDeliveryStats()
    {
        var officeId = Guid.NewGuid();
        var data = CreateData(lowBalanceCount: 0) with
        {
            CrmDeliveries = [new CrmDeliveryStatDto(officeId, "Офис Екатеринбург", 4)]
        };

        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var delivery = Assert.Single(model.CrmDeliveries);
        Assert.Equal(officeId, delivery.OfficeId);
        Assert.Equal("Офис Екатеринбург", delivery.Label);
        Assert.Equal(4, delivery.SentCount);
        Assert.Contains("status=sent", delivery.ResponsesUrl);
    }

    [Fact]
    public void Build_DeliveryLinks_OmitEmptyFilterIds()
    {
        var data = CreateData(lowBalanceCount: 0) with
        {
            BitrixDeliveries = [new BitrixDeliveryStatDto(Guid.NewGuid(), "Отдел подбора", 3)]
        };

        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var delivery = Assert.Single(model.BitrixDeliveries);
        Assert.DoesNotContain(Guid.Empty.ToString(), delivery.ResponsesUrl);
        Assert.DoesNotContain("workerId=", delivery.ResponsesUrl);
        Assert.DoesNotContain("accountId=", delivery.ResponsesUrl);
    }

    [Fact]
    public void Build_ParsesMonitoringNotStartedRows()
    {
        var monitoring = new MonitoringCycleReportDto(
            false,
            0,
            1,
            2,
            ["  Авито 34: 2 не запущены — 1/10 (A), 2/10 (B)"],
            [],
            []);

        var data = CreateData(lowBalanceCount: 0, monitoringCycles: monitoring);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        Assert.Single(model.MonitoringCycles.NotStartedRows);
        Assert.Equal("Авито 34", model.MonitoringCycles.NotStartedRows[0].AccountName);
        Assert.Equal(2, model.MonitoringCycles.NotStartedRows[0].NotStartedCount);
        Assert.Contains("1/10 (A)", model.MonitoringCycles.NotStartedRows[0].PositionsText);
        Assert.True(model.MonitoringCycles.HasData);
    }

    [Fact]
    public void Build_FiltersMonitoringLeadSummariesToPositiveTotals()
    {
        var monitoring = new MonitoringCycleReportDto(
            true,
            5,
            0,
            0,
            [],
            [
                new MonitoringCycleLeadSummaryDto("With leads", 3, ["1/10 (A) = 3"]),
                new MonitoringCycleLeadSummaryDto("No leads", 0, [])
            ],
            []);

        var data = CreateData(lowBalanceCount: 0, monitoringCycles: monitoring);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        Assert.Single(model.MonitoringCycles.LeadSummaries);
        Assert.Equal("With leads", model.MonitoringCycles.LeadSummaries[0].AccountName);
        Assert.Equal(1, model.MonitoringCycles.ZeroLeadAccountCount);
    }

    [Fact]
    public void Build_MapsCaptchaToPassTimestamp()
    {
        var firstPass = DateTime.SpecifyKind(new DateTime(2026, 8, 22, 7, 4, 18), DateTimeKind.Utc);
        var secondPass = DateTime.SpecifyKind(new DateTime(2026, 8, 22, 8, 17, 13), DateTimeKind.Utc);
        var monitoring = new MonitoringCycleReportDto(
            true,
            0,
            0,
            0,
            [],
            [],
            [
                new MonitoringCycleAccountReportDto(
                    "Авито 1",
                    firstPass.Date,
                    10,
                    2,
                    0,
                    [
                        new MonitoringCycleSubProfileRowDto(
                            3,
                            10,
                            "Кадровый отдел3",
                            [firstPass, secondPass],
                            ["0", "0"],
                            [],
                            WasStarted: true,
                            CaptchaPerCycle:
                            [
                                new MonitoringCycleCaptchaDto(secondPass, "решена")
                            ],
                            Passes:
                            [
                                new MonitoringCyclePassDto(firstPass, true, 0, true),
                                new MonitoringCyclePassDto(secondPass, true, 0, true, "решена")
                            ])
                    ],
                    [])
            ]);

        var data = CreateData(lowBalanceCount: 0, monitoringCycles: monitoring);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var row = Assert.Single(model.MonitoringCycles.AccountReports[0].Rows);
        Assert.Equal(2, row.CompletionTimesUtc.Count);
        var captcha = Assert.Single(row.CaptchaEvents);
        Assert.Equal(secondPass, captcha.TimestampUtc);
        Assert.Equal("решена", captcha.Status);
        Assert.False(captcha.Unsolved);
        Assert.Equal(2, row.Passes.Count);
        Assert.Null(row.Passes[0].CaptchaStatus);
        Assert.Equal(secondPass, row.Passes[1].TimestampUtc);
        Assert.Equal("решена", row.Passes[1].CaptchaStatus);
        Assert.True(row.Passes[0].LoginSucceeded);
    }

    [Fact]
    public void Build_MapsLoginRequiredToPass()
    {
        var passTime = DateTime.SpecifyKind(new DateTime(2026, 8, 22, 9, 17, 13), DateTimeKind.Utc);
        var monitoring = new MonitoringCycleReportDto(
            true, 0, 0, 0, [], [],
            [
                new MonitoringCycleAccountReportDto(
                    "Авито 1", passTime.Date, 1, 1, 0,
                    [
                        new MonitoringCycleSubProfileRowDto(
                            1, 1, "Основной", [], [], [], WasStarted: true,
                            Passes: [new MonitoringCyclePassDto(
                                passTime, false, 0, false, ErrorDetail: "нужен вход", LoginRequired: true)])
                    ],
                    [])
            ]);

        var model = BuildModel(CreateData(lowBalanceCount: 0, monitoringCycles: monitoring), DashboardPeriod.Today, new FakeOfficeContext());

        var pass = Assert.Single(model.MonitoringCycles.AccountReports[0].Rows[0].Passes);
        Assert.True(pass.LoginRequired);
        Assert.False(pass.LoginSucceeded);
    }

    [Fact]
    public void Build_MapsNotStartedReason()
    {
        var stoppedAt = DateTime.SpecifyKind(new DateTime(2026, 8, 22, 11, 30, 33), DateTimeKind.Utc);
        var monitoring = new MonitoringCycleReportDto(
            true,
            0,
            1,
            1,
            ["  Авито 1: 1 не запущены — 2/10 (Березники 10)"],
            [],
            [
                new MonitoringCycleAccountReportDto(
                    "Авито 1",
                    stoppedAt.Date,
                    10,
                    1,
                    0,
                    [
                        new MonitoringCycleSubProfileRowDto(
                            2,
                            10,
                            "Березники 10",
                            [],
                            [],
                            [],
                            WasStarted: false,
                            NotStartedReason: "очередь не дошла: капча на «отдел 4»",
                            NotStartedAtUtc: stoppedAt)
                    ],
                    ["2/10 (Березники 10)"])
            ]);

        var data = CreateData(lowBalanceCount: 0, monitoringCycles: monitoring);
        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        var row = Assert.Single(model.MonitoringCycles.AccountReports[0].Rows);
        Assert.True(row.HasNotStarted);
        Assert.Equal("очередь не дошла: капча на «отдел 4»", row.NotStartedReason);
        Assert.Equal(stoppedAt, row.NotStartedAtUtc);
        Assert.Empty(row.Passes);
    }

    [Fact]
    public void Build_MissingMonitoringCyclesFromOlderApi_ReturnsEmptyReport()
    {
        var data = CreateData(lowBalanceCount: 0) with
        {
            // A rolling Web/API deployment can deserialize this newly-added field
            // as null until the API instance has been updated.
            MonitoringCycles = null!
        };

        var model = BuildModel(data, DashboardPeriod.Today, new FakeOfficeContext());

        Assert.False(model.MonitoringCycles.HasData);
        Assert.Empty(model.MonitoringCycles.AccountReports);
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

    private static OfficeStatisticsDto CreateData(
        int lowBalanceCount,
        MonitoringCycleReportDto? monitoringCycles = null)
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
            [],
            [],
            new HrInsightsDto([], [], [], [], "н/д", "0%"),
            monitoringCycles ?? new MonitoringCycleReportDto(false, 0, 0, 0, [], [], []),
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
