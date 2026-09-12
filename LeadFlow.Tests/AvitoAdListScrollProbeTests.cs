using LeadFlow.Core.Services;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdListScrollProbeTests
{
    [Fact]
    public void Parse_ReadsCountMarkersAndEndState()
    {
        const string raw = """
            {"count":12,"first":"item-snippet/1","last":"item-snippet/12","moved":true,"atEnd":false,"loader":false,"loadMoreClicked":true}
            """;

        var probe = AvitoAdListScrollProbeParser.Parse(raw);

        Assert.Equal(12, probe.Count);
        Assert.Equal("item-snippet/1", probe.FirstMarker);
        Assert.Equal("item-snippet/12", probe.LastMarker);
        Assert.True(probe.Moved);
        Assert.False(probe.AtEnd);
        Assert.False(probe.Loader);
        Assert.True(probe.LoadMoreClicked);
        Assert.False(probe.ScrollIdle);
        Assert.True(probe.FirstWindowMoved("item-snippet/0"));
        Assert.False(probe.FirstWindowMoved("item-snippet/1"));
    }

    [Fact]
    public void Parse_NoMovementAndNoLoader_IsScrollIdle()
    {
        const string raw = """
            {"count":4,"first":"item-snippet/1","last":"item-snippet/4","moved":false,"atEnd":true,"loader":false,"loadMoreClicked":false}
            """;

        var probe = AvitoAdListScrollProbeParser.Parse(raw);

        Assert.True(probe.ScrollIdle);
        Assert.True(probe.AtEnd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"count\":-1}")]
    public void Parse_Malformed_ReturnsEmptyFallback(string raw)
    {
        var probe = AvitoAdListScrollProbeParser.Parse(raw);

        Assert.Equal(0, probe.Count);
        Assert.True(probe.ScrollIdle);
        Assert.False(probe.Moved);
    }

    [Fact]
    public void ScrollStepScript_ScrollsSnippetsWithoutAdControlClicks()
    {
        var script = AvitoAdListPageScripts.ScrollStepScript;

        Assert.Contains("[data-marker^='item-snippet/']", script, StringComparison.Ordinal);
        Assert.Contains("scrollBy", script, StringComparison.Ordinal);
        Assert.Contains("behavior: \"auto\"", script, StringComparison.Ordinal);
        Assert.Contains("показать ещё", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pagination-button(more)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pagination-button(next)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("item-snippet/item-buttons", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Timing_BoundsScrollAndPagination()
    {
        Assert.True(MonitoringTiming.AvitoAdsListMaxScrollRounds >= 20);
        Assert.True(MonitoringTiming.AvitoAdsListStableScrollRounds >= 2);
        Assert.True(MonitoringTiming.AvitoAdsListMaxPages >= 5);
    }
}
