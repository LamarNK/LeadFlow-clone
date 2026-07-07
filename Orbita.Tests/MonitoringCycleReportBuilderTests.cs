using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class MonitoringCycleReportBuilderTests
{
    private static readonly DateTime Day = new(2026, 6, 23);

    private static MonitoringCycleSentResponse Sent(
        string accountName,
        string subProfileName,
        DateTime timestampUtc) =>
        new(accountName, subProfileName, timestampUtc);

    [Fact]
    public void Build_SingleDay_BuildsDetailedAccountReport()
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(2), TimeZoneInfo.Local);
        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (
                utc,
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 1: контракт РФ 1 (id=1).",
                null),
            (
                utc.AddMinutes(3),
                "Суб-профиль «контракт РФ 1» аккаунта Avito 1: новых для LeadFlow 2, обработано сейчас 2 (лимит аккаунта 2/10), отложено на следующие циклы 0.",
                null),
            (
                utc.AddMinutes(4),
                "Суб-профиль «контракт РФ 1» (объявления): активных 1, заблокированных 0, drafts=0.",
                null),
            (
                utc.AddHours(3),
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 1: контракт РФ 1 (id=1).",
                null),
            (
                utc.AddHours(3).AddMinutes(2),
                "Суб-профиль «контракт РФ 1» аккаунта Avito 1: новых для LeadFlow 1, обработано сейчас 1 (лимит аккаунта 1/10), отложено на следующие циклы 0.",
                null)
        };
        var sent = new List<MonitoringCycleSentResponse>
        {
            Sent("Avito 1", "контракт РФ 1", utc.AddMinutes(3)),
            Sent("Avito 1", "контракт РФ 1", utc.AddMinutes(3).AddSeconds(1)),
            Sent("Avito 1", "контракт РФ 1", utc.AddHours(3).AddMinutes(2))
        };

        var report = MonitoringCycleReportBuilder.Build(
            rows,
            Day,
            Day,
            new HashSet<string>(["Avito 1"], StringComparer.OrdinalIgnoreCase),
            sent);

        Assert.True(report.IsDetailed);
        Assert.Equal(3, report.TotalLeads);
        Assert.Single(report.AccountReports);
        Assert.Equal(2, report.AccountReports[0].CycleCount);
        Assert.Equal("контракт РФ 1", report.AccountReports[0].Rows[0].Name);
        Assert.Equal(["2", "1"], report.AccountReports[0].Rows[0].LeadsPerCycle);
    }

    [Fact]
    public void Build_MultiDay_ReturnsSummaryOnly()
    {
        var completionUtc = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(1).AddMinutes(2), TimeZoneInfo.Local);
        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (
                TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(1), TimeZoneInfo.Local),
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 1: контракт РФ 1 (id=1).",
                null),
            (
                completionUtc,
                "Суб-профиль «контракт РФ 1» аккаунта Avito 1: новых для LeadFlow 4, обработано сейчас 4 (лимит аккаунта 4/10), отложено на следующие циклы 0.",
                null)
        };
        var sent = Enumerable.Range(0, 4)
            .Select(i => Sent("Avito 1", "контракт РФ 1", completionUtc.AddSeconds(i)))
            .ToList();

        var report = MonitoringCycleReportBuilder.Build(
            rows,
            Day,
            Day.AddDays(2),
            new HashSet<string>(["Avito 1"], StringComparer.OrdinalIgnoreCase),
            sent);

        Assert.False(report.IsDetailed);
        Assert.Empty(report.AccountReports);
        Assert.Single(report.LeadSummaries);
        Assert.Equal(4, report.LeadSummaries[0].TotalLeads);
    }

    [Fact]
    public void Build_FiltersByAllowedAccounts()
    {
        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (
                TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(1), TimeZoneInfo.Local),
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 1: контракт РФ 1 (id=1).",
                null),
            (
                TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(2), TimeZoneInfo.Local),
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 2: Контракт (id=2).",
                null)
        };

        var report = MonitoringCycleReportBuilder.Build(
            rows,
            Day,
            Day,
            new HashSet<string>(["Avito 2"], StringComparer.OrdinalIgnoreCase));

        Assert.Single(report.LeadSummaries);
        Assert.Equal("Avito 2", report.LeadSummaries[0].AccountName);
    }

    [Fact]
    public void Build_NewWorkerLogFormat_BuildsDetailedAccountReport()
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(2), TimeZoneInfo.Local);
        var account = "Аккаунт «Авито 37» (AdsPower k123)";
        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (utc, $"{account} · «Кадровый отдел4» (1/10): переключение субпрофиля", null),
            (
                utc.AddMinutes(2),
                $"{account} · «Кадровый отдел4» — отклики: страница CRM; в DOM 5 карточек; скрипт извлёк 3, валидных 2; новых к публикации 2",
                null),
            (
                utc.AddMinutes(3),
                $"{account} · «Кадровый отдел4» — опубликовано 2 из 2 готовых (лимит 10 на субпрофиль за проход).",
                null),
            (utc.AddHours(3), $"{account} · «Кадровый отдел4» (1/10): переключение субпрофиля", null),
            (
                utc.AddHours(3).AddMinutes(2),
                $"{account} · «Кадровый отдел4» — отклики: страница CRM; в DOM 4 карточек; скрипт извлёк 2, валидных 1; новых к публикации 1",
                null),
            (
                utc.AddHours(3).AddMinutes(3),
                $"{account} · «Кадровый отдел4» — опубликовано 1 из 1 готовых (лимит 10 на субпрофиль за проход).",
                null)
        };
        var sent = new List<MonitoringCycleSentResponse>
        {
            Sent("Авито 37", "Кадровый отдел4", utc.AddMinutes(3)),
            Sent("Авито 37", "Кадровый отдел4", utc.AddMinutes(3).AddSeconds(1)),
            Sent("Авито 37", "Кадровый отдел4", utc.AddHours(3).AddMinutes(3))
        };

        var report = MonitoringCycleReportBuilder.Build(
            rows,
            Day,
            Day,
            new HashSet<string>(["Авито 37"], StringComparer.OrdinalIgnoreCase),
            sent);

        Assert.True(report.IsDetailed);
        Assert.Equal(3, report.TotalLeads);
        Assert.Single(report.AccountReports);
        Assert.Equal("Авито 37", report.AccountReports[0].AccountName);
        Assert.Equal(2, report.AccountReports[0].CycleCount);
        Assert.Equal(["2", "1"], report.AccountReports[0].Rows[0].LeadsPerCycle);
    }

    [Fact]
    public void Build_UsesActualBitrixSends_NotLogProcessedCounts()
    {
        var utc = TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(2), TimeZoneInfo.Local);
        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (
                utc,
                "Avito Pro переключаем суб-профиль 1/10 (отклики+объявления, стрим) для Avito 2: контракт РФ 1 (id=1).",
                null),
            (
                utc.AddMinutes(3),
                "Суб-профиль «контракт РФ 1» аккаунта Avito 2: новых для LeadFlow 64, обработано сейчас 64 (лимит аккаунта 64/10), отложено на следующие циклы 0.",
                null)
        };
        var sent = new List<MonitoringCycleSentResponse>
        {
            Sent("Avito 2", "контракт РФ 1", utc.AddMinutes(3))
        };

        var report = MonitoringCycleReportBuilder.Build(
            rows,
            Day,
            Day,
            new HashSet<string>(["Avito 2"], StringComparer.OrdinalIgnoreCase),
            sent);

        Assert.Equal(1, report.TotalLeads);
        Assert.Single(report.LeadSummaries);
        Assert.Equal(1, report.LeadSummaries[0].TotalLeads);
        Assert.Equal(1, report.AccountReports[0].TotalLeads);
        Assert.Equal(["1"], report.AccountReports[0].Rows[0].LeadsPerCycle);
    }

    [Fact]
    public void Build_ParsesStructuredJsonContext()
    {
        var propertiesJson = """
            {
              "message": "Avito Pro переключаем суб-профиль 3/10 (отклики+объявления, стрим) для Авито 37: Кадровый отдел4 (id=439394231).",
              "context": {
                "accountName": "Авито 37",
                "subProfile.name": "Кадровый отдел4",
                "subProfile.index": 3,
                "subProfile.total": 10
              }
            }
            """;

        var rows = new List<(DateTime TimestampUtc, string Message, string? PropertiesJson)>
        {
            (TimeZoneInfo.ConvertTimeToUtc(Day.AddHours(5), TimeZoneInfo.Local), "ignored", propertiesJson)
        };

        var report = MonitoringCycleReportBuilder.Build(rows, Day, Day, new HashSet<string>(["Авито 37"], StringComparer.OrdinalIgnoreCase));

        Assert.True(report.IsDetailed);
        Assert.Single(report.AccountReports);
        Assert.Equal(3, report.AccountReports[0].Rows[0].Position);
        Assert.Equal("Кадровый отдел4", report.AccountReports[0].Rows[0].Name);
        Assert.Equal(0, report.TotalLeads);
    }
}