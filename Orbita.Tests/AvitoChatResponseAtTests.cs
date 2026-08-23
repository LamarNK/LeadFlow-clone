using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class AvitoChatResponseAtTests
{
    [Fact]
    public void TryGetUtc_PrefersCandidateResponsePlatformMessage()
    {
        var json =
            """
            [
              {"text":"Привет","at":"2026-06-20T12:00:00Z","side":"left","isPlatform":false},
              {"text":"Кандидат откликнулся на вакансию. Его данные сохранились в разделе «Отклики».","at":"2026-08-14T08:15:00Z","side":"left","isPlatform":true},
              {"text":"Ещё","at":"2026-06-19T08:00:00Z","side":"right","isPlatform":false}
            ]
            """;

        var at = AvitoChatResponseAt.TryGetUtc(json);

        Assert.Equal(new DateTime(2026, 8, 14, 8, 15, 0, DateTimeKind.Utc), at);
    }

    [Fact]
    public void TryGetUtc_Empty_ReturnsNull()
    {
        Assert.Null(AvitoChatResponseAt.TryGetUtc((string?)null));
        Assert.Null(AvitoChatResponseAt.TryGetUtc(""));
        Assert.Null(AvitoChatResponseAt.TryGetUtc("[]"));
    }
}
