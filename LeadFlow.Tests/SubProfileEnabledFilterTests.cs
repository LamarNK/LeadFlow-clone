using LeadFlow.Core.Models;
using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class SubProfileEnabledFilterTests
{
    private static readonly IReadOnlyList<AvitoSubProfile> Profiles =
    [
        new() { Id = "a", Name = "Alpha" },
        new() { Id = "b", Name = "Beta" },
        new() { Id = "c", Name = "Gamma" }
    ];

    [Fact]
    public void GetEnabled_ReturnsAll_WhenDisabledIdsNull()
    {
        var enabled = SubProfileEnabledFilter.GetEnabled(Profiles, null);

        Assert.Equal(3, enabled.Count);
        Assert.Equal(["a", "b", "c"], enabled.Select(x => x.Id).ToList());
    }

    [Fact]
    public void GetEnabled_ReturnsAll_WhenDisabledIdsEmpty()
    {
        var enabled = SubProfileEnabledFilter.GetEnabled(Profiles, new HashSet<string>());

        Assert.Equal(3, enabled.Count);
    }

    [Fact]
    public void GetEnabled_ExcludesDisabledIds()
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal) { "b" };

        var enabled = SubProfileEnabledFilter.GetEnabled(Profiles, disabled);

        Assert.Equal(2, enabled.Count);
        Assert.Equal(["a", "c"], enabled.Select(x => x.Id).ToList());
    }

    [Fact]
    public void GetEnabled_ReturnsEmptyInput_WhenProfilesEmpty()
    {
        var enabled = SubProfileEnabledFilter.GetEnabled([], new HashSet<string> { "a" });

        Assert.Empty(enabled);
    }
}