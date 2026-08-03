using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class BitrixWorkforceDistributionTests
{
    private static readonly BitrixWorkforceDistribution.StageMap Stages = new(
        New: "NEW",
        MissedCall: "UC_FU2T4L",
        MissedCallSecondary: "UC_ENSD7E",
        SubstituteMissedCall: "UC_6OQRTF",
        SubstituteMissedCallSecondary: "UC_WT8KQY");

    [Theory]
    [InlineData("NEW", BitrixWorkforceDistribution.NewScenario, "NEW", false)]
    [InlineData("UC_FU2T4L", BitrixWorkforceDistribution.MissedCallScenario, "UC_FU2T4L", true)]
    [InlineData("UC_ENSD7E", BitrixWorkforceDistribution.MissedCallScenario, "UC_FU2T4L", true)]
    [InlineData("UC_6OQRTF", BitrixWorkforceDistribution.SubstituteMissedCallScenario, "UC_6OQRTF", true)]
    [InlineData("UC_WT8KQY", BitrixWorkforceDistribution.SubstituteMissedCallScenario, "UC_6OQRTF", true)]
    public void Classify_MapsConfiguredStages(
        string stage,
        string expectedScenario,
        string expectedTarget,
        bool usesMorningWindow)
    {
        var result = BitrixWorkforceDistribution.Classify(stage, Stages);

        Assert.NotNull(result);
        Assert.Equal(expectedScenario, result.Name);
        Assert.Equal(expectedTarget, result.TargetStageId);
        Assert.Equal(usesMorningWindow, result.UsesMorningWindow);
    }

    [Fact]
    public void Classify_IgnoresUnknownStage()
    {
        Assert.Null(BitrixWorkforceDistribution.Classify("WON", Stages));
    }

    [Fact]
    public void EligibleManagerIds_PreservesConfiguredOrderAndFiltersStatuses()
    {
        var result = BitrixWorkforceDistribution.EligibleManagerIds(
        [
            new(17, "OPENED"),
            new(9, "CLOSED"),
            new(21, "paused"),
            new(17, "OPENED"),
            new(31, "OPENED", IsActive: false)
        ]);

        Assert.Equal([17L, 21L], result);
    }

    [Theory]
    [InlineData(null, 17)]
    [InlineData(17, 21)]
    [InlineData(21, 17)]
    [InlineData(999, 17)]
    public void SelectNextManager_UsesRoundRobin(int? lastManagerId, int expected)
    {
        var result = BitrixWorkforceDistribution.SelectNextManager(
            [17, 21],
            lastManagerId is null ? null : lastManagerId.Value);

        Assert.Equal((long?)expected, result);
    }

    [Fact]
    public void SelectNextManager_ReturnsNullWhenNobodyIsEligible()
    {
        Assert.Null(BitrixWorkforceDistribution.SelectNextManager([], lastManagerId: null));
    }

    [Fact]
    public void MorningWindow_UsesPortalTimeZoneAndDefersAfterWindow()
    {
        var moscow = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Moscow",
            TimeSpan.FromHours(3),
            "Test/Moscow",
            "Test/Moscow");
        var inside = new DateTimeOffset(2026, 7, 30, 6, 30, 0, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 7, 30, 9, 30, 0, TimeSpan.Zero);

        Assert.True(BitrixWorkforceDistribution.IsInsideWindow(
            inside,
            moscow,
            new TimeOnly(8, 0),
            new TimeOnly(11, 0)));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 31, 8, 0, 0, TimeSpan.FromHours(3)),
            BitrixWorkforceDistribution.NextWindowStart(
                after,
                moscow,
                new TimeOnly(8, 0),
                new TimeOnly(11, 0)));
    }

    [Theory]
    [InlineData(0, 25, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 25, 3)]
    [InlineData(10, 100, 10)]
    [InlineData(10, 150, 10)]
    public void CalculateInitialReleaseCount_UsesExplicitPercentage(
        int queued,
        int percent,
        int expected)
    {
        Assert.Equal(
            expected,
            BitrixWorkforceDistribution.CalculateInitialReleaseCount(queued, percent));
    }

    [Fact]
    public void Reserve_ReleasesWhenSecondManagerAppearsOrDeadlinePasses()
    {
        var now = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);
        var reserveUntil = now.AddHours(2);

        Assert.False(BitrixWorkforceDistribution.CanReleaseReservedDeals(1, now, reserveUntil));
        Assert.True(BitrixWorkforceDistribution.CanReleaseReservedDeals(2, now, reserveUntil));
        Assert.True(BitrixWorkforceDistribution.CanReleaseReservedDeals(1, reserveUntil, reserveUntil));
    }
}
