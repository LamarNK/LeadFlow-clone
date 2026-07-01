using LeadFlow.Core.Services.Avito;
using Xunit;

namespace LeadFlow.Tests;

public sealed class AvitoChatMessagesJsonTests
{
    [Fact]
    public void Serialize_And_Parse_RoundTrip()
    {
        var messages = AvitoChatMessagesJson.Parse(
            """[{"text":"Привет","at":"2026-06-18T00:09:31","side":"left","isPlatform":false}]""");

        var json = AvitoChatMessagesJson.Serialize(messages);
        var roundTrip = AvitoChatMessagesJson.Parse(json);

        Assert.Single(roundTrip);
        Assert.Equal("Привет", roundTrip[0].Text);
        Assert.Equal("2026-06-18T00:09:31", roundTrip[0].At);
        Assert.Equal("left", roundTrip[0].Side);
    }
}