using Orbita.Api.Helpers;

namespace Orbita.Tests;

public sealed class SubProfileDeserializerTests
{
    [Fact]
    public void Deserialize_ReadsPascalCaseAvitoProfileJson()
    {
        const string json = """
            [
              {
                "Id": "987654",
                "Name": "Служба России 3",
                "Category": "Работа",
                "IsCurrent": true,
                "Balance": 1500
              }
            ]
            """;

        var profiles = SubProfileDeserializer.Deserialize(json);

        Assert.NotNull(profiles);
        Assert.Single(profiles);
        Assert.Equal("987654", profiles[0].Id);
        Assert.Equal("Служба России 3", profiles[0].Name);
        Assert.Equal("Работа", profiles[0].Category);
        Assert.True(profiles[0].IsCurrent);
        Assert.Equal(1500m, profiles[0].Balance);
    }
}