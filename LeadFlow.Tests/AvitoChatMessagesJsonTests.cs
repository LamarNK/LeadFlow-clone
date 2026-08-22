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

    [Fact]
    public void TryGetResponseAtUtc_PrefersEarliestPlatformMessage()
    {
        var messages = AvitoChatMessagesJson.Parse(
            """
            [
              {"text":"Привет","at":"2026-06-20T12:00:00Z","side":"left","isPlatform":false},
              {"text":"Кандидат откликнулся","at":"2026-06-18T00:09:31Z","side":"left","isPlatform":true},
              {"text":"Ещё","at":"2026-06-19T08:00:00Z","side":"right","isPlatform":false}
            ]
            """);

        var at = AvitoChatMessagesJson.TryGetResponseAtUtc(messages);

        Assert.NotNull(at);
        Assert.Equal(new DateTime(2026, 6, 18, 0, 9, 31, DateTimeKind.Utc), at);
    }

    [Fact]
    public void TryGetResponseAtUtc_IgnoresPlatformMessagesThatAreNotCandidateResponses()
    {
        var messages = AvitoChatMessagesJson.Parse(
            """
            [
              {"text":"Вакансия снята с публикации","at":"2026-06-16T12:00:00Z","side":"left","isPlatform":true},
              {"text":"Кандидат откликнулся на вакансию.","at":"2026-06-18T00:09:31Z","side":"left","isPlatform":true},
              {"text":"Здравствуйте","at":"2026-06-20T12:00:00Z","side":"left","isPlatform":false}
            ]
            """);

        var at = AvitoChatMessagesJson.TryGetResponseAtUtc(messages);

        Assert.Equal(new DateTime(2026, 6, 18, 0, 9, 31, DateTimeKind.Utc), at);
    }

    [Fact]
    public void TryGetResponseAtUtc_FallsBackToEarliestAnyWhenNoPlatform()
    {
        var messages = AvitoChatMessagesJson.Parse(
            """
            [
              {"text":"B","at":"2026-06-20T12:00:00Z","side":"left","isPlatform":false},
              {"text":"A","at":"2026-06-18T00:09:31Z","side":"left","isPlatform":false}
            ]
            """);

        var at = AvitoChatMessagesJson.TryGetResponseAtUtc(messages);

        Assert.NotNull(at);
        Assert.Equal(new DateTime(2026, 6, 18, 0, 9, 31, DateTimeKind.Utc), at);
    }

    [Fact]
    public void TryGetResponseAtUtc_Empty_ReturnsNull()
    {
        Assert.Null(AvitoChatMessagesJson.TryGetResponseAtUtc([]));
    }
}
