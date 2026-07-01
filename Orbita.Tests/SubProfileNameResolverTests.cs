using Orbita.Api.Helpers;
using Orbita.Contracts;
using System.Text.Json;

namespace Orbita.Tests;

public sealed class SubProfileNameResolverTests
{
    [Fact]
    public void ResolveName_ReturnsProfileName_WhenIdMatches()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("42", "Служба России 3", "", false, null, null, null, null)
        });

        var name = SubProfileNameResolver.ResolveName(json, "42");

        Assert.Equal("Служба России 3", name);
    }

    [Fact]
    public void ResolveName_ReturnsNull_WhenSubProfileIdMissing()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("42", "Test", "", false, null, null, null, null)
        });

        Assert.Null(SubProfileNameResolver.ResolveName(json, null));
        Assert.Null(SubProfileNameResolver.ResolveName(json, ""));
        Assert.Null(SubProfileNameResolver.ResolveName(json, "99"));
    }

    [Fact]
    public void BuildLookup_MapsAllProfiles()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("a", "Alpha", "", false, null, null, null, null),
            new WorkerSubProfileDto("b", "Beta", "", false, null, null, null, null)
        });
        var accountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var lookup = SubProfileNameResolver.BuildLookup([(accountId, json)]);

        Assert.Equal("Alpha", lookup[(accountId, "a")]);
        Assert.Equal("Beta", lookup[(accountId, "b")]);
    }
}