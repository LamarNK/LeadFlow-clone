using Orbita.Api.Helpers;
using Orbita.Contracts;
using System.Text.Json;

namespace Orbita.Tests;

public sealed class SubProfilesDisabledIdsHelperTests
{
    [Fact]
    public void Parse_ReturnsEmpty_ForNullOrEmptyJson()
    {
        Assert.Empty(SubProfilesDisabledIdsHelper.Parse(null));
        Assert.Empty(SubProfilesDisabledIdsHelper.Parse(""));
        Assert.Empty(SubProfilesDisabledIdsHelper.Parse("[]"));
    }

    [Fact]
    public void Parse_DeduplicatesAndTrims()
    {
        var parsed = SubProfilesDisabledIdsHelper.Parse("""[" sp-1 ", "sp-2", "sp-1"]""");

        Assert.Equal(["sp-1", "sp-2"], parsed);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var json = SubProfilesDisabledIdsHelper.Serialize(["a", "b"]);
        var parsed = SubProfilesDisabledIdsHelper.Parse(json);

        Assert.Equal(["a", "b"], parsed);
    }

    [Fact]
    public void IsEnabled_RespectsBlacklist()
    {
        var disabled = new List<string> { "sp-2" };

        Assert.True(SubProfilesDisabledIdsHelper.IsEnabled(disabled, "sp-1"));
        Assert.False(SubProfilesDisabledIdsHelper.IsEnabled(disabled, "sp-2"));
    }

    [Fact]
    public void ContainsSubProfile_ReturnsTrue_WhenIdExists()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("42", "Test", "", false, null, null, null, null)
        });

        Assert.True(SubProfilesDisabledIdsHelper.ContainsSubProfile(json, "42"));
        Assert.False(SubProfilesDisabledIdsHelper.ContainsSubProfile(json, "99"));
        Assert.False(SubProfilesDisabledIdsHelper.ContainsSubProfile(json, ""));
    }
}