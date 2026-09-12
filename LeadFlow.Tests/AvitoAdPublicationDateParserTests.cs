using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoAdPublicationDateParserTests
{
    [Fact]
    public void TryParseItemIdLine_UsesCurrentYear()
    {
        var captured = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(AvitoAdPublicationDateParser.TryParseItemIdLine(
            "№ 8302808573, 9 сентября в 20:42",
            captured,
            out var id,
            out var published,
            out _));

        Assert.Equal("8302808573", id);
        var local = AvitoAdBusinessTime.ToLocal(published);
        Assert.Equal(2026, local.Year);
        Assert.Equal(9, local.Month);
        Assert.Equal(9, local.Day);
        Assert.Equal(20, local.Hour);
        Assert.Equal(42, local.Minute);
    }

    [Fact]
    public void TryParseItemIdLine_NewYearWrapsToPreviousYear()
    {
        var captured = new DateTime(2026, 1, 2, 8, 0, 0, DateTimeKind.Utc);

        Assert.True(AvitoAdPublicationDateParser.TryParseItemIdLine(
            "№ 8252186474, 9 сентября в 15:12",
            captured,
            out _,
            out var published,
            out _));

        var local = AvitoAdBusinessTime.ToLocal(published);
        Assert.Equal(2025, local.Year);
        Assert.Equal(9, local.Month);
        Assert.Equal(9, local.Day);
        Assert.Equal(15, local.Hour);
        Assert.Equal(12, local.Minute);
    }
}
