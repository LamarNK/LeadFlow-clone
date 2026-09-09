using Microsoft.Extensions.Options;
using Orbita.Api.Options;
using Orbita.Api.Services;
using Orbita.Contracts;
using System.Text.Json;

namespace Orbita.Tests;

public sealed partial class CrmAnalyticsQueryServiceTests
{
    [Fact]
    public async Task Cache_SeparatesReceiptAndFirstAssignmentCohorts()
    {
        await using var h = await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Lead, from.AddDays(-1),
            initialAssignedAtUtc: from.AddHours(2));
        h.Db.CrmCandidateCards.Add(card);
        await h.Db.SaveChangesAsync();
        var cache = new AnalyticsTestCache();
        var sut = new CrmAnalyticsQueryService(h.Db, new FixedTimeProvider(Now),
            Options.Create(new CrmAnalyticsOptions()), cache);
        var received = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, ManagerOneId,
            CrmAnalyticsCohortBases.Received);
        var assigned = received with { CohortBasis = CrmAnalyticsCohortBases.FirstAssigned };

        Assert.Equal(0, (await sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, received)).Data!.Cards.Received);
        Assert.Equal(1, (await sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, assigned)).Data!.Cards.Received);
        Assert.Equal(0, (await sut.GetAsync(OfficeScope.GlobalAdmin, "admin", true, received)).Data!.Cards.Received);
        Assert.Equal(2, cache.Loads);
        Assert.Equal(1, cache.Hits);
        Assert.All(cache.Parameters, p => Assert.Contains("crm-analytics-v3-sales", p));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cache_FreshRequestRebuildsEvidenceAfterDashboardCacheHit(bool relational)
    {
        await using var h = relational ? await Harness.CreateSqliteAsync(Now) : await Harness.CreateAsync(Now);
        h.AddOffice(OfficeOneId, "Первый", CrmStages.All);
        h.AddManager(ManagerOneId, OfficeOneId, "Анна", 10, false);
        var from = Now.UtcDateTime.Date.AddDays(-1);
        var card = NewCard(OfficeOneId, ManagerOneId, CrmStages.Negotiations, from.AddHours(1));
        AttachReviewResponse(card);
        var history = NewStageHistory(card.Id, $"{CrmStages.Lead} → {CrmStages.Negotiations}",
            from.AddHours(3), ManagerOneId, "Анна");
        history.OfficeId = OfficeOneId;
        h.Db.CrmCandidateCards.Add(card);
        h.Db.CrmCandidateHistory.Add(history);
        await h.Db.SaveChangesAsync();
        var cache = new AnalyticsTestCache();
        CrmAnalyticsQueryService NewRequest() => new(h.Db, new FixedTimeProvider(Now),
            Options.Create(new CrmAnalyticsOptions()), cache);
        var query = new CrmAnalyticsQuery(from, from.AddDays(1), OfficeOneId, null,
            CrmAnalyticsCohortBases.Received);
        Assert.Equal(1, (await NewRequest().GetAsync(OfficeScope.GlobalAdmin, "admin", true, query)).Data!.Cards.Received);
        var next = NewRequest();
        Assert.Equal(1, (await next.GetAsync(OfficeScope.GlobalAdmin, "admin", true, query)).Data!.Cards.Received);
        Assert.Equal(1, cache.Hits);
        var received = await next.GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, query, "cohort.received", 1);
        Assert.Equal(CrmAnalyticsQueryOutcome.Success, received.Outcome);
        Assert.Equal(card.Id, Assert.Single(received.Data!.Rows).CardId);
        var contacts = await NewRequest().GetEvidenceAsync(OfficeScope.GlobalAdmin, "admin", true, query, "cohort.contacts", 1);
        var row = Assert.Single(contacts.Data!.Rows);
        Assert.Equal(card.Id, row.CardId);
        Assert.Equal(history.CreatedAtUtc, Assert.Single(row.BasisEvents!).AtUtc);
        Assert.Equal(CrmAnalyticsQueryOutcome.Forbidden,
            (await NewRequest().GetEvidenceAsync(OfficeScope.NoAccess, "admin", true, query, "cohort.received", 1)).Outcome);
        Assert.Equal(1, cache.Hits); // Evidence never depends on cached totals or another request's side effects.
    }

    private sealed class AnalyticsTestCache : IOrbitaQueryCache
    {
        private readonly Dictionary<string, string> data = new();
        public int Loads { get; private set; }
        public int Hits { get; private set; }
        public List<string> Parameters { get; } = [];

        public async Task<T> GetOrCreateAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default)
        {
            Parameters.Add(JsonSerializer.Serialize(parameters));
            var key = OrbitaQueryCache.BuildDataKey(domain, officeId, audience, 1, parameters);
            if (data.TryGetValue(key, out var json))
            {
                Hits++;
                return JsonSerializer.Deserialize<T>(json)!;
            }
            Loads++;
            var result = await factory(cancellationToken);
            data[key] = JsonSerializer.Serialize(result);
            return result;
        }

        public Task<T> GetOrCreateDistributedAsync<T>(OrbitaCacheDomain domain, Guid? officeId, string? audience,
            object? parameters, OrbitaCachePolicy policy, Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) =>
            GetOrCreateAsync(domain, officeId, audience, parameters, policy, factory, cancellationToken);
        public Task InvalidateAsync(IReadOnlyList<PanelChangeKind> changes, Guid? officeId) => Task.CompletedTask;
        public void ClearLocalVersion(OrbitaCacheDomain domain, Guid? officeId) { }
    }
}
