using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmDailyDistributionTests
{
    [Fact]
    public void BuildBalancedPlan_OneHundredCardsAndFourManagers_GivesTwentyFiveEach()
    {
        var cards = Enumerable.Range(1, 100).Select(DeterministicGuid).ToList();
        var managers = new[] { "manager-1", "manager-2", "manager-3", "manager-4" };

        var plan = CrmDailyDistribution.BuildBalancedPlan(
            cards,
            managers,
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            new DateOnly(2026, 8, 14),
            CrmDailyDistribution.LeadPool);

        Assert.Equal(100, plan.Count);
        Assert.All(plan.GroupBy(x => x.ManagerUserId), group => Assert.Equal(25, group.Count()));
    }

    [Fact]
    public void BuildBalancedPlan_UnevenCount_DiffersByAtMostOne()
    {
        var plan = CrmDailyDistribution.BuildBalancedPlan(
            Enumerable.Range(1, 103).Select(DeterministicGuid),
            new[] { "manager-1", "manager-2", "manager-3", "manager-4" },
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            new DateOnly(2026, 8, 14),
            CrmDailyDistribution.NdzPool);

        var counts = plan.GroupBy(x => x.ManagerUserId).Select(x => x.Count()).ToList();
        Assert.Equal(4, counts.Count);
        Assert.Equal(1, counts.Max() - counts.Min());
    }

    [Fact]
    public void BuildBalancedPlan_RetryForSameDay_IsDeterministic()
    {
        var cards = Enumerable.Range(1, 17).Select(DeterministicGuid).ToList();
        var managers = new[] { "manager-1", "manager-2", "manager-3" };
        var officeId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var date = new DateOnly(2026, 8, 14);

        var first = CrmDailyDistribution.BuildBalancedPlan(
            cards,
            managers,
            officeId,
            date,
            CrmDailyDistribution.LeadPool);
        var retry = CrmDailyDistribution.BuildBalancedPlan(
            cards.AsEnumerable().Reverse(),
            managers.AsEnumerable().Reverse(),
            officeId,
            date,
            CrmDailyDistribution.LeadPool);

        Assert.Equal(first, retry);
    }

    [Fact]
    public void SelectNextForNewLead_UsesDailyCountAndRoundRobinCursor()
    {
        var managers = new[] { "a", "b", "c" };
        var counters = new[]
        {
            new CrmDailyDistribution.Counter("a", 8),
            new CrmDailyDistribution.Counter("b", 7),
            new CrmDailyDistribution.Counter("c", 7)
        };

        Assert.Equal("c", CrmDailyDistribution.SelectNextForNewLead(managers, counters, "b"));
        Assert.Equal("b", CrmDailyDistribution.SelectNextForNewLead(managers, counters, "c"));
    }

    [Fact]
    public void BusinessDate_UsesYekaterinburgDayBoundary()
    {
        Assert.Equal(
            new DateOnly(2026, 8, 14),
            CrmDailyDistribution.BusinessDate(new DateTime(2026, 8, 13, 19, 0, 0, DateTimeKind.Utc)));
    }

    private static Guid DeterministicGuid(int value)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new Guid(bytes);
    }
}
