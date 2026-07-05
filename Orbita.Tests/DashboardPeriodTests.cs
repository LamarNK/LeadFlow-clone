using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class DashboardPeriodTests
{
    [Fact]
    public void All_ActivePresetAndLabel()
    {
        var period = DashboardPeriod.All;

        Assert.Equal("all", period.ActivePreset);
        Assert.Equal("Все", period.Label);
        Assert.True(period.IsAllTime);
        Assert.False(period.IsTodayOnly);
    }

    [Fact]
    public void Parse_AllRange_MatchesAllPreset()
    {
        var all = DashboardPeriod.All;
        var parsed = DashboardPeriod.Parse(all.FromIso, all.ToIso);

        Assert.Equal("all", parsed.ActivePreset);
        Assert.Equal(all.From, parsed.From);
        Assert.Equal(all.To, parsed.To);
    }
}