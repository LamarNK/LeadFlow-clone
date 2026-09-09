using Orbita.Api.Services;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sales_DifferentOfficeFunnelsKeepExactSourceNamesAndDoNotLeakAcrossFilters(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        // Five visible funnel variants, not an assumption about production office IDs.
        string[][] sources = [
            ["Лид", "НДЗ 73", "НДЗ 2.6", "Подменка"],
            ["Лид", "НДЗ", "НДЗ 2", "Подменка"],
            ["Лид", "Недоступные подменные", "НДЗ", "НДЗ 2"],
            ["Лид", "Лид(Важный)", "НДЗ 73", "НДЗ 2.6", "Подменка"],
            ["Лид", "НДЗ", "НДЗ 2"]
        ];
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var fixtures = new List<(Guid Office, string Manager, string[] Stages, HashSet<Guid> Cards)>();
        for (var i = 0; i < sources.Length; i++)
        {
            var office = Guid.NewGuid();
            var manager = $"funnel-manager-{i}";
            var stages = sources[i].Concat(new[] { "Переговоры", "Анкета", "Билет", "Готовится к отправке", "В пути", "На подписании" }).ToArray();
            h.AddOffice(office, $"Вариант {i + 1}", stages);
            h.AddManager(manager, office, $"Менеджер {i + 1}", 100, false);
            var ids = new HashSet<Guid>();
            foreach (var source in sources[i])
            {
                var reached = SalesCard(h, manager, from.AddDays(-20), source, office);
                SalesMove(h, reached, source, "Переговоры", from.AddHours(1), manager);
                ids.Add(reached.Id);
                var refusal = SalesCard(h, manager, from.AddDays(-20), source, office);
                SalesClose(h, refusal, source, "Неактуально", from.AddHours(2), manager);
                ids.Add(refusal.Id);
                var unanswered = SalesCard(h, manager, from.AddDays(-20), source, office);
                SalesClose(h, unanswered, source, "НДЗ", from.AddHours(3), manager);
            }
            fixtures.Add((office, manager, stages, ids));
        }
        await h.Db.SaveChangesAsync();

        for (var i = 0; i < fixtures.Count; i++)
        {
            var fixture = fixtures[i];
            foreach (var manager in new string?[] { null, fixture.Manager })
            {
                var q = SalesQuery(from, fixture.Office, manager);
                var data = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, q)).Data!;
                Assert.Equal(0, SalesCount(data, CrmSalesMetrics.Received));
                Assert.Equal(fixture.Cards.Count, SalesCount(data, CrmSalesMetrics.Contacts));
                Assert.Equal(sources[i].Order(), data.Sales!.ContactSources.Select(x => x.Label).Order());
                Assert.All(data.Sales.ContactSources, x => Assert.Equal(2, x.Count));
                Assert.Equal(fixture.Stages, Assert.Single(data.Sales.Offices).Stages.Select(x => x.Stage));
                Assert.All(data.Sales.Offices.Single().Stages, x => Assert.False(x.Archived));
                Assert.Equal(fixture.Cards.Count, Assert.Single(data.Sales.Managers).Contacts);
                var proof = (await h.Sut.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, q,
                    CrmSalesMetrics.Contacts, 1)).Data!;
                Assert.Equal(fixture.Cards.Count, proof.Total);
                Assert.True(fixture.Cards.SetEquals(proof.Rows.Select(x => x.CardId)));
            }
        }
        var allQuery = new CrmAnalyticsQuery(from, from.AddDays(1), null, null, CrmAnalyticsCohortBases.Received);
        var all = (await h.Sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, allQuery)).Data!;
        Assert.Equal(fixtures.Sum(x => x.Cards.Count), SalesCount(all, CrmSalesMetrics.Contacts));
        Assert.Equal(sources.SelectMany(x => x).Distinct().Order(), all.Sales!.ContactSources.Select(x => x.Label).Order());
        Assert.Equal(2 * sources.Count(x => x.Contains("НДЗ")), all.Sales.ContactSources.Single(x => x.Label == "НДЗ").Count);
        Assert.Equal(2 * sources.Count(x => x.Contains("НДЗ 73")), all.Sales.ContactSources.Single(x => x.Label == "НДЗ 73").Count);
    }
}
