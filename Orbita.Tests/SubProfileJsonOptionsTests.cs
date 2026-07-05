using System.Text.Json;
using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class SubProfileJsonOptionsTests
{
    [Fact]
    public void Deserialize_ReadsPascalCaseSubProfilesJson()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new WorkerSubProfileDto("42", "Служба России 3", "Работа", false, null, null, null, null)
        });

        var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(json, SubProfileJsonOptions.Deserialize);

        Assert.NotNull(profiles);
        Assert.Single(profiles);
        Assert.Equal("42", profiles[0].Id);
        Assert.Equal("Служба России 3", profiles[0].Name);
        Assert.Equal("Работа", profiles[0].Category);
    }

    [Fact]
    public void Deserialize_ReadsCamelCaseSubProfilesJson()
    {
        var json = JsonSerializer.Serialize(
            new[] { new WorkerSubProfileDto("sp-1", "Alpha", "", true, null, null, null, null) },
            SubProfileJsonOptions.Serialize);

        var profiles = JsonSerializer.Deserialize<List<WorkerSubProfileDto>>(json, SubProfileJsonOptions.Deserialize);

        Assert.NotNull(profiles);
        Assert.Single(profiles);
        Assert.Equal("sp-1", profiles[0].Id);
        Assert.Equal("Alpha", profiles[0].Name);
        Assert.True(profiles[0].IsCurrent);
    }
}