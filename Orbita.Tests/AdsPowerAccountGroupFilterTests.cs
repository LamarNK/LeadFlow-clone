using Orbita.Contracts;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class AdsPowerAccountGroupFilterTests
{
    [Fact]
    public void Matches_AllGroups_WhenSelectedEmpty()
    {
        Assert.True(AdsPowerAccountGroupFilter.Matches(null, "1001"));
        Assert.True(AdsPowerAccountGroupFilter.Matches("", null));
    }

    [Fact]
    public void Matches_UngroupedSentinel_OnlyEmptyAccountGroup()
    {
        Assert.True(AdsPowerAccountGroupFilter.Matches(AdsPowerAccountGroupFilter.UngroupedValue, null));
        Assert.True(AdsPowerAccountGroupFilter.Matches(AdsPowerAccountGroupFilter.UngroupedValue, "  "));
        Assert.False(AdsPowerAccountGroupFilter.Matches(AdsPowerAccountGroupFilter.UngroupedValue, "1001"));
    }

    [Fact]
    public void Matches_ExactGroupId()
    {
        Assert.True(AdsPowerAccountGroupFilter.Matches("1001", "1001"));
        Assert.False(AdsPowerAccountGroupFilter.Matches("1001", "1002"));
    }

    [Fact]
    public void BuildOptions_IncludesCatalogUngroupedAndSortedNames()
    {
        var options = AdsPowerAccountGroupFilter.BuildOptions(
            [
                (null, null),
                ("1002", "Beta"),
                ("1001", "Alpha")
            ],
            [new AdsPowerGroupDto("1003", "Catalog")]);

        Assert.Equal("", options[0].Value);
        Assert.Equal("Все группы", options[0].Label);
        Assert.Equal(AdsPowerAccountGroupFilter.UngroupedValue, options[1].Value);
        Assert.Equal("Без группы", options[1].Label);
        Assert.Equal(["Alpha", "Beta", "Catalog"], options.Skip(2).Select(o => o.Label).ToArray());
    }
}
