using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class CrmDailyDistributionTests
{
    [Fact]
    public void BuildBalancedPlan_ImportedFileWithOneHundredOneLeadsAndFourManagers_SplitsTwentySixTwentyFive()
    {
        var plan = CrmDailyDistribution.BuildBalancedPlan(
            Enumerable.Range(1, 101).Select(_ => Guid.NewGuid()),
            ["manager-1", "manager-2", "manager-3", "manager-4"],
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            new DateOnly(2026, 8, 25),
            $"{CrmDailyDistribution.LeadPool}-file-import");

        var counts = plan
            .GroupBy(x => x.ManagerUserId)
            .Select(x => x.Count())
            .OrderByDescending(x => x)
            .ToArray();

        Assert.Equal([26, 25, 25, 25], counts);
    }

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
    public void SelectNextForNewLead_UsesOnlyRoundRobinCursor()
    {
        var managers = new[] { "a", "b", "c" };

        Assert.Equal("c", CrmDailyDistribution.SelectNextForNewLead(managers, "b"));
        Assert.Equal("a", CrmDailyDistribution.SelectNextForNewLead(managers, "c"));
        Assert.Equal("a", CrmDailyDistribution.SelectNextForNewLead(managers, null));
    }

    [Fact]
    public void SelectNextForNewLead_TwentyFiveNewLeadsAndThreeManagers_DoesNotCatchUpNewManager()
    {
        var managers = new[] { "a", "b", "c" };
        var counts = managers.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);
        string? last = "b";

        for (var index = 0; index < 25; index++)
        {
            last = CrmDailyDistribution.SelectNextForNewLead(managers, last);
            Assert.NotNull(last);
            counts[last!]++;
        }

        Assert.Equal(25, counts.Values.Sum());
        Assert.Equal(1, counts.Values.Max() - counts.Values.Min());
        Assert.All(counts.Values, count => Assert.InRange(count, 8, 9));
    }

    [Fact]
    public void ResolveNdzStages_UsesProductionOfficeNamesWithoutMatchingSubstituteStages()
    {
        var resolved = CrmDailyDistribution.ResolveNdzStages(
        [
            "Лид",
            "Недоступные подменные",
            "НДЗ",
            "НДЗ 2",
            "Пустые",
            "НДЗ с подменным",
            "НДЗ с подменным 2",
            "Переговоры"
        ]);

        Assert.Equal("НДЗ", resolved.PrimaryStage);
        Assert.Equal(["НДЗ", "НДЗ 2"], resolved.Stages);
    }

    [Theory]
    [InlineData("НДЗ")]
    [InlineData("НДЗ 2")]
    [InlineData("НДЗ 73")]
    [InlineData("НДЗ 2.6")]
    public void IsNdz_AcceptsProductionAndLocalAliases(string stage)
    {
        Assert.True(CrmDailyDistribution.IsNdz(stage));
    }

    [Theory]
    [InlineData("Недоступные подменные")]
    [InlineData("НДЗ с подменным")]
    [InlineData("НДЗ с подменным 2")]
    [InlineData("Пустые")]
    public void IsNdz_DoesNotMixSubstitutePoolsIntoRegularNdz(string stage)
    {
        Assert.False(CrmDailyDistribution.IsNdz(stage));
    }

    [Fact]
    public void ResolveUnavailableSubstituteStage_OnlyEnablesConfiguredThirdOfficeStage()
    {
        var stages = new[] { "Лид", "Недоступные подменные", "НДЗ" };

        Assert.Equal(
            "Недоступные подменные",
            CrmDailyDistribution.ResolveUnavailableSubstituteStage(" 3 ОФИС ", stages));
        Assert.Null(CrmDailyDistribution.ResolveUnavailableSubstituteStage("2 офис", stages));
        Assert.Null(CrmDailyDistribution.ResolveUnavailableSubstituteStage("3 офис", ["Лид", "НДЗ"]));
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
