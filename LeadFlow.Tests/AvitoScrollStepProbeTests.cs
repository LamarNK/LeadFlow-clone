using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoScrollStepProbeTests
{
    [Fact]
    public void Parse_ValidIncrementalPackage_ReturnsOnlyNewCards()
    {
        const string raw = """
            {"itemCount":4,"moved":true,"atEnd":false,"fullRescan":false,"structureValid":true,
             "newItems":[
               {"index":2,"fullName":"Новый Один","cardFingerprint":"avito-card:one","city":"Самара","age":"31","gender":"male","phoneDigits":"79990000001"},
               {"index":3,"fullName":"Новый Два","cardFingerprint":"avito-card:two","city":"Уфа","age":"","gender":"female","phoneDigits":""}
             ]}
            """;

        var probe = AvitoScrollStepProbeParser.Parse(raw, previousItemCount: 2);

        Assert.False(probe.IsFullRescan);
        Assert.False(probe.RequiresFallbackRescan);
        Assert.Equal(4, probe.ItemCount);
        Assert.Collection(
            probe.NewItems,
            item => Assert.Equal((2, "Новый Один", "avito-card:one", "79990000001"), (item.Index, item.FullName, item.CardFingerprint, item.PhoneDigits)),
            item => Assert.Equal((3, "Новый Два", "avito-card:two", ""), (item.Index, item.FullName, item.CardFingerprint, item.PhoneDigits)));
    }

    [Theory]
    [InlineData("not-json", 5)]
    [InlineData("{\"itemCount\":3,\"moved\":true,\"atEnd\":false,\"fullRescan\":false,\"structureValid\":true,\"newItems\":[]}", 5)]
    [InlineData("{\"itemCount\":6,\"moved\":true,\"atEnd\":false,\"fullRescan\":false,\"structureValid\":false,\"newItems\":[]}", 5)]
    public void Parse_MalformedShrunkOrChangedDom_RequiresConservativeFullRescan(string raw, int previousItemCount)
    {
        var probe = AvitoScrollStepProbeParser.Parse(raw, previousItemCount);

        Assert.True(probe.IsFullRescan || probe.RequiresFallbackRescan);
        Assert.False(probe.AllowEarlyStop);
    }

    [Fact]
    public void ScrollGeometry_AcceptsFractionalScrollOffsets()
    {
        var geometry = AvitoScrollStepProbeParser.TryParseGeometry(
            "{\"scrollTop\":19.5,\"clientHeight\":600,\"scrollHeight\":1400.25}");

        Assert.NotNull(geometry);
        Assert.Equal(19.5, geometry.ScrollTop);
        Assert.Equal(1400.25, geometry.ScrollHeight);
    }
}
